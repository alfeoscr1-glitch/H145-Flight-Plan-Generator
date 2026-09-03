using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using H145FlightPlanner.Models;

namespace H145FlightPlanner.Services
{
    // This service treats "coastline" as the visible outer land/sea edge.
    // It does not use administrative borders. It downloads detailed OSM edge
    // vectors inside a route-sized view, builds a graph, and traces the edge.
    public class CoastlineGeometryService
    {
        private static readonly HttpClient HttpClient = CreateHttpClient();

        private static readonly string[] OverpassEndpoints =
        {
            "https://overpass-api.de/api/interpreter",
            "https://overpass.private.coffee/api/interpreter",
            "https://maps.mail.ru/osm/tools/overpass/api/interpreter"
        };

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "H145FlightPlanGenerator/1.0");
            client.Timeout = TimeSpan.FromSeconds(60);
            return client;
        }

        public async Task<CoastlineGeometry> GetAroundCoastlineAsync(
            GeographyResult area,
            CancellationToken cancellationToken = default)
        {
            if (area == null)
                throw new ArgumentNullException(nameof(area));

            double south;
            double north;
            double west;
            double east;

            if (area.HasBoundingBox)
            {
                south = area.SouthLatitude;
                north = area.NorthLatitude;
                west = area.WestLongitude;
                east = area.EastLongitude;
            }
            else
            {
                const double fallback = 0.35;
                south = area.Latitude - fallback;
                north = area.Latitude + fallback;
                west = area.Longitude - fallback;
                east = area.Longitude + fallback;
            }

            ExpandBoundingBox(ref south, ref north, ref west, ref east, 0.35, 0.08);

            LandEdgeGraph graph = await DownloadGraphAsync(
                south, north, west, east, cancellationToken);

            List<int> component =
                graph.GetComponentNearest(area.Latitude, area.Longitude);

            if (component.Count < 3)
            {
                throw new InvalidOperationException(
                    $"A detailed outer land edge could not be traced around {area.Name}.");
            }

            List<CoastlinePoint> ordered =
                graph.TryBuildClosedLoop(component, area.Latitude, area.Longitude);

            bool closed = ordered.Count >= 4 &&
                          DistanceNm(ordered[0], ordered[^1]) < 0.02;

            if (!closed)
            {
                // Regions such as counties can have only a section of sea-facing
                // edge inside their bounds. In that case trace the longest detailed
                // edge section rather than inventing an oval or admin-border loop.
                ordered = graph.BuildLongestDetailedPath(component);
            }

            if (ordered.Count < 2)
            {
                throw new InvalidOperationException(
                    $"No usable detailed outer edge was found for {area.Name}.");
            }

            ordered = Densify(ordered, 0.12, 3000, closed);

            return new CoastlineGeometry
            {
                Points = ordered,
                IsClosed = closed,
                SourceDescription = "OpenStreetMap detailed land/sea edge"
            };
        }

        public Task<CoastlineGeometry> GetAlongCoastlineAsync(
            AirportResult departure,
            AirportResult destination,
            CancellationToken cancellationToken = default)
        {
            if (departure == null)
                throw new ArgumentNullException(nameof(departure));
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));

            return GetAlongCoastlineAsync(
                departure.Latitude,
                departure.Longitude,
                destination.Latitude,
                destination.Longitude,
                cancellationToken);
        }

        public async Task<CoastlineGeometry> GetAlongCoastlineAsync(
            double startLatitude,
            double startLongitude,
            double endLatitude,
            double endLongitude,
            CancellationToken cancellationToken = default)
        {
            Exception? lastError = null;

            // Increasing view sizes emulate zooming out until the full requested
            // coastal route is visible and connected, without using screenshots.
            double[] expansionFactors = { 0.20, 0.45, 0.85 };

            foreach (double factor in expansionFactors)
            {
                double south = Math.Min(startLatitude, endLatitude);
                double north = Math.Max(startLatitude, endLatitude);
                double west = Math.Min(startLongitude, endLongitude);
                double east = Math.Max(startLongitude, endLongitude);

                ExpandBoundingBox(
                    ref south, ref north, ref west, ref east, factor, 0.12);

                try
                {
                    LandEdgeGraph graph = await DownloadGraphAsync(
                        south, north, west, east, cancellationToken);

                    List<CoastlinePoint> route =
                        graph.FindBestConnectedPath(
                            startLatitude,
                            startLongitude,
                            endLatitude,
                            endLongitude);

                    if (route.Count >= 2)
                    {
                        route = Densify(route, 0.12, 3000, false);

                        return new CoastlineGeometry
                        {
                            Points = route,
                            IsClosed = false,
                            SourceDescription = "OpenStreetMap detailed land/sea edge"
                        };
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastError = ex;
                }
            }

            throw new InvalidOperationException(
                "A continuous detailed outer land edge could not be traced between the requested locations.",
                lastError);
        }

        private static async Task<LandEdgeGraph> DownloadGraphAsync(
            double south,
            double north,
            double west,
            double east,
            CancellationToken cancellationToken)
        {
            string bbox = string.Format(
                CultureInfo.InvariantCulture,
                "{0},{1},{2},{3}", south, west, north, east);

            string query =
                $"""
                [out:json][timeout:45];
                way["natural"="coastline"]({bbox});
                out geom;
                """;

            var errors = new List<string>();

            foreach (string endpoint in OverpassEndpoints)
            {
                try
                {
                    using var content = new FormUrlEncodedContent(
                        new[] { new KeyValuePair<string, string>("data", query) });

                    using HttpResponseMessage response =
                        await HttpClient.PostAsync(endpoint, content, cancellationToken);

                    if (IsTemporaryFailure(response.StatusCode))
                    {
                        errors.Add($"{endpoint}: {(int)response.StatusCode}");
                        continue;
                    }

                    response.EnsureSuccessStatusCode();

                    string json =
                        await response.Content.ReadAsStringAsync(cancellationToken);

                    LandEdgeGraph graph = ParseGraph(json);
                    if (graph.VertexCount > 1)
                        return graph;
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException)
                {
                    errors.Add($"{endpoint}: {ex.Message}");
                }
            }

            throw new InvalidOperationException(
                "Detailed map-edge data could not be downloaded." +
                (errors.Count == 0 ? string.Empty : "\r\n" + string.Join("\r\n", errors)));
        }

        private static LandEdgeGraph ParseGraph(string json)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            var graph = new LandEdgeGraph();

            if (!document.RootElement.TryGetProperty("elements", out JsonElement elements) ||
                elements.ValueKind != JsonValueKind.Array)
            {
                return graph;
            }

            foreach (JsonElement element in elements.EnumerateArray())
            {
                if (!element.TryGetProperty("geometry", out JsonElement geometry) ||
                    geometry.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                CoastlinePoint? previous = null;

                foreach (JsonElement point in geometry.EnumerateArray())
                {
                    if (!point.TryGetProperty("lat", out JsonElement lat) ||
                        !point.TryGetProperty("lon", out JsonElement lon))
                    {
                        continue;
                    }

                    var current = new CoastlinePoint
                    {
                        Latitude = lat.GetDouble(),
                        Longitude = lon.GetDouble()
                    };

                    if (previous != null)
                        graph.AddEdge(previous, current);

                    previous = current;
                }
            }

            return graph;
        }

        private static List<CoastlinePoint> Densify(
            List<CoastlinePoint> points,
            double maxSegmentNm,
            int maxPoints,
            bool closed)
        {
            if (points.Count < 2)
                return points;

            var result = new List<CoastlinePoint>();

            int segmentCount = closed ? points.Count : points.Count - 1;
            if (closed && DistanceNm(points[0], points[^1]) < 0.02)
                segmentCount = points.Count - 1;

            for (int i = 0; i < segmentCount; i++)
            {
                CoastlinePoint a = points[i];
                CoastlinePoint b = points[(i + 1) % points.Count];

                if (result.Count == 0)
                    result.Add(Clone(a));

                double distance = DistanceNm(a, b);
                int sections = Math.Max(1, (int)Math.Ceiling(distance / maxSegmentNm));

                for (int s = 1; s <= sections; s++)
                {
                    double f = s / (double)sections;
                    result.Add(new CoastlinePoint
                    {
                        Latitude = a.Latitude + (b.Latitude - a.Latitude) * f,
                        Longitude = a.Longitude + (b.Longitude - a.Longitude) * f
                    });
                }
            }

            if (closed && DistanceNm(result[0], result[^1]) >= 0.02)
                result.Add(Clone(result[0]));

            if (result.Count <= maxPoints)
                return result;

            // Keep the complete shape while reducing uniformly only when Little
            // Navmap would otherwise receive an extreme number of points.
            double ratio = result.Count / (double)maxPoints;
            var reduced = new List<CoastlinePoint>(maxPoints + 1);

            for (int i = 0; i < maxPoints; i++)
            {
                int index = Math.Min(result.Count - 1, (int)Math.Floor(i * ratio));
                reduced.Add(Clone(result[index]));
            }

            if (!closed)
                reduced[^1] = Clone(result[^1]);
            else if (DistanceNm(reduced[0], reduced[^1]) >= 0.02)
                reduced.Add(Clone(reduced[0]));

            return reduced;
        }

        private static CoastlinePoint Clone(CoastlinePoint p) =>
            new CoastlinePoint { Latitude = p.Latitude, Longitude = p.Longitude };

        private static void ExpandBoundingBox(
            ref double south,
            ref double north,
            ref double west,
            ref double east,
            double factor,
            double minimumDegrees)
        {
            double latSpan = Math.Max(minimumDegrees, north - south);
            double lonSpan = Math.Max(minimumDegrees, east - west);

            south -= latSpan * factor + minimumDegrees;
            north += latSpan * factor + minimumDegrees;
            west -= lonSpan * factor + minimumDegrees;
            east += lonSpan * factor + minimumDegrees;
        }

        private static bool IsTemporaryFailure(HttpStatusCode statusCode) =>
            statusCode == HttpStatusCode.RequestTimeout ||
            (int)statusCode == 429 ||
            statusCode == HttpStatusCode.BadGateway ||
            statusCode == HttpStatusCode.ServiceUnavailable ||
            statusCode == HttpStatusCode.GatewayTimeout;

        private static double DistanceNm(CoastlinePoint a, CoastlinePoint b) =>
            DistanceNm(a.Latitude, a.Longitude, b.Latitude, b.Longitude);

        private static double DistanceNm(
            double latitude1,
            double longitude1,
            double latitude2,
            double longitude2)
        {
            const double earthRadiusNm = 3440.065;
            double lat1 = latitude1 * Math.PI / 180.0;
            double lat2 = latitude2 * Math.PI / 180.0;
            double dLat = (latitude2 - latitude1) * Math.PI / 180.0;
            double dLon = (longitude2 - longitude1) * Math.PI / 180.0;

            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(lat1) * Math.Cos(lat2) *
                       Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

            return earthRadiusNm * 2.0 *
                   Math.Atan2(Math.Sqrt(a), Math.Sqrt(1.0 - a));
        }

        private sealed class LandEdgeGraph
        {
            private readonly List<Vertex> _vertices = new();
            private readonly Dictionary<string, int> _vertexByKey = new();

            public int VertexCount => _vertices.Count;

            public void AddEdge(CoastlinePoint a, CoastlinePoint b)
            {
                int ia = GetOrAdd(a);
                int ib = GetOrAdd(b);
                if (ia == ib)
                    return;

                double weight = DistanceNm(a, b);
                AddNeighbour(ia, ib, weight);
                AddNeighbour(ib, ia, weight);
            }

            public List<int> GetComponentNearest(double latitude, double longitude)
            {
                int start = FindNearestVertex(latitude, longitude);
                if (start < 0)
                    return new List<int>();

                var result = new List<int>();
                var queue = new Queue<int>();
                var visited = new HashSet<int>();

                queue.Enqueue(start);
                visited.Add(start);

                while (queue.Count > 0)
                {
                    int current = queue.Dequeue();
                    result.Add(current);

                    foreach (Edge edge in _vertices[current].Edges)
                    {
                        if (visited.Add(edge.To))
                            queue.Enqueue(edge.To);
                    }
                }

                return result;
            }

            public List<CoastlinePoint> FindBestConnectedPath(
                double startLatitude,
                double startLongitude,
                double endLatitude,
                double endLongitude)
            {
                int[] starts = FindNearestVertices(startLatitude, startLongitude, 24);
                int[] ends = FindNearestVertices(endLatitude, endLongitude, 24);

                double bestScore = double.MaxValue;
                List<int>? bestPath = null;

                foreach (int start in starts)
                {
                    (double[] dist, int[] previous) = Dijkstra(start);

                    foreach (int end in ends)
                    {
                        if (double.IsInfinity(dist[end]))
                            continue;

                        double joinStart = DistanceNm(
                            startLatitude, startLongitude,
                            _vertices[start].Point.Latitude,
                            _vertices[start].Point.Longitude);

                        double joinEnd = DistanceNm(
                            endLatitude, endLongitude,
                            _vertices[end].Point.Latitude,
                            _vertices[end].Point.Longitude);

                        double score = joinStart + dist[end] + joinEnd;

                        if (score < bestScore)
                        {
                            bestScore = score;
                            bestPath = ReconstructPath(start, end, previous);
                        }
                    }
                }

                if (bestPath == null || bestPath.Count < 2)
                    return new List<CoastlinePoint>();

                return bestPath.Select(i => Clone(_vertices[i].Point)).ToList();
            }

            public List<CoastlinePoint> TryBuildClosedLoop(
                List<int> component,
                double targetLatitude,
                double targetLongitude)
            {
                var componentSet = component.ToHashSet();
                List<int> degreeTwo = component
                    .Where(i => _vertices[i].Edges.Count(e => componentSet.Contains(e.To)) == 2)
                    .ToList();

                if (degreeTwo.Count < 3)
                    return new List<CoastlinePoint>();

                int start = degreeTwo
                    .OrderBy(i => DistanceNm(
                        targetLatitude, targetLongitude,
                        _vertices[i].Point.Latitude,
                        _vertices[i].Point.Longitude))
                    .First();

                var path = new List<int> { start };
                int previous = -1;
                int current = start;

                for (int guard = 0; guard < component.Count + 10; guard++)
                {
                    List<int> neighbours = _vertices[current].Edges
                        .Select(e => e.To)
                        .Where(componentSet.Contains)
                        .Distinct()
                        .ToList();

                    int next = neighbours.FirstOrDefault(n => n != previous, -1);
                    if (next < 0)
                        return new List<CoastlinePoint>();

                    if (next == start)
                    {
                        path.Add(start);
                        return path.Select(i => Clone(_vertices[i].Point)).ToList();
                    }

                    if (path.Contains(next))
                        return new List<CoastlinePoint>();

                    path.Add(next);
                    previous = current;
                    current = next;
                }

                return new List<CoastlinePoint>();
            }

            public List<CoastlinePoint> BuildLongestDetailedPath(List<int> component)
            {
                if (component.Count < 2)
                    return new List<CoastlinePoint>();

                var set = component.ToHashSet();
                List<int> ends = component
                    .Where(i => _vertices[i].Edges.Count(e => set.Contains(e.To)) <= 1)
                    .ToList();

                int start = ends.Count > 0 ? ends[0] : component[0];
                (double[] firstDistances, _) = DijkstraRestricted(start, set);
                int farA = component.OrderByDescending(i => firstDistances[i]).First();

                (double[] secondDistances, int[] previous) = DijkstraRestricted(farA, set);
                int farB = component.OrderByDescending(i => secondDistances[i]).First();

                List<int> path = ReconstructPath(farA, farB, previous);
                return path.Select(i => Clone(_vertices[i].Point)).ToList();
            }

            private int GetOrAdd(CoastlinePoint point)
            {
                string key = Key(point);
                if (_vertexByKey.TryGetValue(key, out int index))
                    return index;

                index = _vertices.Count;
                _vertices.Add(new Vertex { Point = Clone(point) });
                _vertexByKey[key] = index;
                return index;
            }

            private static string Key(CoastlinePoint point) =>
                $"{Math.Round(point.Latitude, 6):F6},{Math.Round(point.Longitude, 6):F6}";

            private void AddNeighbour(int from, int to, double weight)
            {
                if (_vertices[from].Edges.Any(e => e.To == to))
                    return;
                _vertices[from].Edges.Add(new Edge { To = to, Weight = weight });
            }

            private int FindNearestVertex(double latitude, double longitude) =>
                FindNearestVertices(latitude, longitude, 1).FirstOrDefault(-1);

            private int[] FindNearestVertices(double latitude, double longitude, int count) =>
                _vertices
                    .Select((v, i) => new
                    {
                        Index = i,
                        Distance = DistanceNm(
                            latitude, longitude,
                            v.Point.Latitude, v.Point.Longitude)
                    })
                    .OrderBy(x => x.Distance)
                    .Take(Math.Min(count, _vertices.Count))
                    .Select(x => x.Index)
                    .ToArray();

            private (double[] Distances, int[] Previous) Dijkstra(int start)
            {
                var allowed = Enumerable.Range(0, _vertices.Count).ToHashSet();
                return DijkstraRestricted(start, allowed);
            }

            private (double[] Distances, int[] Previous) DijkstraRestricted(
                int start,
                HashSet<int> allowed)
            {
                double[] distance = Enumerable.Repeat(double.PositiveInfinity, _vertices.Count).ToArray();
                int[] previous = Enumerable.Repeat(-1, _vertices.Count).ToArray();
                var queue = new PriorityQueue<int, double>();

                distance[start] = 0;
                queue.Enqueue(start, 0);

                while (queue.Count > 0)
                {
                    queue.TryDequeue(out int current, out double queuedDistance);
                    if (queuedDistance > distance[current])
                        continue;

                    foreach (Edge edge in _vertices[current].Edges)
                    {
                        if (!allowed.Contains(edge.To))
                            continue;

                        double candidate = distance[current] + edge.Weight;
                        if (candidate >= distance[edge.To])
                            continue;

                        distance[edge.To] = candidate;
                        previous[edge.To] = current;
                        queue.Enqueue(edge.To, candidate);
                    }
                }

                return (distance, previous);
            }

            private static List<int> ReconstructPath(int start, int end, int[] previous)
            {
                var path = new List<int>();
                int current = end;

                while (current >= 0)
                {
                    path.Add(current);
                    if (current == start)
                        break;
                    current = previous[current];
                }

                path.Reverse();
                return path.Count > 0 && path[0] == start ? path : new List<int>();
            }

            private sealed class Vertex
            {
                public CoastlinePoint Point { get; set; } = new();
                public List<Edge> Edges { get; set; } = new();
            }

            private sealed class Edge
            {
                public int To { get; set; }
                public double Weight { get; set; }
            }
        }
    }
}

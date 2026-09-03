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
    // High-detail vector coastline tracer.
    //
    // Important design choice: this does NOT use screenshots, Google imagery or
    // administrative borders. Screenshots would be less precise and Google/Bing
    // imagery cannot be silently scraped or redistributed. Instead the service
    // uses the actual OSM land/sea edge vectors, fetches them in overlapping
    // route-sized tiles, joins small data gaps, measures every segment and keeps
    // hundreds/thousands of points when needed.
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
            client.DefaultRequestHeaders.UserAgent.ParseAdd("H145FlightPlanGenerator/2.0");
            client.Timeout = TimeSpan.FromSeconds(75);
            return client;
        }

        public async Task<CoastlineGeometry> GetAroundCoastlineAsync(
            GeographyResult area,
            CancellationToken cancellationToken = default)
        {
            if (area == null)
                throw new ArgumentNullException(nameof(area));

            double south, north, west, east;
            if (area.HasBoundingBox)
            {
                south = area.SouthLatitude;
                north = area.NorthLatitude;
                west = area.WestLongitude;
                east = area.EastLongitude;
            }
            else
            {
                const double fallback = 0.30;
                south = area.Latitude - fallback;
                north = area.Latitude + fallback;
                west = area.Longitude - fallback;
                east = area.Longitude + fallback;
            }

            ExpandBox(ref south, ref north, ref west, ref east, 0.40, 0.08);

            LandEdgeGraph graph = await DownloadTiledGraphAsync(
                south, north, west, east, cancellationToken);

            graph.SnapNearbyEnds(0.10);

            List<int> component = graph.GetComponentNearest(area.Latitude, area.Longitude);
            if (component.Count < 3)
                throw new InvalidOperationException($"No detailed outer edge was found around {area.Name}.");

            List<GraphStep> loop = graph.TryBuildClosedLoop(component, area.Latitude, area.Longitude);

            if (loop.Count < 4)
            {
                // One more conservative repair pass for tiny OSM endpoint gaps.
                graph.SnapNearbyEnds(0.22);
                component = graph.GetComponentNearest(area.Latitude, area.Longitude);
                loop = graph.TryBuildClosedLoop(component, area.Latitude, area.Longitude);
            }

            if (loop.Count < 4)
            {
                throw new InvalidOperationException(
                    $"The real outer edge around {area.Name} was found, but it did not form a complete circuit. " +
                    "The program refused to invent an oval or a fake closing line.");
            }

            List<CoastlinePoint> points = graph.ToOffshorePoints(loop, 0.08);
            points = Densify(points, 0.08, 5000, true);

            return new CoastlineGeometry
            {
                Points = points,
                IsClosed = true,
                SourceDescription = "OSM tiled detailed land/sea edge"
            };
        }

        public Task<CoastlineGeometry> GetAlongCoastlineAsync(
            AirportResult departure,
            AirportResult destination,
            CancellationToken cancellationToken = default)
        {
            return GetAlongCoastlineAsync(
                departure.Latitude, departure.Longitude,
                destination.Latitude, destination.Longitude,
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

            // Progressive view sizes: equivalent to zooming out until the entire
            // requested journey and its connected coast are visible.
            double[] expansions = { 0.18, 0.35, 0.60, 0.95 };

            foreach (double expansion in expansions)
            {
                double south = Math.Min(startLatitude, endLatitude);
                double north = Math.Max(startLatitude, endLatitude);
                double west = Math.Min(startLongitude, endLongitude);
                double east = Math.Max(startLongitude, endLongitude);
                ExpandBox(ref south, ref north, ref west, ref east, expansion, 0.12);

                try
                {
                    LandEdgeGraph graph = await DownloadTiledGraphAsync(
                        south, north, west, east, cancellationToken);

                    // First join only genuine tiny data gaps. Then, if necessary,
                    // allow slightly larger joins but penalise them heavily so real
                    // coastline is always preferred.
                    graph.SnapNearbyEnds(0.10);
                    List<GraphStep> route = graph.FindBestCoastalPath(
                        startLatitude, startLongitude,
                        endLatitude, endLongitude);

                    if (route.Count < 2)
                    {
                        graph.SnapNearbyEnds(0.30);
                        route = graph.FindBestCoastalPath(
                            startLatitude, startLongitude,
                            endLatitude, endLongitude);
                    }

                    if (route.Count >= 2)
                    {
                        List<CoastlinePoint> points = graph.ToOffshorePoints(route, 0.08);
                        points = Densify(points, 0.08, 5000, false);

                        return new CoastlineGeometry
                        {
                            Points = points,
                            IsClosed = false,
                            SourceDescription = "OSM tiled detailed land/sea edge"
                        };
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastError = ex;
                }
            }

            throw new InvalidOperationException(
                "The map edge was downloaded, but a continuous coast-following path could not be constructed between these two requested points. " +
                "No fake straight coastline was inserted.",
                lastError);
        }

        private static async Task<LandEdgeGraph> DownloadTiledGraphAsync(
            double south,
            double north,
            double west,
            double east,
            CancellationToken cancellationToken)
        {
            // Split large requests into overlapping tiles. This avoids asking one
            // Overpass server for a giant Wales-sized response and prevents a
            // single clipped query from losing connectivity.
            double latSpan = Math.Max(0.01, north - south);
            double lonSpan = Math.Max(0.01, east - west);
            int latTiles = Math.Clamp((int)Math.Ceiling(latSpan / 0.55), 1, 8);
            int lonTiles = Math.Clamp((int)Math.Ceiling(lonSpan / 0.70), 1, 8);

            var graph = new LandEdgeGraph();
            var seenWays = new HashSet<long>();

            for (int y = 0; y < latTiles; y++)
            {
                double tileSouth = south + latSpan * y / latTiles;
                double tileNorth = south + latSpan * (y + 1) / latTiles;

                for (int x = 0; x < lonTiles; x++)
                {
                    double tileWest = west + lonSpan * x / lonTiles;
                    double tileEast = west + lonSpan * (x + 1) / lonTiles;

                    double latPad = Math.Max(0.02, (tileNorth - tileSouth) * 0.10);
                    double lonPad = Math.Max(0.02, (tileEast - tileWest) * 0.10);

                    string json = await DownloadTileAsync(
                        tileSouth - latPad,
                        tileNorth + latPad,
                        tileWest - lonPad,
                        tileEast + lonPad,
                        cancellationToken);

                    AddTileToGraph(json, graph, seenWays);
                }
            }

            if (graph.VertexCount < 2)
                throw new InvalidOperationException("No detailed OSM land/sea edge vectors were returned for this route area.");

            return graph;
        }

        private static async Task<string> DownloadTileAsync(
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
                out ids geom;
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
                    return await response.Content.ReadAsStringAsync(cancellationToken);
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException)
                {
                    errors.Add($"{endpoint}: {ex.Message}");
                }
            }

            throw new InvalidOperationException(
                "A coastline map tile could not be downloaded. " + string.Join(" | ", errors));
        }

        private static void AddTileToGraph(
            string json,
            LandEdgeGraph graph,
            HashSet<long> seenWays)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("elements", out JsonElement elements) ||
                elements.ValueKind != JsonValueKind.Array)
                return;

            foreach (JsonElement element in elements.EnumerateArray())
            {
                long wayId = element.TryGetProperty("id", out JsonElement idElement) &&
                             idElement.TryGetInt64(out long id) ? id : 0;

                if (wayId != 0 && !seenWays.Add(wayId))
                    continue;

                if (!element.TryGetProperty("geometry", out JsonElement geometry) ||
                    geometry.ValueKind != JsonValueKind.Array)
                    continue;

                var points = new List<CoastlinePoint>();
                foreach (JsonElement p in geometry.EnumerateArray())
                {
                    if (p.TryGetProperty("lat", out JsonElement lat) &&
                        p.TryGetProperty("lon", out JsonElement lon) &&
                        lat.TryGetDouble(out double la) && lon.TryGetDouble(out double lo))
                    {
                        points.Add(new CoastlinePoint { Latitude = la, Longitude = lo });
                    }
                }

                // OSM coastline direction convention: land is left, sea is right.
                for (int i = 1; i < points.Count; i++)
                    graph.AddCoastSegment(points[i - 1], points[i]);
            }
        }

        private static List<CoastlinePoint> Densify(
            List<CoastlinePoint> points,
            double maxSegmentNm,
            int maxPoints,
            bool closed)
        {
            if (points.Count < 2)
                return points;

            var output = new List<CoastlinePoint> { Clone(points[0]) };
            int end = points.Count;

            for (int i = 1; i < end; i++)
            {
                CoastlinePoint a = points[i - 1];
                CoastlinePoint b = points[i];
                double distance = DistanceNm(a, b);
                int pieces = Math.Max(1, (int)Math.Ceiling(distance / maxSegmentNm));

                for (int p = 1; p <= pieces; p++)
                {
                    double f = p / (double)pieces;
                    output.Add(new CoastlinePoint
                    {
                        Latitude = a.Latitude + (b.Latitude - a.Latitude) * f,
                        Longitude = a.Longitude + (b.Longitude - a.Longitude) * f
                    });
                }
            }

            if (closed && output.Count > 2 && DistanceNm(output[0], output[^1]) > 0.01)
                output.Add(Clone(output[0]));

            if (output.Count <= maxPoints)
                return output;

            int stride = (int)Math.Ceiling(output.Count / (double)maxPoints);
            List<CoastlinePoint> reduced = output.Where((_, i) => i % stride == 0).ToList();
            if (!closed && DistanceNm(reduced[^1], output[^1]) > 0.001)
                reduced.Add(Clone(output[^1]));
            if (closed && DistanceNm(reduced[0], reduced[^1]) > 0.01)
                reduced.Add(Clone(reduced[0]));
            return reduced;
        }

        private static void ExpandBox(
            ref double south,
            ref double north,
            ref double west,
            ref double east,
            double factor,
            double minimum)
        {
            double lat = Math.Max(minimum, north - south);
            double lon = Math.Max(minimum, east - west);
            south -= lat * factor;
            north += lat * factor;
            west -= lon * factor;
            east += lon * factor;
        }

        private static bool IsTemporaryFailure(HttpStatusCode statusCode) =>
            statusCode == HttpStatusCode.RequestTimeout ||
            (int)statusCode == 429 ||
            statusCode == HttpStatusCode.BadGateway ||
            statusCode == HttpStatusCode.ServiceUnavailable ||
            statusCode == HttpStatusCode.GatewayTimeout;

        private static CoastlinePoint Clone(CoastlinePoint p) =>
            new CoastlinePoint { Latitude = p.Latitude, Longitude = p.Longitude };

        private static double DistanceNm(CoastlinePoint a, CoastlinePoint b) =>
            DistanceNm(a.Latitude, a.Longitude, b.Latitude, b.Longitude);

        private static double DistanceNm(double lat1, double lon1, double lat2, double lon2)
        {
            const double radius = 3440.065;
            double p1 = lat1 * Math.PI / 180.0;
            double p2 = lat2 * Math.PI / 180.0;
            double dp = (lat2 - lat1) * Math.PI / 180.0;
            double dl = (lon2 - lon1) * Math.PI / 180.0;
            double a = Math.Sin(dp / 2) * Math.Sin(dp / 2) +
                       Math.Cos(p1) * Math.Cos(p2) *
                       Math.Sin(dl / 2) * Math.Sin(dl / 2);
            return radius * 2.0 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1.0 - a));
        }

        private static double Bearing(double lat1, double lon1, double lat2, double lon2)
        {
            double p1 = lat1 * Math.PI / 180.0;
            double p2 = lat2 * Math.PI / 180.0;
            double dl = (lon2 - lon1) * Math.PI / 180.0;
            double y = Math.Sin(dl) * Math.Cos(p2);
            double x = Math.Cos(p1) * Math.Sin(p2) - Math.Sin(p1) * Math.Cos(p2) * Math.Cos(dl);
            double value = Math.Atan2(y, x) * 180.0 / Math.PI;
            return (value + 360.0) % 360.0;
        }

        private static CoastlinePoint Destination(CoastlinePoint start, double bearingDeg, double distanceNm)
        {
            const double radius = 3440.065;
            double angular = distanceNm / radius;
            double brg = bearingDeg * Math.PI / 180.0;
            double p1 = start.Latitude * Math.PI / 180.0;
            double l1 = start.Longitude * Math.PI / 180.0;
            double p2 = Math.Asin(Math.Sin(p1) * Math.Cos(angular) + Math.Cos(p1) * Math.Sin(angular) * Math.Cos(brg));
            double l2 = l1 + Math.Atan2(Math.Sin(brg) * Math.Sin(angular) * Math.Cos(p1), Math.Cos(angular) - Math.Sin(p1) * Math.Sin(p2));
            return new CoastlinePoint { Latitude = p2 * 180.0 / Math.PI, Longitude = l2 * 180.0 / Math.PI };
        }

        private sealed class LandEdgeGraph
        {
            private readonly List<Vertex> _vertices = new();
            private readonly Dictionary<string, int> _byKey = new();
            public int VertexCount => _vertices.Count;

            public void AddCoastSegment(CoastlinePoint a, CoastlinePoint b)
            {
                int ia = GetOrAdd(a);
                int ib = GetOrAdd(b);
                if (ia == ib)
                    return;

                double length = DistanceNm(a, b);
                double seaBearing = (Bearing(a.Latitude, a.Longitude, b.Latitude, b.Longitude) + 90.0) % 360.0;
                AddEdge(ia, ib, length, seaBearing, false);
                AddEdge(ib, ia, length, seaBearing, false);
            }

            public void SnapNearbyEnds(double maxGapNm)
            {
                List<int> ends = Enumerable.Range(0, _vertices.Count)
                    .Where(i => RealDegree(i) <= 1)
                    .ToList();

                for (int i = 0; i < ends.Count; i++)
                {
                    int a = ends[i];
                    if (RealDegree(a) > 1)
                        continue;

                    int best = -1;
                    double bestDistance = maxGapNm;

                    for (int j = i + 1; j < ends.Count; j++)
                    {
                        int b = ends[j];
                        if (a == b || AreConnected(a, b))
                            continue;

                        double d = DistanceNm(_vertices[a].Point, _vertices[b].Point);
                        if (d < bestDistance)
                        {
                            bestDistance = d;
                            best = b;
                        }
                    }

                    if (best >= 0)
                    {
                        // Bridge is marked and heavily penalised in pathfinding.
                        double seaBearing = Bearing(
                            _vertices[a].Point.Latitude, _vertices[a].Point.Longitude,
                            _vertices[best].Point.Latitude, _vertices[best].Point.Longitude);
                        AddEdge(a, best, bestDistance, seaBearing, true);
                        AddEdge(best, a, bestDistance, seaBearing, true);
                    }
                }
            }

            public List<int> GetComponentNearest(double latitude, double longitude)
            {
                int start = FindNearestVertex(latitude, longitude);
                if (start < 0)
                    return new List<int>();

                var output = new List<int>();
                var queue = new Queue<int>();
                var seen = new HashSet<int> { start };
                queue.Enqueue(start);

                while (queue.Count > 0)
                {
                    int current = queue.Dequeue();
                    output.Add(current);
                    foreach (Edge edge in _vertices[current].Edges)
                    {
                        if (seen.Add(edge.To))
                            queue.Enqueue(edge.To);
                    }
                }

                return output;
            }

            public List<GraphStep> FindBestCoastalPath(
                double startLatitude,
                double startLongitude,
                double endLatitude,
                double endLongitude)
            {
                int[] starts = FindNearestVertices(startLatitude, startLongitude, 36);
                int[] ends = FindNearestVertices(endLatitude, endLongitude, 36);

                double bestScore = double.MaxValue;
                List<GraphStep> best = new();

                foreach (int start in starts)
                {
                    (double[] distance, int[] previous, Edge?[] via) = Dijkstra(start);

                    foreach (int end in ends)
                    {
                        if (double.IsInfinity(distance[end]))
                            continue;

                        double joinStart = DistanceNm(
                            startLatitude, startLongitude,
                            _vertices[start].Point.Latitude, _vertices[start].Point.Longitude);
                        double joinEnd = DistanceNm(
                            endLatitude, endLongitude,
                            _vertices[end].Point.Latitude, _vertices[end].Point.Longitude);

                        double score = joinStart * 1.2 + distance[end] + joinEnd * 1.2;
                        if (score >= bestScore)
                            continue;

                        List<GraphStep> path = Reconstruct(start, end, previous, via);
                        if (path.Count >= 2)
                        {
                            bestScore = score;
                            best = path;
                        }
                    }
                }

                return best;
            }

            public List<GraphStep> TryBuildClosedLoop(
                List<int> component,
                double targetLatitude,
                double targetLongitude)
            {
                if (component.Count < 3)
                    return new List<GraphStep>();

                var set = component.ToHashSet();
                int start = component
                    .OrderBy(i => DistanceNm(
                        targetLatitude, targetLongitude,
                        _vertices[i].Point.Latitude, _vertices[i].Point.Longitude))
                    .First();

                // A proper coastline loop normally has degree 2 throughout. Start
                // at the nearest vertex and walk one direction until returning.
                int previous = -1;
                int current = start;
                var output = new List<GraphStep>
                {
                    new GraphStep { Vertex = start, SeaBearing = 0 }
                };
                var visited = new HashSet<int> { start };

                for (int guard = 0; guard < component.Count + 20; guard++)
                {
                    List<Edge> options = _vertices[current].Edges
                        .Where(e => set.Contains(e.To) && e.To != previous)
                        .OrderBy(e => e.IsBridge)
                        .ThenBy(e => e.Weight)
                        .ToList();

                    if (options.Count == 0)
                        return new List<GraphStep>();

                    Edge chosen = options[0];
                    int next = chosen.To;

                    if (next == start)
                    {
                        output.Add(new GraphStep { Vertex = start, SeaBearing = chosen.SeaBearing });
                        return output;
                    }

                    if (!visited.Add(next))
                        return new List<GraphStep>();

                    output.Add(new GraphStep { Vertex = next, SeaBearing = chosen.SeaBearing });
                    previous = current;
                    current = next;
                }

                return new List<GraphStep>();
            }

            public List<CoastlinePoint> ToOffshorePoints(List<GraphStep> path, double offsetNm)
            {
                var output = new List<CoastlinePoint>();
                if (path.Count == 0)
                    return output;

                for (int i = 0; i < path.Count; i++)
                {
                    Vertex vertex = _vertices[path[i].Vertex];
                    double seaBearing = path[i].SeaBearing;

                    // First point has no incoming edge, so use the next edge's
                    // stored seaward bearing when available.
                    if (i == 0 && path.Count > 1)
                        seaBearing = path[1].SeaBearing;

                    output.Add(Destination(vertex.Point, seaBearing, offsetNm));
                }

                return output;
            }

            private int GetOrAdd(CoastlinePoint point)
            {
                string key = Key(point);
                if (_byKey.TryGetValue(key, out int index))
                    return index;

                index = _vertices.Count;
                _vertices.Add(new Vertex { Point = Clone(point) });
                _byKey[key] = index;
                return index;
            }

            private static string Key(CoastlinePoint p) =>
                $"{Math.Round(p.Latitude, 6):F6},{Math.Round(p.Longitude, 6):F6}";

            private void AddEdge(int from, int to, double weight, double seaBearing, bool bridge)
            {
                if (_vertices[from].Edges.Any(e => e.To == to))
                    return;
                _vertices[from].Edges.Add(new Edge
                {
                    To = to,
                    Weight = weight,
                    SeaBearing = seaBearing,
                    IsBridge = bridge
                });
            }

            private bool AreConnected(int a, int b) =>
                _vertices[a].Edges.Any(e => e.To == b);

            private int RealDegree(int index) =>
                _vertices[index].Edges.Count(e => !e.IsBridge);

            private int FindNearestVertex(double latitude, double longitude)
            {
                int[] values = FindNearestVertices(latitude, longitude, 1);
                return values.Length == 0 ? -1 : values[0];
            }

            private int[] FindNearestVertices(double latitude, double longitude, int count) =>
                _vertices.Select((v, i) => new
                {
                    Index = i,
                    Distance = DistanceNm(latitude, longitude, v.Point.Latitude, v.Point.Longitude)
                })
                .OrderBy(x => x.Distance)
                .Take(Math.Min(count, _vertices.Count))
                .Select(x => x.Index)
                .ToArray();

            private (double[] Distance, int[] Previous, Edge?[] Via) Dijkstra(int start)
            {
                double[] distance = Enumerable.Repeat(double.PositiveInfinity, _vertices.Count).ToArray();
                int[] previous = Enumerable.Repeat(-1, _vertices.Count).ToArray();
                Edge?[] via = new Edge?[_vertices.Count];
                var queue = new PriorityQueue<int, double>();

                distance[start] = 0;
                queue.Enqueue(start, 0);

                while (queue.Count > 0)
                {
                    queue.TryDequeue(out int current, out double queued);
                    if (queued > distance[current])
                        continue;

                    foreach (Edge edge in _vertices[current].Edges)
                    {
                        // Real coastline is cheap. A synthetic micro-gap bridge is
                        // deliberately expensive, so it is used only when needed.
                        double cost = edge.Weight * (edge.IsBridge ? 18.0 : 1.0);
                        double candidate = distance[current] + cost;
                        if (candidate >= distance[edge.To])
                            continue;

                        distance[edge.To] = candidate;
                        previous[edge.To] = current;
                        via[edge.To] = edge;
                        queue.Enqueue(edge.To, candidate);
                    }
                }

                return (distance, previous, via);
            }

            private List<GraphStep> Reconstruct(
                int start,
                int end,
                int[] previous,
                Edge?[] via)
            {
                var reversed = new List<GraphStep>();
                int current = end;

                while (current >= 0)
                {
                    Edge? incoming = via[current];
                    reversed.Add(new GraphStep
                    {
                        Vertex = current,
                        SeaBearing = incoming?.SeaBearing ?? 0
                    });

                    if (current == start)
                        break;
                    current = previous[current];
                }

                reversed.Reverse();
                return reversed.Count > 0 && reversed[0].Vertex == start
                    ? reversed
                    : new List<GraphStep>();
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
                public double SeaBearing { get; set; }
                public bool IsBridge { get; set; }
            }
        }

        private sealed class GraphStep
        {
            public int Vertex { get; set; }
            public double SeaBearing { get; set; }
        }
    }
}

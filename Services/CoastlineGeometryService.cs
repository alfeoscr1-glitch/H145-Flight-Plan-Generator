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
            client.Timeout = TimeSpan.FromSeconds(45);
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
                const double fallbackDegrees = 0.35;
                south = area.Latitude - fallbackDegrees;
                north = area.Latitude + fallbackDegrees;
                west = area.Longitude - fallbackDegrees;
                east = area.Longitude + fallbackDegrees;
            }

            ExpandBoundingBox(
                ref south, ref north, ref west, ref east, 0.10);

            List<CoastlineWay> ways =
                await DownloadCoastlineWaysAsync(
                    south, north, west, east, cancellationToken);

            List<CoastlineChain> chains = BuildChains(ways);

            CoastlineChain? selected =
                chains
                    .Where(x => x.Points.Count >= 3)
                    .OrderByDescending(x => x.IsClosed)
                    .ThenBy(x => DistanceToChainNm(
                        area.Latitude,
                        area.Longitude,
                        x.Points))
                    .ThenByDescending(x => ChainLengthNm(x.Points))
                    .FirstOrDefault();

            if (selected == null)
            {
                throw new InvalidOperationException(
                    "A usable OpenStreetMap coastline could not be identified for this location.");
            }

            List<CoastlinePoint> offshore =
                OffsetSeaward(selected.Points, selected.IsClosed, 0.12);

            List<CoastlinePoint> simplified =
                SimplifyAndLimit(
                    offshore,
                    selected.IsClosed,
                    0.08,
                    0.75,
                    1200);

            return new CoastlineGeometry
            {
                Points = simplified,
                IsClosed = selected.IsClosed,
                SourceDescription = "OpenStreetMap natural=coastline"
            };
        }

        public async Task<CoastlineGeometry> GetAlongCoastlineAsync(
            AirportResult departure,
            AirportResult destination,
            CancellationToken cancellationToken = default)
        {
            if (departure == null)
                throw new ArgumentNullException(nameof(departure));

            if (destination == null)
                throw new ArgumentNullException(nameof(destination));

            double south = Math.Min(
                departure.Latitude, destination.Latitude);
            double north = Math.Max(
                departure.Latitude, destination.Latitude);
            double west = Math.Min(
                departure.Longitude, destination.Longitude);
            double east = Math.Max(
                departure.Longitude, destination.Longitude);

            ExpandBoundingBox(
                ref south, ref north, ref west, ref east, 0.55);

            List<CoastlineWay> ways =
                await DownloadCoastlineWaysAsync(
                    south, north, west, east, cancellationToken);

            List<CoastlineChain> chains = BuildChains(ways);

            CoastlineChain? selected =
                chains
                    .Where(x => x.Points.Count >= 2)
                    .OrderBy(x =>
                        DistanceToChainNm(
                            departure.Latitude,
                            departure.Longitude,
                            x.Points) +
                        DistanceToChainNm(
                            destination.Latitude,
                            destination.Longitude,
                            x.Points))
                    .ThenByDescending(x => ChainLengthNm(x.Points))
                    .FirstOrDefault();

            if (selected == null)
            {
                throw new InvalidOperationException(
                    "A usable OpenStreetMap coastline could not be found between the two airports.");
            }

            List<CoastlinePoint> route =
                ExtractBestSubPath(
                    selected.Points,
                    selected.IsClosed,
                    departure.Latitude,
                    departure.Longitude,
                    destination.Latitude,
                    destination.Longitude);

            if (route.Count < 2)
            {
                throw new InvalidOperationException(
                    "The coastline could not be trimmed into a route between the two airports.");
            }

            List<CoastlinePoint> offshore =
                OffsetSeaward(route, false, 0.12);

            List<CoastlinePoint> simplified =
                SimplifyAndLimit(
                    offshore,
                    false,
                    0.08,
                    0.75,
                    1200);

            return new CoastlineGeometry
            {
                Points = simplified,
                IsClosed = false,
                SourceDescription = "OpenStreetMap natural=coastline"
            };
        }

        private static async Task<List<CoastlineWay>>
            DownloadCoastlineWaysAsync(
                double south,
                double north,
                double west,
                double east,
                CancellationToken cancellationToken)
        {
            string bbox =
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{0},{1},{2},{3}",
                    south,
                    west,
                    north,
                    east);

            string query =
                $"""
                [out:json][timeout:30];
                way["natural"="coastline"]({bbox});
                out geom;
                """;

            var errors = new List<string>();

            foreach (string endpoint in OverpassEndpoints)
            {
                try
                {
                    using var content =
                        new FormUrlEncodedContent(
                            new[]
                            {
                                new KeyValuePair<string, string>(
                                    "data",
                                    query)
                            });

                    using HttpResponseMessage response =
                        await HttpClient.PostAsync(
                            endpoint,
                            content,
                            cancellationToken);

                    if (IsTemporaryFailure(response.StatusCode))
                    {
                        errors.Add(
                            $"{endpoint}: {(int)response.StatusCode} {response.ReasonPhrase}");
                        continue;
                    }

                    response.EnsureSuccessStatusCode();

                    string json =
                        await response.Content.ReadAsStringAsync(
                            cancellationToken);

                    List<CoastlineWay> ways = ParseWays(json);

                    if (ways.Count > 0)
                        return ways;
                }
                catch (OperationCanceledException)
                    when (!cancellationToken.IsCancellationRequested)
                {
                    errors.Add($"{endpoint}: request timed out");
                }
                catch (HttpRequestException ex)
                {
                    errors.Add($"{endpoint}: {ex.Message}");
                }
                catch (JsonException ex)
                {
                    errors.Add($"{endpoint}: invalid response ({ex.Message})");
                }
            }

            throw new InvalidOperationException(
                "OpenStreetMap coastline data could not be downloaded." +
                (errors.Count == 0
                    ? string.Empty
                    : "\r\n\r\n" + string.Join("\r\n", errors)));
        }

        private static List<CoastlineWay> ParseWays(string json)
        {
            using JsonDocument document = JsonDocument.Parse(json);

            var ways = new List<CoastlineWay>();

            if (!document.RootElement.TryGetProperty(
                    "elements",
                    out JsonElement elements) ||
                elements.ValueKind != JsonValueKind.Array)
            {
                return ways;
            }

            foreach (JsonElement element in elements.EnumerateArray())
            {
                if (!element.TryGetProperty(
                        "type",
                        out JsonElement typeElement) ||
                    !string.Equals(
                        typeElement.GetString(),
                        "way",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!element.TryGetProperty(
                        "nodes",
                        out JsonElement nodesElement) ||
                    nodesElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                if (!element.TryGetProperty(
                        "geometry",
                        out JsonElement geometryElement) ||
                    geometryElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                List<long> nodeIds =
                    nodesElement
                        .EnumerateArray()
                        .Where(x => x.ValueKind == JsonValueKind.Number)
                        .Select(x => x.GetInt64())
                        .ToList();

                var points = new List<CoastlinePoint>();

                foreach (JsonElement point in geometryElement.EnumerateArray())
                {
                    if (!point.TryGetProperty(
                            "lat",
                            out JsonElement latElement) ||
                        !point.TryGetProperty(
                            "lon",
                            out JsonElement lonElement))
                    {
                        continue;
                    }

                    points.Add(
                        new CoastlinePoint
                        {
                            Latitude = latElement.GetDouble(),
                            Longitude = lonElement.GetDouble()
                        });
                }

                if (nodeIds.Count >= 2 &&
                    points.Count == nodeIds.Count)
                {
                    ways.Add(
                        new CoastlineWay
                        {
                            NodeIds = nodeIds,
                            Points = points
                        });
                }
            }

            if (ways.Count == 0)
            {
                throw new InvalidOperationException(
                    "No natural=coastline ways were returned by OpenStreetMap.");
            }

            return ways;
        }

        private static List<CoastlineChain> BuildChains(
            List<CoastlineWay> ways)
        {
            var unused = new List<CoastlineWay>(ways);
            var chains = new List<CoastlineChain>();

            while (unused.Count > 0)
            {
                CoastlineWay seed = unused[0];
                unused.RemoveAt(0);

                var nodeIds = new List<long>(seed.NodeIds);
                var points = new List<CoastlinePoint>(seed.Points);

                bool extended;

                do
                {
                    extended = false;

                    long first = nodeIds[0];
                    long last = nodeIds[^1];

                    for (int i = 0; i < unused.Count; i++)
                    {
                        CoastlineWay candidate = unused[i];

                        if (candidate.NodeIds[0] == last)
                        {
                            nodeIds.AddRange(candidate.NodeIds.Skip(1));
                            points.AddRange(candidate.Points.Skip(1));
                            unused.RemoveAt(i);
                            extended = true;
                            break;
                        }

                        if (candidate.NodeIds[^1] == first)
                        {
                            nodeIds.InsertRange(
                                0,
                                candidate.NodeIds.Take(
                                    candidate.NodeIds.Count - 1));

                            points.InsertRange(
                                0,
                                candidate.Points.Take(
                                    candidate.Points.Count - 1));

                            unused.RemoveAt(i);
                            extended = true;
                            break;
                        }
                    }
                }
                while (extended);

                chains.Add(
                    new CoastlineChain
                    {
                        NodeIds = nodeIds,
                        Points = points,
                        IsClosed =
                            nodeIds.Count > 2 &&
                            nodeIds[0] == nodeIds[^1]
                    });
            }

            return chains;
        }

        private static List<CoastlinePoint> ExtractBestSubPath(
            List<CoastlinePoint> points,
            bool isClosed,
            double startLatitude,
            double startLongitude,
            double endLatitude,
            double endLongitude)
        {
            int startIndex =
                FindNearestPointIndex(
                    points,
                    startLatitude,
                    startLongitude);

            int endIndex =
                FindNearestPointIndex(
                    points,
                    endLatitude,
                    endLongitude);

            if (startIndex < 0 ||
                endIndex < 0 ||
                startIndex == endIndex)
            {
                return new List<CoastlinePoint>();
            }

            if (!isClosed)
            {
                if (startIndex <= endIndex)
                {
                    return points
                        .Skip(startIndex)
                        .Take(endIndex - startIndex + 1)
                        .ToList();
                }

                return points
                    .Skip(endIndex)
                    .Take(startIndex - endIndex + 1)
                    .Reverse()
                    .ToList();
            }

            List<CoastlinePoint> forward =
                CircularSlice(
                    points,
                    startIndex,
                    endIndex,
                    1);

            List<CoastlinePoint> backward =
                CircularSlice(
                    points,
                    startIndex,
                    endIndex,
                    -1);

            return ChainLengthNm(forward) <= ChainLengthNm(backward)
                ? forward
                : backward;
        }

        private static List<CoastlinePoint> CircularSlice(
            List<CoastlinePoint> points,
            int startIndex,
            int endIndex,
            int step)
        {
            var result = new List<CoastlinePoint>();
            int index = startIndex;

            for (int guard = 0; guard <= points.Count + 1; guard++)
            {
                result.Add(points[index]);

                if (index == endIndex)
                    break;

                index = (index + step + points.Count) % points.Count;
            }

            return result;
        }

        private static List<CoastlinePoint> OffsetSeaward(
            List<CoastlinePoint> points,
            bool isClosed,
            double offsetNm)
        {
            if (points.Count < 2)
                return new List<CoastlinePoint>(points);

            var result = new List<CoastlinePoint>(points.Count + 1);

            for (int i = 0; i < points.Count; i++)
            {
                CoastlinePoint previous =
                    points[
                        i == 0
                            ? (isClosed
                                ? Math.Max(0, points.Count - 2)
                                : 0)
                            : i - 1];

                CoastlinePoint current = points[i];

                CoastlinePoint next =
                    points[
                        i == points.Count - 1
                            ? (isClosed
                                ? Math.Min(1, points.Count - 1)
                                : points.Count - 1)
                            : i + 1];

                double bearing =
                    BearingDegrees(
                        previous.Latitude,
                        previous.Longitude,
                        next.Latitude,
                        next.Longitude);

                // OSM natural=coastline convention:
                // land is on the left, sea is on the right.
                result.Add(
                    DestinationPoint(
                        current,
                        NormalizeBearing(bearing + 90.0),
                        offsetNm));
            }

            if (isClosed && result.Count > 2)
            {
                if (DistanceNm(
                        result[0].Latitude,
                        result[0].Longitude,
                        result[^1].Latitude,
                        result[^1].Longitude) > 0.01)
                {
                    result.Add(
                        new CoastlinePoint
                        {
                            Latitude = result[0].Latitude,
                            Longitude = result[0].Longitude
                        });
                }
            }

            return result;
        }

        private static List<CoastlinePoint> SimplifyAndLimit(
            List<CoastlinePoint> points,
            bool isClosed,
            double toleranceNm,
            double maxSegmentNm,
            int maxPoints)
        {
            if (points.Count <= 2)
                return new List<CoastlinePoint>(points);

            bool closedInput =
                isClosed &&
                DistanceNm(
                    points[0].Latitude,
                    points[0].Longitude,
                    points[^1].Latitude,
                    points[^1].Longitude) < 0.02;

            List<CoastlinePoint> working =
                closedInput
                    ? points.Take(points.Count - 1).ToList()
                    : new List<CoastlinePoint>(points);

            List<CoastlinePoint> simplified =
                RamerDouglasPeucker(working, toleranceNm);

            simplified =
                EnforceMaximumSegmentLength(
                    simplified,
                    maxSegmentNm);

            if (simplified.Count > maxPoints)
            {
                int step =
                    (int)Math.Ceiling(
                        simplified.Count /
                        (double)maxPoints);

                List<CoastlinePoint> reduced =
                    simplified
                        .Where((_, index) => index % step == 0)
                        .ToList();

                if (DistanceNm(
                        reduced[^1].Latitude,
                        reduced[^1].Longitude,
                        simplified[^1].Latitude,
                        simplified[^1].Longitude) > 0.001)
                {
                    reduced.Add(simplified[^1]);
                }

                simplified = reduced;
            }

            if (isClosed && simplified.Count > 2)
            {
                if (DistanceNm(
                        simplified[0].Latitude,
                        simplified[0].Longitude,
                        simplified[^1].Latitude,
                        simplified[^1].Longitude) > 0.01)
                {
                    simplified.Add(
                        new CoastlinePoint
                        {
                            Latitude = simplified[0].Latitude,
                            Longitude = simplified[0].Longitude
                        });
                }
            }

            return simplified;
        }

        private static List<CoastlinePoint> RamerDouglasPeucker(
            List<CoastlinePoint> points,
            double toleranceNm)
        {
            if (points.Count <= 2)
                return new List<CoastlinePoint>(points);

            int index = -1;
            double maximumDistance = 0;

            CoastlinePoint start = points[0];
            CoastlinePoint end = points[^1];

            for (int i = 1; i < points.Count - 1; i++)
            {
                double distance =
                    PerpendicularDistanceNm(
                        points[i],
                        start,
                        end);

                if (distance > maximumDistance)
                {
                    maximumDistance = distance;
                    index = i;
                }
            }

            if (index >= 0 && maximumDistance > toleranceNm)
            {
                List<CoastlinePoint> firstPart =
                    RamerDouglasPeucker(
                        points.Take(index + 1).ToList(),
                        toleranceNm);

                List<CoastlinePoint> secondPart =
                    RamerDouglasPeucker(
                        points.Skip(index).ToList(),
                        toleranceNm);

                return firstPart
                    .Take(firstPart.Count - 1)
                    .Concat(secondPart)
                    .ToList();
            }

            return new List<CoastlinePoint> { start, end };
        }

        private static List<CoastlinePoint> EnforceMaximumSegmentLength(
            List<CoastlinePoint> points,
            double maxSegmentNm)
        {
            if (points.Count <= 1)
                return new List<CoastlinePoint>(points);

            var result = new List<CoastlinePoint> { points[0] };

            for (int i = 1; i < points.Count; i++)
            {
                CoastlinePoint start = points[i - 1];
                CoastlinePoint end = points[i];

                double distance =
                    DistanceNm(
                        start.Latitude,
                        start.Longitude,
                        end.Latitude,
                        end.Longitude);

                int sections =
                    Math.Max(
                        1,
                        (int)Math.Ceiling(
                            distance / maxSegmentNm));

                for (int section = 1; section <= sections; section++)
                {
                    double fraction =
                        section / (double)sections;

                    result.Add(
                        new CoastlinePoint
                        {
                            Latitude =
                                start.Latitude +
                                (end.Latitude - start.Latitude) * fraction,

                            Longitude =
                                start.Longitude +
                                (end.Longitude - start.Longitude) * fraction
                        });
                }
            }

            return result;
        }

        private static int FindNearestPointIndex(
            List<CoastlinePoint> points,
            double latitude,
            double longitude)
        {
            int bestIndex = -1;
            double bestDistance = double.MaxValue;

            for (int i = 0; i < points.Count; i++)
            {
                double distance =
                    DistanceNm(
                        latitude,
                        longitude,
                        points[i].Latitude,
                        points[i].Longitude);

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = i;
                }
            }

            return bestIndex;
        }

        private static double DistanceToChainNm(
            double latitude,
            double longitude,
            List<CoastlinePoint> points)
        {
            double best = double.MaxValue;

            foreach (CoastlinePoint point in points)
            {
                best =
                    Math.Min(
                        best,
                        DistanceNm(
                            latitude,
                            longitude,
                            point.Latitude,
                            point.Longitude));
            }

            return best;
        }

        private static double ChainLengthNm(
            List<CoastlinePoint> points)
        {
            double total = 0;

            for (int i = 1; i < points.Count; i++)
            {
                total +=
                    DistanceNm(
                        points[i - 1].Latitude,
                        points[i - 1].Longitude,
                        points[i].Latitude,
                        points[i].Longitude);
            }

            return total;
        }

        private static double PerpendicularDistanceNm(
            CoastlinePoint point,
            CoastlinePoint lineStart,
            CoastlinePoint lineEnd)
        {
            double referenceLatitude =
                (lineStart.Latitude +
                 lineEnd.Latitude +
                 point.Latitude) / 3.0;

            double cos =
                Math.Cos(
                    referenceLatitude *
                    Math.PI / 180.0);

            double x = point.Longitude * 60.0 * cos;
            double y = point.Latitude * 60.0;
            double x1 = lineStart.Longitude * 60.0 * cos;
            double y1 = lineStart.Latitude * 60.0;
            double x2 = lineEnd.Longitude * 60.0 * cos;
            double y2 = lineEnd.Latitude * 60.0;

            double dx = x2 - x1;
            double dy = y2 - y1;

            if (Math.Abs(dx) < 0.0000001 &&
                Math.Abs(dy) < 0.0000001)
            {
                return Math.Sqrt(
                    Math.Pow(x - x1, 2) +
                    Math.Pow(y - y1, 2));
            }

            double t =
                ((x - x1) * dx +
                 (y - y1) * dy) /
                (dx * dx + dy * dy);

            t = Math.Max(0, Math.Min(1, t));

            double nearestX = x1 + t * dx;
            double nearestY = y1 + t * dy;

            return Math.Sqrt(
                Math.Pow(x - nearestX, 2) +
                Math.Pow(y - nearestY, 2));
        }

        private static CoastlinePoint DestinationPoint(
            CoastlinePoint origin,
            double bearingDegrees,
            double distanceNm)
        {
            const double EarthRadiusNm = 3440.065;

            double angularDistance = distanceNm / EarthRadiusNm;
            double bearing = bearingDegrees * Math.PI / 180.0;
            double latitude1 = origin.Latitude * Math.PI / 180.0;
            double longitude1 = origin.Longitude * Math.PI / 180.0;

            double latitude2 =
                Math.Asin(
                    Math.Sin(latitude1) *
                    Math.Cos(angularDistance) +
                    Math.Cos(latitude1) *
                    Math.Sin(angularDistance) *
                    Math.Cos(bearing));

            double longitude2 =
                longitude1 +
                Math.Atan2(
                    Math.Sin(bearing) *
                    Math.Sin(angularDistance) *
                    Math.Cos(latitude1),
                    Math.Cos(angularDistance) -
                    Math.Sin(latitude1) *
                    Math.Sin(latitude2));

            return new CoastlinePoint
            {
                Latitude = latitude2 * 180.0 / Math.PI,
                Longitude = longitude2 * 180.0 / Math.PI
            };
        }

        private static double BearingDegrees(
            double latitude1,
            double longitude1,
            double latitude2,
            double longitude2)
        {
            double lat1 = latitude1 * Math.PI / 180.0;
            double lat2 = latitude2 * Math.PI / 180.0;
            double deltaLon =
                (longitude2 - longitude1) *
                Math.PI / 180.0;

            double y =
                Math.Sin(deltaLon) *
                Math.Cos(lat2);

            double x =
                Math.Cos(lat1) *
                Math.Sin(lat2) -
                Math.Sin(lat1) *
                Math.Cos(lat2) *
                Math.Cos(deltaLon);

            return NormalizeBearing(
                Math.Atan2(y, x) *
                180.0 / Math.PI);
        }

        private static double NormalizeBearing(double bearing)
        {
            bearing %= 360.0;
            if (bearing < 0)
                bearing += 360.0;
            return bearing;
        }

        private static double DistanceNm(
            double latitude1,
            double longitude1,
            double latitude2,
            double longitude2)
        {
            const double EarthRadiusNm = 3440.065;

            double lat1 = latitude1 * Math.PI / 180.0;
            double lat2 = latitude2 * Math.PI / 180.0;
            double deltaLat =
                (latitude2 - latitude1) *
                Math.PI / 180.0;
            double deltaLon =
                (longitude2 - longitude1) *
                Math.PI / 180.0;

            double a =
                Math.Sin(deltaLat / 2.0) *
                Math.Sin(deltaLat / 2.0) +
                Math.Cos(lat1) *
                Math.Cos(lat2) *
                Math.Sin(deltaLon / 2.0) *
                Math.Sin(deltaLon / 2.0);

            double c =
                2.0 *
                Math.Atan2(
                    Math.Sqrt(a),
                    Math.Sqrt(1.0 - a));

            return EarthRadiusNm * c;
        }

        private static void ExpandBoundingBox(
            ref double south,
            ref double north,
            ref double west,
            ref double east,
            double factor)
        {
            double latitudeSpan =
                Math.Max(0.05, north - south);

            double longitudeSpan =
                Math.Max(0.05, east - west);

            south -= latitudeSpan * factor;
            north += latitudeSpan * factor;
            west -= longitudeSpan * factor;
            east += longitudeSpan * factor;
        }

        private static bool IsTemporaryFailure(HttpStatusCode statusCode)
        {
            return statusCode == HttpStatusCode.RequestTimeout ||
                   (int)statusCode == 429 ||
                   statusCode == HttpStatusCode.BadGateway ||
                   statusCode == HttpStatusCode.ServiceUnavailable ||
                   statusCode == HttpStatusCode.GatewayTimeout;
        }

        private sealed class CoastlineWay
        {
            public List<long> NodeIds { get; set; } = new();
            public List<CoastlinePoint> Points { get; set; } = new();
        }

        private sealed class CoastlineChain
        {
            public List<long> NodeIds { get; set; } = new();
            public List<CoastlinePoint> Points { get; set; } = new();
            public bool IsClosed { get; set; }
        }
    }
}

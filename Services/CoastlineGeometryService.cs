using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using H145FlightPlanner.Models;

namespace H145FlightPlanner.Services
{
    public class CoastlineGeometryService
    {
        private static readonly HttpClient HttpClient =
            CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient();

            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "H145FlightPlanGenerator/1.0");

            client.Timeout =
                TimeSpan.FromSeconds(45);

            return client;
        }

        // =====================================================
        // AROUND
        // =====================================================

        public async Task<CoastlineGeometry> GetAroundCoastlineAsync(
            GeographyResult area,
            CancellationToken cancellationToken = default)
        {
            if (area == null)
                throw new ArgumentNullException(nameof(area));

            JsonElement? geoJson =
                await GetExactPlaceGeometryAsync(
                    area,
                    cancellationToken);

            if (geoJson == null)
            {
                throw new InvalidOperationException(
                    $"The detailed outline for {area.Name} could not be found.");
            }

            List<List<CoastlinePoint>> outerRings =
                ExtractOuterRings(
                    geoJson.Value);

            if (outerRings.Count == 0)
            {
                throw new InvalidOperationException(
                    $"No usable outer outline was found for {area.Name}.");
            }

            List<CoastlinePoint> outline =
                SelectBestRing(
                    outerRings,
                    area.Latitude,
                    area.Longitude);

            if (outline.Count < 3)
            {
                throw new InvalidOperationException(
                    $"The outline for {area.Name} was not detailed enough.");
            }

            EnsureClosed(outline);

            // Put the helicopter just outside the land boundary.
            List<CoastlinePoint> outsideOutline =
                OffsetOutsidePolygon(
                    outline,
                    0.12);

            // Keep detail around bends/headlands while avoiding
            // thousands of unnecessary points on straight edges.
            List<CoastlinePoint> simplified =
                SimplifyClosedOutline(
                    outsideOutline,
                    0.06,
                    0.65,
                    1400);

            return new CoastlineGeometry
            {
                Points = simplified,
                IsClosed = true,
                SourceDescription =
                    "OpenStreetMap place outline geometry"
            };
        }

        // =====================================================
        // ALONG
        // =====================================================

        public async Task<CoastlineGeometry> GetAlongCoastlineAsync(
            AirportResult departure,
            AirportResult destination,
            CancellationToken cancellationToken = default)
        {
            if (departure == null)
                throw new ArgumentNullException(nameof(departure));

            if (destination == null)
                throw new ArgumentNullException(nameof(destination));

            string? regionName =
                await FindCommonRegionAsync(
                    departure.Latitude,
                    departure.Longitude,
                    destination.Latitude,
                    destination.Longitude,
                    cancellationToken);

            if (string.IsNullOrWhiteSpace(regionName))
            {
                throw new InvalidOperationException(
                    "A common geographic area could not be identified " +
                    "between the departure and destination.");
            }

            JsonElement? regionGeometry =
                await SearchPlaceGeometryAsync(
                    regionName,
                    cancellationToken);

            if (regionGeometry == null)
            {
                throw new InvalidOperationException(
                    $"The outer layout of {regionName} could not be found.");
            }

            List<List<CoastlinePoint>> rings =
                ExtractOuterRings(
                    regionGeometry.Value);

            if (rings.Count == 0)
            {
                throw new InvalidOperationException(
                    $"No usable outer layout was found for {regionName}.");
            }

            double middleLatitude =
                (departure.Latitude +
                 destination.Latitude) /
                2.0;

            double middleLongitude =
                (departure.Longitude +
                 destination.Longitude) /
                2.0;

            List<CoastlinePoint> outline =
                SelectBestRing(
                    rings,
                    middleLatitude,
                    middleLongitude);

            EnsureClosed(outline);

            List<CoastlinePoint> route =
                ExtractBestBoundarySection(
                    outline,
                    departure.Latitude,
                    departure.Longitude,
                    destination.Latitude,
                    destination.Longitude);

            if (route.Count < 2)
            {
                throw new InvalidOperationException(
                    "A usable outer-edge route could not be created.");
            }

            List<CoastlinePoint> outsideRoute =
                OffsetOutsideOpenSection(
                    route,
                    outline,
                    0.12);

            List<CoastlinePoint> simplified =
                SimplifyOpenOutline(
                    outsideRoute,
                    0.06,
                    0.65,
                    1400);

            return new CoastlineGeometry
            {
                Points = simplified,
                IsClosed = false,
                SourceDescription =
                    $"OpenStreetMap outer layout of {regionName}"
            };
        }

        // =====================================================
        // EXACT PLACE GEOMETRY
        // =====================================================

        private static async Task<JsonElement?>
            GetExactPlaceGeometryAsync(
                GeographyResult area,
                CancellationToken cancellationToken)
        {
            string prefix =
                area.OsmType.ToLowerInvariant() switch
                {
                    "relation" => "R",
                    "way" => "W",
                    "node" => "N",
                    _ => string.Empty
                };

            if (!string.IsNullOrWhiteSpace(prefix) &&
                area.OsmId > 0)
            {
                string osmId =
                    $"{prefix}{area.OsmId}";

                string lookupUrl =
                    "https://nominatim.openstreetmap.org/lookup" +
                    $"?osm_ids={Uri.EscapeDataString(osmId)}" +
                    "&format=jsonv2" +
                    "&polygon_geojson=1";

                JsonElement? lookupGeometry =
                    await DownloadFirstGeometryAsync(
                        lookupUrl,
                        cancellationToken);

                if (lookupGeometry != null)
                    return lookupGeometry;
            }

            if (!string.IsNullOrWhiteSpace(area.Name))
            {
                JsonElement? searchGeometry =
                    await SearchPlaceGeometryAsync(
                        area.Name,
                        cancellationToken);

                if (searchGeometry != null)
                    return searchGeometry;
            }

            if (!string.IsNullOrWhiteSpace(area.DisplayName))
            {
                return await SearchPlaceGeometryAsync(
                    area.DisplayName,
                    cancellationToken);
            }

            return null;
        }

        private static async Task<JsonElement?>
            SearchPlaceGeometryAsync(
                string placeName,
                CancellationToken cancellationToken)
        {
            string query =
                Uri.EscapeDataString(
                    placeName);

            string url =
                "https://nominatim.openstreetmap.org/search" +
                $"?q={query}" +
                "&format=jsonv2" +
                "&limit=10" +
                "&dedupe=0" +
                "&namedetails=1" +
                "&polygon_geojson=1" +
                "&countrycodes=gb,im,ie,gg,je";

            using HttpResponseMessage response =
                await HttpClient.GetAsync(
                    url,
                    cancellationToken);

            response.EnsureSuccessStatusCode();

            string json =
                await response.Content.ReadAsStringAsync(
                    cancellationToken);

            using JsonDocument document =
                JsonDocument.Parse(json);

            if (document.RootElement.ValueKind !=
                JsonValueKind.Array)
            {
                return null;
            }

            foreach (JsonElement result
                in document.RootElement.EnumerateArray())
            {
                if (!result.TryGetProperty(
                    "geojson",
                    out JsonElement geoJson))
                {
                    continue;
                }

                if (IsPolygonGeometry(geoJson))
                {
                    return geoJson.Clone();
                }
            }

            return null;
        }

        private static async Task<JsonElement?>
            DownloadFirstGeometryAsync(
                string url,
                CancellationToken cancellationToken)
        {
            using HttpResponseMessage response =
                await HttpClient.GetAsync(
                    url,
                    cancellationToken);

            response.EnsureSuccessStatusCode();

            string json =
                await response.Content.ReadAsStringAsync(
                    cancellationToken);

            using JsonDocument document =
                JsonDocument.Parse(json);

            if (document.RootElement.ValueKind !=
                    JsonValueKind.Array ||
                document.RootElement.GetArrayLength() == 0)
            {
                return null;
            }

            foreach (JsonElement result
                in document.RootElement.EnumerateArray())
            {
                if (result.TryGetProperty(
                        "geojson",
                        out JsonElement geoJson) &&
                    IsPolygonGeometry(geoJson))
                {
                    return geoJson.Clone();
                }
            }

            return null;
        }

        private static bool IsPolygonGeometry(
            JsonElement geoJson)
        {
            if (!geoJson.TryGetProperty(
                "type",
                out JsonElement typeElement))
            {
                return false;
            }

            string type =
                typeElement.GetString() ??
                string.Empty;

            return
                string.Equals(
                    type,
                    "Polygon",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    type,
                    "MultiPolygon",
                    StringComparison.OrdinalIgnoreCase);
        }

        // =====================================================
        // REGION LOOKUP FOR "ALONG"
        // =====================================================

        private static async Task<string?>
            FindCommonRegionAsync(
                double startLatitude,
                double startLongitude,
                double endLatitude,
                double endLongitude,
                CancellationToken cancellationToken)
        {
            Dictionary<string, string> start =
                await ReverseAddressAsync(
                    startLatitude,
                    startLongitude,
                    cancellationToken);

            Dictionary<string, string> end =
                await ReverseAddressAsync(
                    endLatitude,
                    endLongitude,
                    cancellationToken);

            string[] preferredLevels =
            {
                "state",
                "region",
                "province",
                "state_district",
                "country"
            };

            foreach (string level
                in preferredLevels)
            {
                if (!start.TryGetValue(
                        level,
                        out string? startValue) ||
                    !end.TryGetValue(
                        level,
                        out string? endValue))
                {
                    continue;
                }

                if (string.Equals(
                    startValue,
                    endValue,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return startValue;
                }
            }

            if (start.TryGetValue(
                    "country",
                    out string? startCountry) &&
                end.TryGetValue(
                    "country",
                    out string? endCountry) &&
                string.Equals(
                    startCountry,
                    endCountry,
                    StringComparison.OrdinalIgnoreCase))
            {
                return startCountry;
            }

            return null;
        }

        private static async Task<Dictionary<string, string>>
            ReverseAddressAsync(
                double latitude,
                double longitude,
                CancellationToken cancellationToken)
        {
            string lat =
                latitude.ToString(
                    CultureInfo.InvariantCulture);

            string lon =
                longitude.ToString(
                    CultureInfo.InvariantCulture);

            string url =
                "https://nominatim.openstreetmap.org/reverse" +
                $"?lat={lat}" +
                $"&lon={lon}" +
                "&format=jsonv2" +
                "&zoom=10" +
                "&addressdetails=1";

            using HttpResponseMessage response =
                await HttpClient.GetAsync(
                    url,
                    cancellationToken);

            response.EnsureSuccessStatusCode();

            string json =
                await response.Content.ReadAsStringAsync(
                    cancellationToken);

            using JsonDocument document =
                JsonDocument.Parse(json);

            var address =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);

            if (!document.RootElement.TryGetProperty(
                "address",
                out JsonElement addressElement) ||
                addressElement.ValueKind !=
                    JsonValueKind.Object)
            {
                return address;
            }

            foreach (JsonProperty property
                in addressElement.EnumerateObject())
            {
                if (property.Value.ValueKind !=
                    JsonValueKind.String)
                {
                    continue;
                }

                string? value =
                    property.Value.GetString();

                if (!string.IsNullOrWhiteSpace(value))
                {
                    address[property.Name] =
                        value;
                }
            }

            return address;
        }

        // =====================================================
        // GEOJSON OUTER RINGS
        // =====================================================

        private static List<List<CoastlinePoint>>
            ExtractOuterRings(
                JsonElement geoJson)
        {
            var rings =
                new List<List<CoastlinePoint>>();

            if (!geoJson.TryGetProperty(
                    "type",
                    out JsonElement typeElement) ||
                !geoJson.TryGetProperty(
                    "coordinates",
                    out JsonElement coordinates))
            {
                return rings;
            }

            string type =
                typeElement.GetString() ??
                string.Empty;

            if (string.Equals(
                type,
                "Polygon",
                StringComparison.OrdinalIgnoreCase))
            {
                if (coordinates.ValueKind ==
                        JsonValueKind.Array &&
                    coordinates.GetArrayLength() > 0)
                {
                    List<CoastlinePoint> ring =
                        ReadRing(
                            coordinates[0]);

                    if (ring.Count >= 3)
                        rings.Add(ring);
                }
            }
            else if (string.Equals(
                type,
                "MultiPolygon",
                StringComparison.OrdinalIgnoreCase))
            {
                foreach (JsonElement polygon
                    in coordinates.EnumerateArray())
                {
                    if (polygon.ValueKind !=
                            JsonValueKind.Array ||
                        polygon.GetArrayLength() == 0)
                    {
                        continue;
                    }

                    List<CoastlinePoint> ring =
                        ReadRing(
                            polygon[0]);

                    if (ring.Count >= 3)
                        rings.Add(ring);
                }
            }

            return rings;
        }

        private static List<CoastlinePoint> ReadRing(
            JsonElement ringElement)
        {
            var result =
                new List<CoastlinePoint>();

            if (ringElement.ValueKind !=
                JsonValueKind.Array)
            {
                return result;
            }

            foreach (JsonElement coordinate
                in ringElement.EnumerateArray())
            {
                if (coordinate.ValueKind !=
                        JsonValueKind.Array ||
                    coordinate.GetArrayLength() < 2 ||
                    coordinate[0].ValueKind !=
                        JsonValueKind.Number ||
                    coordinate[1].ValueKind !=
                        JsonValueKind.Number)
                {
                    continue;
                }

                result.Add(
                    new CoastlinePoint
                    {
                        Longitude =
                            coordinate[0].GetDouble(),

                        Latitude =
                            coordinate[1].GetDouble()
                    });
            }

            return result;
        }

        private static List<CoastlinePoint> SelectBestRing(
            List<List<CoastlinePoint>> rings,
            double targetLatitude,
            double targetLongitude)
        {
            return rings
                .OrderBy(ring =>
                    DistanceToOutlineNm(
                        targetLatitude,
                        targetLongitude,
                        ring))
                .ThenByDescending(ring =>
                    AbsolutePolygonArea(ring))
                .First();
        }

        // =====================================================
        // OUTSIDE OFFSET
        // =====================================================

        private static List<CoastlinePoint>
            OffsetOutsidePolygon(
                List<CoastlinePoint> polygon,
                double offsetNm)
        {
            List<CoastlinePoint> working =
                RemoveDuplicateClosure(
                    polygon);

            if (working.Count < 3)
                return polygon;

            bool counterClockwise =
                SignedPolygonArea(
                    working) >
                0;

            var result =
                new List<CoastlinePoint>(
                    working.Count + 1);

            for (int i = 0;
                 i < working.Count;
                 i++)
            {
                CoastlinePoint previous =
                    working[
                        (i - 1 +
                         working.Count) %
                        working.Count];

                CoastlinePoint current =
                    working[i];

                CoastlinePoint next =
                    working[
                        (i + 1) %
                        working.Count];

                double bearing =
                    BearingDegrees(
                        previous.Latitude,
                        previous.Longitude,
                        next.Latitude,
                        next.Longitude);

                // CCW polygon:
                // interior is on the left,
                // therefore outside is on the right.
                //
                // CW polygon:
                // interior is on the right,
                // therefore outside is on the left.
                double outwardBearing =
                    counterClockwise
                        ? bearing + 90.0
                        : bearing - 90.0;

                result.Add(
                    DestinationPoint(
                        current,
                        NormalizeBearing(
                            outwardBearing),
                        offsetNm));
            }

            EnsureClosed(result);

            return result;
        }

        private static List<CoastlinePoint>
            OffsetOutsideOpenSection(
                List<CoastlinePoint> section,
                List<CoastlinePoint> fullPolygon,
                double offsetNm)
        {
            List<CoastlinePoint> polygon =
                RemoveDuplicateClosure(
                    fullPolygon);

            bool counterClockwise =
                SignedPolygonArea(
                    polygon) >
                0;

            var result =
                new List<CoastlinePoint>(
                    section.Count);

            for (int i = 0;
                 i < section.Count;
                 i++)
            {
                CoastlinePoint previous =
                    section[
                        i == 0
                            ? 0
                            : i - 1];

                CoastlinePoint current =
                    section[i];

                CoastlinePoint next =
                    section[
                        i == section.Count - 1
                            ? section.Count - 1
                            : i + 1];

                double bearing =
                    BearingDegrees(
                        previous.Latitude,
                        previous.Longitude,
                        next.Latitude,
                        next.Longitude);

                double outwardBearing =
                    counterClockwise
                        ? bearing + 90.0
                        : bearing - 90.0;

                result.Add(
                    DestinationPoint(
                        current,
                        NormalizeBearing(
                            outwardBearing),
                        offsetNm));
            }

            return result;
        }

        // =====================================================
        // ALONG SECTION
        // =====================================================

        private static List<CoastlinePoint>
            ExtractBestBoundarySection(
                List<CoastlinePoint> closedOutline,
                double startLatitude,
                double startLongitude,
                double endLatitude,
                double endLongitude)
        {
            List<CoastlinePoint> outline =
                RemoveDuplicateClosure(
                    closedOutline);

            int startIndex =
                FindNearestPointIndex(
                    outline,
                    startLatitude,
                    startLongitude);

            int endIndex =
                FindNearestPointIndex(
                    outline,
                    endLatitude,
                    endLongitude);

            if (startIndex < 0 ||
                endIndex < 0 ||
                startIndex == endIndex)
            {
                return new List<CoastlinePoint>();
            }

            List<CoastlinePoint> forward =
                CircularSlice(
                    outline,
                    startIndex,
                    endIndex,
                    1);

            List<CoastlinePoint> backward =
                CircularSlice(
                    outline,
                    startIndex,
                    endIndex,
                    -1);

            double forwardLength =
                ChainLengthNm(
                    forward);

            double backwardLength =
                ChainLengthNm(
                    backward);

            // For a coastline-style route between two points,
            // choose the shorter outside-edge path.
            return forwardLength <= backwardLength
                ? forward
                : backward;
        }

        private static List<CoastlinePoint> CircularSlice(
            List<CoastlinePoint> points,
            int startIndex,
            int endIndex,
            int step)
        {
            var result =
                new List<CoastlinePoint>();

            int index =
                startIndex;

            for (int guard = 0;
                 guard <= points.Count + 1;
                 guard++)
            {
                result.Add(
                    points[index]);

                if (index == endIndex)
                    break;

                index =
                    (index +
                     step +
                     points.Count) %
                    points.Count;
            }

            return result;
        }

        // =====================================================
        // SIMPLIFICATION
        // =====================================================

        private static List<CoastlinePoint>
            SimplifyClosedOutline(
                List<CoastlinePoint> points,
                double toleranceNm,
                double maxSegmentNm,
                int maxPoints)
        {
            List<CoastlinePoint> working =
                RemoveDuplicateClosure(
                    points);

            if (working.Count < 4)
            {
                EnsureClosed(working);
                return working;
            }

            int firstIndex =
                0;

            int oppositeIndex =
                FindFarthestPointIndex(
                    working,
                    firstIndex);

            List<CoastlinePoint> firstHalf =
                CircularSlice(
                    working,
                    firstIndex,
                    oppositeIndex,
                    1);

            List<CoastlinePoint> secondHalf =
                CircularSlice(
                    working,
                    oppositeIndex,
                    firstIndex,
                    1);

            firstHalf =
                RamerDouglasPeucker(
                    firstHalf,
                    toleranceNm);

            secondHalf =
                RamerDouglasPeucker(
                    secondHalf,
                    toleranceNm);

            var simplified =
                new List<CoastlinePoint>();

            simplified.AddRange(
                firstHalf.Take(
                    firstHalf.Count - 1));

            simplified.AddRange(
                secondHalf.Take(
                    secondHalf.Count - 1));

            simplified =
                EnforceMaximumSegmentLength(
                    simplified,
                    maxSegmentNm,
                    true);

            simplified =
                LimitPoints(
                    simplified,
                    maxPoints,
                    true);

            EnsureClosed(
                simplified);

            return simplified;
        }

        private static List<CoastlinePoint>
            SimplifyOpenOutline(
                List<CoastlinePoint> points,
                double toleranceNm,
                double maxSegmentNm,
                int maxPoints)
        {
            List<CoastlinePoint> simplified =
                RamerDouglasPeucker(
                    points,
                    toleranceNm);

            simplified =
                EnforceMaximumSegmentLength(
                    simplified,
                    maxSegmentNm,
                    false);

            return LimitPoints(
                simplified,
                maxPoints,
                false);
        }

        private static List<CoastlinePoint>
            RamerDouglasPeucker(
                List<CoastlinePoint> points,
                double toleranceNm)
        {
            if (points.Count <= 2)
                return new List<CoastlinePoint>(points);

            int index =
                -1;

            double maximumDistance =
                0;

            CoastlinePoint start =
                points[0];

            CoastlinePoint end =
                points[^1];

            for (int i = 1;
                 i < points.Count - 1;
                 i++)
            {
                double distance =
                    PerpendicularDistanceNm(
                        points[i],
                        start,
                        end);

                if (distance >
                    maximumDistance)
                {
                    maximumDistance =
                        distance;

                    index =
                        i;
                }
            }

            if (index >= 0 &&
                maximumDistance >
                toleranceNm)
            {
                List<CoastlinePoint> first =
                    RamerDouglasPeucker(
                        points
                            .Take(index + 1)
                            .ToList(),
                        toleranceNm);

                List<CoastlinePoint> second =
                    RamerDouglasPeucker(
                        points
                            .Skip(index)
                            .ToList(),
                        toleranceNm);

                return first
                    .Take(first.Count - 1)
                    .Concat(second)
                    .ToList();
            }

            return new List<CoastlinePoint>
            {
                start,
                end
            };
        }

        private static List<CoastlinePoint>
            EnforceMaximumSegmentLength(
                List<CoastlinePoint> points,
                double maxSegmentNm,
                bool closed)
        {
            if (points.Count < 2)
                return new List<CoastlinePoint>(points);

            var result =
                new List<CoastlinePoint>();

            int segmentCount =
                closed
                    ? points.Count
                    : points.Count - 1;

            for (int i = 0;
                 i < segmentCount;
                 i++)
            {
                CoastlinePoint start =
                    points[i];

                CoastlinePoint end =
                    points[
                        (i + 1) %
                        points.Count];

                if (i == 0)
                    result.Add(start);

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
                            distance /
                            maxSegmentNm));

                for (int section = 1;
                     section <= sections;
                     section++)
                {
                    double fraction =
                        section /
                        (double)sections;

                    CoastlinePoint point =
                        new CoastlinePoint
                        {
                            Latitude =
                                start.Latitude +
                                (end.Latitude -
                                 start.Latitude) *
                                fraction,

                            Longitude =
                                start.Longitude +
                                (end.Longitude -
                                 start.Longitude) *
                                fraction
                        };

                    if (closed &&
                        i == segmentCount - 1 &&
                        section == sections)
                    {
                        continue;
                    }

                    result.Add(point);
                }
            }

            return result;
        }

        private static List<CoastlinePoint> LimitPoints(
            List<CoastlinePoint> points,
            int maximum,
            bool closed)
        {
            if (points.Count <= maximum)
                return points;

            int step =
                (int)Math.Ceiling(
                    points.Count /
                    (double)maximum);

            List<CoastlinePoint> result =
                points
                    .Where(
                        (_, index) =>
                            index % step == 0)
                    .ToList();

            if (!closed &&
                DistanceNm(
                    result[^1].Latitude,
                    result[^1].Longitude,
                    points[^1].Latitude,
                    points[^1].Longitude) >
                0.001)
            {
                result.Add(
                    points[^1]);
            }

            return result;
        }

        // =====================================================
        // POLYGON HELPERS
        // =====================================================

        private static void EnsureClosed(
            List<CoastlinePoint> points)
        {
            if (points.Count < 2)
                return;

            if (DistanceNm(
                    points[0].Latitude,
                    points[0].Longitude,
                    points[^1].Latitude,
                    points[^1].Longitude) <
                0.001)
            {
                return;
            }

            points.Add(
                new CoastlinePoint
                {
                    Latitude =
                        points[0].Latitude,

                    Longitude =
                        points[0].Longitude
                });
        }

        private static List<CoastlinePoint>
            RemoveDuplicateClosure(
                List<CoastlinePoint> points)
        {
            var result =
                new List<CoastlinePoint>(
                    points);

            if (result.Count > 2 &&
                DistanceNm(
                    result[0].Latitude,
                    result[0].Longitude,
                    result[^1].Latitude,
                    result[^1].Longitude) <
                0.001)
            {
                result.RemoveAt(
                    result.Count - 1);
            }

            return result;
        }

        private static double SignedPolygonArea(
            List<CoastlinePoint> points)
        {
            if (points.Count < 3)
                return 0;

            double centreLatitude =
                points.Average(
                    p => p.Latitude);

            double cos =
                Math.Cos(
                    centreLatitude *
                    Math.PI /
                    180.0);

            double area =
                0;

            for (int i = 0;
                 i < points.Count;
                 i++)
            {
                CoastlinePoint first =
                    points[i];

                CoastlinePoint second =
                    points[
                        (i + 1) %
                        points.Count];

                double x1 =
                    first.Longitude *
                    cos;

                double y1 =
                    first.Latitude;

                double x2 =
                    second.Longitude *
                    cos;

                double y2 =
                    second.Latitude;

                area +=
                    x1 * y2 -
                    x2 * y1;
            }

            return area / 2.0;
        }

        private static double AbsolutePolygonArea(
            List<CoastlinePoint> points)
        {
            return Math.Abs(
                SignedPolygonArea(
                    RemoveDuplicateClosure(
                        points)));
        }

        private static int FindFarthestPointIndex(
            List<CoastlinePoint> points,
            int originIndex)
        {
            int bestIndex =
                originIndex;

            double bestDistance =
                -1;

            CoastlinePoint origin =
                points[originIndex];

            for (int i = 0;
                 i < points.Count;
                 i++)
            {
                double distance =
                    DistanceNm(
                        origin.Latitude,
                        origin.Longitude,
                        points[i].Latitude,
                        points[i].Longitude);

                if (distance >
                    bestDistance)
                {
                    bestDistance =
                        distance;

                    bestIndex =
                        i;
                }
            }

            return bestIndex;
        }

        private static int FindNearestPointIndex(
            List<CoastlinePoint> points,
            double latitude,
            double longitude)
        {
            int bestIndex =
                -1;

            double bestDistance =
                double.MaxValue;

            for (int i = 0;
                 i < points.Count;
                 i++)
            {
                double distance =
                    DistanceNm(
                        latitude,
                        longitude,
                        points[i].Latitude,
                        points[i].Longitude);

                if (distance <
                    bestDistance)
                {
                    bestDistance =
                        distance;

                    bestIndex =
                        i;
                }
            }

            return bestIndex;
        }

        private static double DistanceToOutlineNm(
            double latitude,
            double longitude,
            List<CoastlinePoint> points)
        {
            double best =
                double.MaxValue;

            foreach (CoastlinePoint point
                in points)
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
            double total =
                0;

            for (int i = 1;
                 i < points.Count;
                 i++)
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

        // =====================================================
        // GEOGRAPHIC MATHS
        // =====================================================

        private static double PerpendicularDistanceNm(
            CoastlinePoint point,
            CoastlinePoint lineStart,
            CoastlinePoint lineEnd)
        {
            double referenceLatitude =
                (lineStart.Latitude +
                 lineEnd.Latitude +
                 point.Latitude) /
                3.0;

            double cos =
                Math.Cos(
                    referenceLatitude *
                    Math.PI /
                    180.0);

            double x =
                point.Longitude *
                60.0 *
                cos;

            double y =
                point.Latitude *
                60.0;

            double x1 =
                lineStart.Longitude *
                60.0 *
                cos;

            double y1 =
                lineStart.Latitude *
                60.0;

            double x2 =
                lineEnd.Longitude *
                60.0 *
                cos;

            double y2 =
                lineEnd.Latitude *
                60.0;

            double dx =
                x2 - x1;

            double dy =
                y2 - y1;

            if (Math.Abs(dx) <
                    0.0000001 &&
                Math.Abs(dy) <
                    0.0000001)
            {
                return Math.Sqrt(
                    Math.Pow(
                        x - x1,
                        2) +
                    Math.Pow(
                        y - y1,
                        2));
            }

            double t =
                ((x - x1) * dx +
                 (y - y1) * dy) /
                (dx * dx +
                 dy * dy);

            t =
                Math.Max(
                    0,
                    Math.Min(
                        1,
                        t));

            double nearestX =
                x1 +
                t * dx;

            double nearestY =
                y1 +
                t * dy;

            return Math.Sqrt(
                Math.Pow(
                    x - nearestX,
                    2) +
                Math.Pow(
                    y - nearestY,
                    2));
        }

        private static CoastlinePoint DestinationPoint(
            CoastlinePoint origin,
            double bearingDegrees,
            double distanceNm)
        {
            const double EarthRadiusNm =
                3440.065;

            double angularDistance =
                distanceNm /
                EarthRadiusNm;

            double bearing =
                bearingDegrees *
                Math.PI /
                180.0;

            double latitude1 =
                origin.Latitude *
                Math.PI /
                180.0;

            double longitude1 =
                origin.Longitude *
                Math.PI /
                180.0;

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
                Latitude =
                    latitude2 *
                    180.0 /
                    Math.PI,

                Longitude =
                    longitude2 *
                    180.0 /
                    Math.PI
            };
        }

        private static double BearingDegrees(
            double latitude1,
            double longitude1,
            double latitude2,
            double longitude2)
        {
            double lat1 =
                latitude1 *
                Math.PI /
                180.0;

            double lat2 =
                latitude2 *
                Math.PI /
                180.0;

            double deltaLongitude =
                (longitude2 -
                 longitude1) *
                Math.PI /
                180.0;

            double y =
                Math.Sin(
                    deltaLongitude) *
                Math.Cos(
                    lat2);

            double x =
                Math.Cos(lat1) *
                Math.Sin(lat2) -
                Math.Sin(lat1) *
                Math.Cos(lat2) *
                Math.Cos(
                    deltaLongitude);

            return NormalizeBearing(
                Math.Atan2(
                    y,
                    x) *
                180.0 /
                Math.PI);
        }

        private static double NormalizeBearing(
            double bearing)
        {
            bearing %=
                360.0;

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
            const double EarthRadiusNm =
                3440.065;

            double lat1 =
                latitude1 *
                Math.PI /
                180.0;

            double lat2 =
                latitude2 *
                Math.PI /
                180.0;

            double deltaLatitude =
                (latitude2 -
                 latitude1) *
                Math.PI /
                180.0;

            double deltaLongitude =
                (longitude2 -
                 longitude1) *
                Math.PI /
                180.0;

            double a =
                Math.Sin(
                    deltaLatitude /
                    2.0) *
                Math.Sin(
                    deltaLatitude /
                    2.0) +
                Math.Cos(lat1) *
                Math.Cos(lat2) *
                Math.Sin(
                    deltaLongitude /
                    2.0) *
                Math.Sin(
                    deltaLongitude /
                    2.0);

            double c =
                2.0 *
                Math.Atan2(
                    Math.Sqrt(a),
                    Math.Sqrt(
                        1.0 - a));

            return EarthRadiusNm *
                   c;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace H145FlightPlanner.Services
{
    public class GeographyResult
    {
        public string Name { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public double Latitude { get; set; }

        public double Longitude { get; set; }

        public double SouthLatitude { get; set; }

        public double NorthLatitude { get; set; }

        public double WestLongitude { get; set; }

        public double EastLongitude { get; set; }

        public string OsmType { get; set; } = string.Empty;

        public long OsmId { get; set; }

        public string PlaceType { get; set; } = string.Empty;

        public string Category { get; set; } = string.Empty;

        public bool HasBoundingBox =>
            NorthLatitude > SouthLatitude &&
            EastLongitude > WestLongitude;
    }

    public class GeographyService
    {
        private static readonly HttpClient HttpClient =
            CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient();

            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "H145FlightPlanGenerator/1.0");

            client.Timeout =
                TimeSpan.FromSeconds(30);

            return client;
        }

        public async Task<GeographyResult?> FindPlaceAsync(
            string placeName,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(placeName))
                return null;

            string cleanedName =
                placeName.Trim();

            string query =
                Uri.EscapeDataString(cleanedName);

            // UK / Ireland operating area.
            //
            // gb = United Kingdom
            // im = Isle of Man
            // gg = Guernsey
            // je = Jersey
            // ie = Ireland
            //
            // namedetails gives us the real name and aliases.
            // dedupe=0 lets us compare competing OSM features.
            // polygon_geojson gives us the actual geometry.

            string url =
                $"https://nominatim.openstreetmap.org/search" +
                $"?q={query}" +
                $"&format=jsonv2" +
                $"&limit=30" +
                $"&addressdetails=1" +
                $"&namedetails=1" +
                $"&extratags=1" +
                $"&dedupe=0" +
                $"&polygon_geojson=1" +
                $"&countrycodes=gb,im,gg,je,ie";

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

            if (document.RootElement.GetArrayLength() == 0)
                return null;

            var results =
                new List<JsonElement>();

            foreach (JsonElement element
                in document.RootElement.EnumerateArray())
            {
                results.Add(
                    element.Clone());
            }

            JsonElement? selected =
                SelectBestPlaceResult(
                    results,
                    cleanedName);

            if (selected == null)
                return null;

            JsonElement result =
                selected.Value;

            if (!TryGetDouble(
                result,
                "lat",
                out double latitude))
            {
                return null;
            }

            if (!TryGetDouble(
                result,
                "lon",
                out double longitude))
            {
                return null;
            }

            var geographyResult =
                new GeographyResult
                {
                    Name =
                        cleanedName,

                    DisplayName =
                        GetString(
                            result,
                            "display_name"),

                    Latitude =
                        latitude,

                    Longitude =
                        longitude,

                    OsmType =
                        GetString(
                            result,
                            "osm_type"),

                    OsmId =
                        GetLong(
                            result,
                            "osm_id"),

                    PlaceType =
                        GetString(
                            result,
                            "type"),

                    Category =
                        GetString(
                            result,
                            "category")
                };

            // Prefer bounds calculated from actual geometry.
            bool geometryBoundsFound =
                TryReadGeometryBounds(
                    result,
                    geographyResult);

            if (!geometryBoundsFound)
            {
                ReadBoundingBox(
                    result,
                    geographyResult);
            }

            // Never send obviously broken/global geometry
            // into the Around generator.
            if (geographyResult.HasBoundingBox &&
                !BoundingBoxLooksReasonable(
                    geographyResult))
            {
                geographyResult.SouthLatitude = 0;
                geographyResult.NorthLatitude = 0;
                geographyResult.WestLongitude = 0;
                geographyResult.EastLongitude = 0;
            }

            return geographyResult;
        }

        private static JsonElement? SelectBestPlaceResult(
            List<JsonElement> results,
            string requestedName)
        {
            if (results.Count == 0)
                return null;

            bool wantsHelipad =
                ContainsWord(
                    requestedName,
                    "helipad");

            bool wantsHeliport =
                ContainsWord(
                    requestedName,
                    "heliport");

            bool wantsAirport =
                ContainsWord(
                    requestedName,
                    "airport") ||
                ContainsWord(
                    requestedName,
                    "aerodrome");

            // -------------------------------------------------
            // EXPLICIT AVIATION REQUEST
            // -------------------------------------------------

            if (wantsHelipad)
            {
                JsonElement? result =
                    FindBestExplicitType(
                        results,
                        "helipad",
                        requestedName);

                if (result != null)
                    return result;
            }

            if (wantsHeliport)
            {
                JsonElement? result =
                    FindBestExplicitType(
                        results,
                        "heliport",
                        requestedName);

                if (result != null)
                    return result;
            }

            if (wantsAirport)
            {
                JsonElement? airport =
                    FindBestAirport(
                        results,
                        requestedName);

                if (airport != null)
                    return airport;
            }

            if (wantsHelipad ||
                wantsHeliport ||
                wantsAirport)
            {
                JsonElement? aviation =
                    results
                        .Where(IsAviationResult)
                        .OrderByDescending(
                            x => GetImportance(x))
                        .Cast<JsonElement?>()
                        .FirstOrDefault();

                if (aviation != null)
                    return aviation;

                return results[0];
            }

            // -------------------------------------------------
            // NORMAL PLACE / ISLAND / AREA REQUEST
            // -------------------------------------------------

            string normalizedRequest =
                NormalizeName(
                    requestedName);

            var candidates =
                new List<ScoredCandidate>();

            foreach (JsonElement result
                in results)
            {
                if (IsAviationResult(result))
                    continue;

                int nameScore =
                    GetNameMatchScore(
                        result,
                        normalizedRequest);

                // A weak/unrelated result should never win just
                // because its geometry happens to be convenient.
                if (nameScore <= 0)
                    continue;

                int featureScore =
                    GetFeatureScore(
                        result);

                double importance =
                    GetImportance(
                        result);

                double geometrySize =
                    GetGeometrySizeScore(
                        result);

                double totalScore =
                    nameScore +
                    featureScore +
                    (importance * 100.0);

                // Among otherwise good matches, prefer the
                // tighter actual geographic feature.
                //
                // This is what helps an island feature beat
                // an oversized territorial/admin boundary.
                if (geometrySize > 0)
                {
                    totalScore -=
                        Math.Min(
                            geometrySize,
                            100.0);
                }

                candidates.Add(
                    new ScoredCandidate
                    {
                        Result =
                            result,

                        Score =
                            totalScore,

                        NameScore =
                            nameScore,

                        FeatureScore =
                            featureScore,

                        Importance =
                            importance,

                        GeometrySize =
                            geometrySize
                    });
            }

            if (candidates.Count == 0)
                return null;

            // -------------------------------------------------
            // FIRST CHOICE:
            // Exact-name island/islet.
            //
            // If OSM has an actual island object for the name,
            // use it instead of an administrative boundary.
            // -------------------------------------------------

            ScoredCandidate? exactIsland =
                candidates
                    .Where(candidate =>
                        candidate.NameScore >= 200 &&
                        IsIslandResult(
                            candidate.Result))
                    .OrderBy(candidate =>
                        candidate.GeometrySize <= 0
                            ? double.MaxValue
                            : candidate.GeometrySize)
                    .ThenByDescending(candidate =>
                        candidate.Importance)
                    .FirstOrDefault();

            if (exactIsland != null)
            {
                return exactIsland.Result;
            }

            // -------------------------------------------------
            // SECOND CHOICE:
            // Exact-name geographic features.
            //
            // Prefer actual place/region geometry with a tight
            // footprint over an enormous administrative area.
            // -------------------------------------------------

            List<ScoredCandidate> exactMatches =
                candidates
                    .Where(candidate =>
                        candidate.NameScore >= 200)
                    .ToList();

            if (exactMatches.Count > 0)
            {
                ScoredCandidate bestExact =
                    exactMatches
                        .OrderByDescending(candidate =>
                            candidate.FeatureScore)
                        .ThenByDescending(candidate =>
                            candidate.Importance)
                        .ThenBy(candidate =>
                            candidate.GeometrySize <= 0
                                ? double.MaxValue
                                : candidate.GeometrySize)
                        .First();

                return bestExact.Result;
            }

            // -------------------------------------------------
            // THIRD CHOICE:
            // Strong alias/name match.
            // -------------------------------------------------

            ScoredCandidate best =
                candidates
                    .OrderByDescending(candidate =>
                        candidate.Score)
                    .First();

            // Don't silently accept something with an extremely
            // weak relationship to what the user actually said.
            if (best.NameScore < 80)
                return null;

            return best.Result;
        }

        private static int GetNameMatchScore(
            JsonElement result,
            string normalizedRequest)
        {
            if (string.IsNullOrWhiteSpace(
                normalizedRequest))
            {
                return 0;
            }

            int bestScore =
                0;

            foreach (string name
                in GetResultNames(result))
            {
                string normalizedName =
                    NormalizeName(name);

                if (string.IsNullOrWhiteSpace(
                    normalizedName))
                {
                    continue;
                }

                if (string.Equals(
                    normalizedName,
                    normalizedRequest,
                    StringComparison.OrdinalIgnoreCase))
                {
                    bestScore =
                        Math.Max(
                            bestScore,
                            250);

                    continue;
                }

                if (normalizedName.StartsWith(
                        normalizedRequest + " ",
                        StringComparison.OrdinalIgnoreCase) ||
                    normalizedName.EndsWith(
                        " " + normalizedRequest,
                        StringComparison.OrdinalIgnoreCase))
                {
                    bestScore =
                        Math.Max(
                            bestScore,
                            170);

                    continue;
                }

                if (normalizedName.Contains(
                    normalizedRequest,
                    StringComparison.OrdinalIgnoreCase))
                {
                    bestScore =
                        Math.Max(
                            bestScore,
                            100);
                }
            }

            return bestScore;
        }

        private static IEnumerable<string> GetResultNames(
            JsonElement result)
        {
            var names =
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

            string directName =
                GetString(
                    result,
                    "name");

            if (!string.IsNullOrWhiteSpace(
                directName))
            {
                names.Add(
                    directName);
            }

            string displayName =
                GetString(
                    result,
                    "display_name");

            if (!string.IsNullOrWhiteSpace(
                displayName))
            {
                names.Add(
                    displayName);

                string firstPart =
                    displayName
                        .Split(',')[0]
                        .Trim();

                if (!string.IsNullOrWhiteSpace(
                    firstPart))
                {
                    names.Add(
                        firstPart);
                }
            }

            if (result.TryGetProperty(
                "namedetails",
                out JsonElement nameDetails) &&
                nameDetails.ValueKind ==
                    JsonValueKind.Object)
            {
                foreach (JsonProperty property
                    in nameDetails.EnumerateObject())
                {
                    if (property.Value.ValueKind !=
                        JsonValueKind.String)
                    {
                        continue;
                    }

                    string? value =
                        property.Value.GetString();

                    if (!string.IsNullOrWhiteSpace(
                        value))
                    {
                        names.Add(
                            value);
                    }
                }
            }

            return names;
        }

        private static int GetFeatureScore(
            JsonElement result)
        {
            string category =
                GetString(
                    result,
                    "category");

            string type =
                GetString(
                    result,
                    "type");

            if (string.Equals(
                    type,
                    "island",
                    StringComparison.OrdinalIgnoreCase))
            {
                return 180;
            }

            if (string.Equals(
                    type,
                    "islet",
                    StringComparison.OrdinalIgnoreCase))
            {
                return 160;
            }

            if (string.Equals(
                    category,
                    "place",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(
                        type,
                        "city",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        type,
                        "town",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        type,
                        "village",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return 150;
                }

                if (string.Equals(
                        type,
                        "municipality",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        type,
                        "borough",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        type,
                        "suburb",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        type,
                        "locality",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return 110;
                }

                return 100;
            }

            if (string.Equals(
                    category,
                    "boundary",
                    StringComparison.OrdinalIgnoreCase))
            {
                return 80;
            }

            if (string.Equals(
                    category,
                    "natural",
                    StringComparison.OrdinalIgnoreCase))
            {
                return 120;
            }

            return 40;
        }

        private static bool IsIslandResult(
            JsonElement result)
        {
            string type =
                GetString(
                    result,
                    "type");

            return
                string.Equals(
                    type,
                    "island",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    type,
                    "islet",
                    StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAviationResult(
            JsonElement result)
        {
            string category =
                GetString(
                    result,
                    "category");

            string type =
                GetString(
                    result,
                    "type");

            if (string.Equals(
                category,
                "aeroway",
                StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return
                string.Equals(
                    type,
                    "helipad",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    type,
                    "heliport",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    type,
                    "aerodrome",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    type,
                    "airport",
                    StringComparison.OrdinalIgnoreCase);
        }

        private static double GetImportance(
            JsonElement result)
        {
            if (!result.TryGetProperty(
                "importance",
                out JsonElement importanceElement))
            {
                return 0;
            }

            if (importanceElement.ValueKind ==
                    JsonValueKind.Number &&
                importanceElement.TryGetDouble(
                    out double importance))
            {
                return importance;
            }

            return 0;
        }

        private static double GetGeometrySizeScore(
            JsonElement result)
        {
            if (!TryGetGeometryBounds(
                result,
                out double south,
                out double north,
                out double west,
                out double east))
            {
                return 0;
            }

            double latitudeSpan =
                Math.Abs(
                    north -
                    south);

            double centreLatitude =
                (north + south) / 2.0;

            double longitudeSpan =
                Math.Abs(
                    east -
                    west) *
                Math.Cos(
                    centreLatitude *
                    Math.PI /
                    180.0);

            // Approximate degrees converted to NM.
            double northSouthNm =
                latitudeSpan * 60.0;

            double eastWestNm =
                longitudeSpan * 60.0;

            return Math.Max(
                northSouthNm,
                eastWestNm);
        }

        private static JsonElement? FindBestExplicitType(
            List<JsonElement> results,
            string type,
            string requestedName)
        {
            string normalizedRequest =
                NormalizeName(
                    requestedName);

            var matches =
                results
                    .Where(result =>
                        string.Equals(
                            GetString(
                                result,
                                "type"),
                            type,
                            StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(result =>
                        GetNameMatchScore(
                            result,
                            normalizedRequest))
                    .ThenByDescending(result =>
                        GetImportance(
                            result))
                    .ToList();

            if (matches.Count == 0)
                return null;

            return matches[0];
        }

        private static JsonElement? FindBestAirport(
            List<JsonElement> results,
            string requestedName)
        {
            string normalizedRequest =
                NormalizeName(
                    requestedName);

            var matches =
                results
                    .Where(result =>
                    {
                        string type =
                            GetString(
                                result,
                                "type");

                        return
                            string.Equals(
                                type,
                                "aerodrome",
                                StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(
                                type,
                                "airport",
                                StringComparison.OrdinalIgnoreCase);
                    })
                    .OrderByDescending(result =>
                        GetNameMatchScore(
                            result,
                            normalizedRequest))
                    .ThenByDescending(result =>
                        GetImportance(
                            result))
                    .ToList();

            if (matches.Count == 0)
                return null;

            return matches[0];
        }

        private static bool ContainsWord(
            string text,
            string word)
        {
            return Regex.IsMatch(
                text,
                $@"\b{Regex.Escape(word)}\b",
                RegexOptions.IgnoreCase);
        }

        private static string NormalizeName(
            string value)
        {
            if (string.IsNullOrWhiteSpace(
                value))
            {
                return string.Empty;
            }

            string normalized =
                value.Trim();

            normalized =
                Regex.Replace(
                    normalized,
                    @"^(?:the\s+)",
                    string.Empty,
                    RegexOptions.IgnoreCase);

            normalized =
                Regex.Replace(
                    normalized,
                    @"\s+",
                    " ");

            return normalized.Trim();
        }

        // -----------------------------------------------------
        // GEOMETRY
        // -----------------------------------------------------

        private static bool TryReadGeometryBounds(
            JsonElement result,
            GeographyResult geographyResult)
        {
            if (!TryGetGeometryBounds(
                result,
                out double south,
                out double north,
                out double west,
                out double east))
            {
                return false;
            }

            geographyResult.SouthLatitude =
                south;

            geographyResult.NorthLatitude =
                north;

            geographyResult.WestLongitude =
                west;

            geographyResult.EastLongitude =
                east;

            return true;
        }

        private static bool TryGetGeometryBounds(
            JsonElement result,
            out double south,
            out double north,
            out double west,
            out double east)
        {
            south =
                0;

            north =
                0;

            west =
                0;

            east =
                0;

            if (!result.TryGetProperty(
                "geojson",
                out JsonElement geoJson))
            {
                return false;
            }

            if (!geoJson.TryGetProperty(
                "coordinates",
                out JsonElement coordinates))
            {
                return false;
            }

            double minLatitude =
                double.MaxValue;

            double maxLatitude =
                double.MinValue;

            double minLongitude =
                double.MaxValue;

            double maxLongitude =
                double.MinValue;

            bool foundCoordinate =
                false;

            ReadCoordinatesRecursive(
                coordinates,
                ref minLatitude,
                ref maxLatitude,
                ref minLongitude,
                ref maxLongitude,
                ref foundCoordinate);

            if (!foundCoordinate)
                return false;

            if (maxLatitude <= minLatitude ||
                maxLongitude <= minLongitude)
            {
                return false;
            }

            south =
                minLatitude;

            north =
                maxLatitude;

            west =
                minLongitude;

            east =
                maxLongitude;

            return true;
        }

        private static void ReadCoordinatesRecursive(
            JsonElement element,
            ref double minLatitude,
            ref double maxLatitude,
            ref double minLongitude,
            ref double maxLongitude,
            ref bool foundCoordinate)
        {
            if (element.ValueKind !=
                JsonValueKind.Array)
            {
                return;
            }

            // GeoJSON coordinate:
            // [longitude, latitude]

            if (element.GetArrayLength() >= 2 &&
                element[0].ValueKind ==
                    JsonValueKind.Number &&
                element[1].ValueKind ==
                    JsonValueKind.Number)
            {
                double longitude =
                    element[0].GetDouble();

                double latitude =
                    element[1].GetDouble();

                if (latitude >= -90 &&
                    latitude <= 90 &&
                    longitude >= -180 &&
                    longitude <= 180)
                {
                    minLatitude =
                        Math.Min(
                            minLatitude,
                            latitude);

                    maxLatitude =
                        Math.Max(
                            maxLatitude,
                            latitude);

                    minLongitude =
                        Math.Min(
                            minLongitude,
                            longitude);

                    maxLongitude =
                        Math.Max(
                            maxLongitude,
                            longitude);

                    foundCoordinate =
                        true;
                }

                return;
            }

            foreach (JsonElement child
                in element.EnumerateArray())
            {
                ReadCoordinatesRecursive(
                    child,
                    ref minLatitude,
                    ref maxLatitude,
                    ref minLongitude,
                    ref maxLongitude,
                    ref foundCoordinate);
            }
        }

        // -----------------------------------------------------
        // FALLBACK BOUNDING BOX
        // -----------------------------------------------------

        private static void ReadBoundingBox(
            JsonElement result,
            GeographyResult geographyResult)
        {
            if (!result.TryGetProperty(
                "boundingbox",
                out JsonElement boundingBox))
            {
                return;
            }

            if (boundingBox.ValueKind !=
                    JsonValueKind.Array ||
                boundingBox.GetArrayLength() < 4)
            {
                return;
            }

            if (!TryParseDouble(
                boundingBox[0].GetString(),
                out double south))
            {
                return;
            }

            if (!TryParseDouble(
                boundingBox[1].GetString(),
                out double north))
            {
                return;
            }

            if (!TryParseDouble(
                boundingBox[2].GetString(),
                out double west))
            {
                return;
            }

            if (!TryParseDouble(
                boundingBox[3].GetString(),
                out double east))
            {
                return;
            }

            geographyResult.SouthLatitude =
                south;

            geographyResult.NorthLatitude =
                north;

            geographyResult.WestLongitude =
                west;

            geographyResult.EastLongitude =
                east;
        }

        private static bool BoundingBoxLooksReasonable(
            GeographyResult geographyResult)
        {
            double latitudeSpan =
                Math.Abs(
                    geographyResult.NorthLatitude -
                    geographyResult.SouthLatitude);

            double longitudeSpan =
                Math.Abs(
                    geographyResult.EastLongitude -
                    geographyResult.WestLongitude);

            if (latitudeSpan <= 0 ||
                longitudeSpan <= 0)
            {
                return false;
            }

            if (latitudeSpan > 8.0)
                return false;

            if (longitudeSpan > 12.0)
                return false;

            return true;
        }

        private static bool TryGetDouble(
            JsonElement element,
            string propertyName,
            out double value)
        {
            value =
                0;

            if (!element.TryGetProperty(
                propertyName,
                out JsonElement property))
            {
                return false;
            }

            if (property.ValueKind ==
                    JsonValueKind.Number &&
                property.TryGetDouble(
                    out value))
            {
                return true;
            }

            return TryParseDouble(
                property.GetString(),
                out value);
        }

        private static bool TryParseDouble(
            string? text,
            out double value)
        {
            return double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value);
        }

        private static string GetString(
            JsonElement element,
            string propertyName)
        {
            if (!element.TryGetProperty(
                propertyName,
                out JsonElement property))
            {
                return string.Empty;
            }

            if (property.ValueKind !=
                JsonValueKind.String)
            {
                return string.Empty;
            }

            return property.GetString() ??
                   string.Empty;
        }

        private static long GetLong(
            JsonElement element,
            string propertyName)
        {
            if (!element.TryGetProperty(
                propertyName,
                out JsonElement property))
            {
                return 0;
            }

            if (property.ValueKind ==
                    JsonValueKind.Number &&
                property.TryGetInt64(
                    out long number))
            {
                return number;
            }

            return 0;
        }

        private sealed class ScoredCandidate
        {
            public JsonElement Result { get; set; }

            public double Score { get; set; }

            public int NameScore { get; set; }

            public int FeatureScore { get; set; }

            public double Importance { get; set; }

            public double GeometrySize { get; set; }
        }
    }
}

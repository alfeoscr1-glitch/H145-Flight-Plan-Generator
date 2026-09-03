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
                CleanRequestedName(placeName);

            if (string.IsNullOrWhiteSpace(cleanedName))
                return null;

            List<JsonElement> results =
                await SearchNominatimAsync(
                    cleanedName,
                    cancellationToken);

            if (results.Count == 0)
                return null;

            JsonElement? selected =
                SelectBestResult(
                    results,
                    cleanedName);

            if (selected == null)
                return null;

            return CreateGeographyResult(
                selected.Value,
                cleanedName);
        }

        private static async Task<List<JsonElement>>
            SearchNominatimAsync(
                string placeName,
                CancellationToken cancellationToken)
        {
            string query =
                Uri.EscapeDataString(placeName);

            // The app currently operates around the UK,
            // Ireland, Isle of Man and Channel Islands.
            //
            // This is only a geographic search filter.
            // No individual places are hardcoded.
            string url =
                $"https://nominatim.openstreetmap.org/search" +
                $"?q={query}" +
                $"&format=jsonv2" +
                $"&limit=40" +
                $"&addressdetails=1" +
                $"&namedetails=1" +
                $"&extratags=1" +
                $"&dedupe=0" +
                $"&polygon_geojson=1" +
                $"&countrycodes=gb,im,ie,gg,je";

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

            var results =
                new List<JsonElement>();

            if (document.RootElement.ValueKind !=
                JsonValueKind.Array)
            {
                return results;
            }

            foreach (JsonElement element
                in document.RootElement.EnumerateArray())
            {
                results.Add(
                    element.Clone());
            }

            return results;
        }

        private static JsonElement? SelectBestResult(
            List<JsonElement> results,
            string requestedName)
        {
            if (results.Count == 0)
                return null;

            // -------------------------------------------------
            // EXPLICIT AVIATION REQUESTS
            // -------------------------------------------------

            if (ContainsWord(
                requestedName,
                "helipad"))
            {
                JsonElement? helipad =
                    FindBestAviationResult(
                        results,
                        requestedName,
                        "helipad");

                if (helipad != null)
                    return helipad;
            }

            if (ContainsWord(
                requestedName,
                "heliport"))
            {
                JsonElement? heliport =
                    FindBestAviationResult(
                        results,
                        requestedName,
                        "heliport");

                if (heliport != null)
                    return heliport;
            }

            if (ContainsWord(
                    requestedName,
                    "airport") ||
                ContainsWord(
                    requestedName,
                    "aerodrome"))
            {
                JsonElement? airport =
                    FindBestAirportResult(
                        results,
                        requestedName);

                if (airport != null)
                    return airport;
            }

            // -------------------------------------------------
            // NORMAL GEOGRAPHIC SEARCH
            // -------------------------------------------------

            string normalizedRequest =
                NormalizeName(
                    requestedName);

            var candidates =
                new List<Candidate>();

            foreach (JsonElement result
                in results)
            {
                // If the user did not explicitly ask for
                // aviation, never let an airport or helipad
                // steal a normal place search.
                if (IsAviationResult(result))
                    continue;

                int nameScore =
                    GetNameScore(
                        result,
                        normalizedRequest);

                int featureScore =
                    GetFeatureScore(
                        result,
                        requestedName);

                double importance =
                    GetImportance(
                        result);

                bool hasGeometry =
                    TryGetGeometryBounds(
                        result,
                        out double south,
                        out double north,
                        out double west,
                        out double east);

                double sizeNm =
                    hasGeometry
                        ? EstimateLargestDimensionNm(
                            south,
                            north,
                            west,
                            east)
                        : double.MaxValue;

                candidates.Add(
                    new Candidate
                    {
                        Result = result,
                        NameScore = nameScore,
                        FeatureScore = featureScore,
                        Importance = importance,
                        HasGeometry = hasGeometry,
                        SizeNm = sizeNm
                    });
            }

            if (candidates.Count == 0)
                return null;

            // -------------------------------------------------
            // 1. EXACT-NAME ISLAND / ISLET
            //
            // This prevents an island's administrative or
            // territorial boundary winning when the actual
            // land feature is available.
            // -------------------------------------------------

            Candidate? exactIsland =
                candidates
                    .Where(candidate =>
                        candidate.NameScore >= 300 &&
                        IsIslandResult(
                            candidate.Result))
                    .OrderByDescending(candidate =>
                        candidate.HasGeometry)
                    .ThenBy(candidate =>
                        candidate.SizeNm)
                    .ThenByDescending(candidate =>
                        candidate.Importance)
                    .FirstOrDefault();

            if (exactIsland != null)
                return exactIsland.Result;

            // -------------------------------------------------
            // 2. EXACT-NAME MATCH
            // -------------------------------------------------

            List<Candidate> exactMatches =
                candidates
                    .Where(candidate =>
                        candidate.NameScore >= 300)
                    .ToList();

            if (exactMatches.Count > 0)
            {
                Candidate winner =
                    exactMatches
                        .OrderByDescending(candidate =>
                            candidate.FeatureScore)
                        .ThenByDescending(candidate =>
                            candidate.HasGeometry)
                        .ThenBy(candidate =>
                            candidate.SizeNm)
                        .ThenByDescending(candidate =>
                            candidate.Importance)
                        .First();

                return winner.Result;
            }

            // -------------------------------------------------
            // 3. STRONG ALIAS / DISPLAY-NAME MATCH
            // -------------------------------------------------

            List<Candidate> strongMatches =
                candidates
                    .Where(candidate =>
                        candidate.NameScore >= 150)
                    .ToList();

            if (strongMatches.Count > 0)
            {
                Candidate winner =
                    strongMatches
                        .OrderByDescending(candidate =>
                            candidate.NameScore)
                        .ThenByDescending(candidate =>
                            candidate.FeatureScore)
                        .ThenByDescending(candidate =>
                            candidate.HasGeometry)
                        .ThenBy(candidate =>
                            candidate.SizeNm)
                        .ThenByDescending(candidate =>
                            candidate.Importance)
                        .First();

                return winner.Result;
            }

            // -------------------------------------------------
            // 4. FALLBACK
            //
            // This is important.
            //
            // Do NOT simply fail because our own name scoring
            // could not recognise the way OSM named something.
            //
            // Nominatim already searched for the user's actual
            // query within our operating area. Prefer its best
            // non-aviation result rather than saying that a real
            // place such as a county or island does not exist.
            // -------------------------------------------------

            Candidate fallback =
                candidates
                    .OrderByDescending(candidate =>
                        candidate.NameScore)
                    .ThenByDescending(candidate =>
                        candidate.FeatureScore)
                    .ThenByDescending(candidate =>
                        candidate.Importance)
                    .First();

            return fallback.Result;
        }

        private static GeographyResult? CreateGeographyResult(
            JsonElement result,
            string requestedName)
        {
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
                        requestedName,

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

            // First choice:
            // actual polygon / multipolygon geometry.
            if (TryGetGeometryBounds(
                result,
                out double south,
                out double north,
                out double west,
                out double east))
            {
                geographyResult.SouthLatitude =
                    south;

                geographyResult.NorthLatitude =
                    north;

                geographyResult.WestLongitude =
                    west;

                geographyResult.EastLongitude =
                    east;
            }
            else
            {
                // Only use the normal Nominatim bounding box
                // if detailed geometry was unavailable.
                ReadBoundingBox(
                    result,
                    geographyResult);
            }

            if (geographyResult.HasBoundingBox &&
                !BoundingBoxLooksReasonable(
                    geographyResult))
            {
                // A bad enormous boundary must never become
                // an Around route stretching across countries.
                //
                // Clearing the box causes AroundRouteGenerator
                // to use its local fallback around the centre
                // instead of creating a ridiculous route.
                geographyResult.SouthLatitude = 0;
                geographyResult.NorthLatitude = 0;
                geographyResult.WestLongitude = 0;
                geographyResult.EastLongitude = 0;
            }

            return geographyResult;
        }

        private static int GetNameScore(
            JsonElement result,
            string normalizedRequest)
        {
            int bestScore =
                0;

            foreach (string candidateName
                in GetAllNames(result))
            {
                string normalizedCandidate =
                    NormalizeName(
                        candidateName);

                if (string.IsNullOrWhiteSpace(
                    normalizedCandidate))
                {
                    continue;
                }

                // Exact match.
                if (string.Equals(
                    normalizedCandidate,
                    normalizedRequest,
                    StringComparison.OrdinalIgnoreCase))
                {
                    bestScore =
                        Math.Max(
                            bestScore,
                            400);

                    continue;
                }

                // Ignore leading descriptors for matching.
                //
                // Example:
                // "Isle of Anglesey"
                // can still strongly match "Anglesey".
                string simplifiedCandidate =
                    RemoveGeographicDescriptors(
                        normalizedCandidate);

                string simplifiedRequest =
                    RemoveGeographicDescriptors(
                        normalizedRequest);

                if (string.Equals(
                    simplifiedCandidate,
                    simplifiedRequest,
                    StringComparison.OrdinalIgnoreCase))
                {
                    bestScore =
                        Math.Max(
                            bestScore,
                            320);

                    continue;
                }

                if (normalizedCandidate.StartsWith(
                        normalizedRequest + " ",
                        StringComparison.OrdinalIgnoreCase) ||
                    normalizedCandidate.EndsWith(
                        " " + normalizedRequest,
                        StringComparison.OrdinalIgnoreCase))
                {
                    bestScore =
                        Math.Max(
                            bestScore,
                            220);

                    continue;
                }

                if (normalizedCandidate.Contains(
                        normalizedRequest,
                        StringComparison.OrdinalIgnoreCase) ||
                    normalizedRequest.Contains(
                        normalizedCandidate,
                        StringComparison.OrdinalIgnoreCase))
                {
                    bestScore =
                        Math.Max(
                            bestScore,
                            160);
                }
            }

            return bestScore;
        }

        private static IEnumerable<string> GetAllNames(
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
            JsonElement result,
            string requestedName)
        {
            string category =
                GetString(
                    result,
                    "category");

            string type =
                GetString(
                    result,
                    "type");

            int score =
                0;

            if (string.Equals(
                type,
                "island",
                StringComparison.OrdinalIgnoreCase))
            {
                score +=
                    300;
            }

            if (string.Equals(
                type,
                "islet",
                StringComparison.OrdinalIgnoreCase))
            {
                score +=
                    280;
            }

            if (string.Equals(
                category,
                "place",
                StringComparison.OrdinalIgnoreCase))
            {
                score +=
                    160;

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
                    score +=
                        100;
                }
            }

            if (string.Equals(
                category,
                "natural",
                StringComparison.OrdinalIgnoreCase))
            {
                score +=
                    180;
            }

            if (string.Equals(
                category,
                "boundary",
                StringComparison.OrdinalIgnoreCase))
            {
                score +=
                    120;
            }

            // If the user's wording itself clearly describes
            // an island, give island results an extra advantage.
            //
            // This is generic language understanding,
            // not a hardcoded place.
            if ((ContainsWord(
                    requestedName,
                    "island") ||
                 ContainsWord(
                    requestedName,
                    "isle")) &&
                IsIslandResult(result))
            {
                score +=
                    200;
            }

            return score;
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

        private static JsonElement?
            FindBestAviationResult(
                List<JsonElement> results,
                string requestedName,
                string requiredType)
        {
            string normalizedRequest =
                NormalizeName(
                    requestedName);

            List<JsonElement> matches =
                results
                    .Where(result =>
                        string.Equals(
                            GetString(
                                result,
                                "type"),
                            requiredType,
                            StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(result =>
                        GetNameScore(
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

        private static JsonElement?
            FindBestAirportResult(
                List<JsonElement> results,
                string requestedName)
        {
            string normalizedRequest =
                NormalizeName(
                    requestedName);

            List<JsonElement> matches =
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
                                "airport",
                                StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(
                                type,
                                "aerodrome",
                                StringComparison.OrdinalIgnoreCase);
                    })
                    .OrderByDescending(result =>
                        GetNameScore(
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

        private static double GetImportance(
            JsonElement result)
        {
            if (!result.TryGetProperty(
                "importance",
                out JsonElement importance))
            {
                return 0;
            }

            if (importance.ValueKind ==
                    JsonValueKind.Number &&
                importance.TryGetDouble(
                    out double value))
            {
                return value;
            }

            return 0;
        }

        // -----------------------------------------------------
        // REAL OSM GEOMETRY
        // -----------------------------------------------------

        private static bool TryGetGeometryBounds(
            JsonElement result,
            out double south,
            out double north,
            out double west,
            out double east)
        {
            south = 0;
            north = 0;
            west = 0;
            east = 0;

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

            // GeoJSON coordinate pair:
            // longitude, latitude
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

        private static double EstimateLargestDimensionNm(
            double south,
            double north,
            double west,
            double east)
        {
            double northSouthNm =
                Math.Abs(
                    north - south) *
                60.0;

            double centreLatitude =
                (north + south) /
                2.0;

            double eastWestNm =
                Math.Abs(
                    east - west) *
                60.0 *
                Math.Cos(
                    centreLatitude *
                    Math.PI /
                    180.0);

            return Math.Max(
                northSouthNm,
                eastWestNm);
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
            GeographyResult result)
        {
            double latitudeSpan =
                Math.Abs(
                    result.NorthLatitude -
                    result.SouthLatitude);

            double longitudeSpan =
                Math.Abs(
                    result.EastLongitude -
                    result.WestLongitude);

            if (latitudeSpan <= 0 ||
                longitudeSpan <= 0)
            {
                return false;
            }

            // These are only guards against completely
            // nonsensical/global geometry.
            //
            // They are deliberately large enough for
            // genuine countries and regions.
            if (latitudeSpan > 15.0)
                return false;

            if (longitudeSpan > 20.0)
                return false;

            return true;
        }

        private static string CleanRequestedName(
            string value)
        {
            string cleaned =
                value.Trim();

            cleaned =
                Regex.Replace(
                    cleaned,
                    @"^[,.;:\s]+",
                    string.Empty);

            cleaned =
                Regex.Replace(
                    cleaned,
                    @"[,.;:\s]+$",
                    string.Empty);

            return cleaned.Trim();
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
                value.Trim()
                    .ToLowerInvariant();

            normalized =
                Regex.Replace(
                    normalized,
                    @"^(?:the\s+)",
                    string.Empty);

            normalized =
                Regex.Replace(
                    normalized,
                    @"[^\p{L}\p{N}\s]",
                    " ");

            normalized =
                Regex.Replace(
                    normalized,
                    @"\s+",
                    " ");

            return normalized.Trim();
        }

        private static string RemoveGeographicDescriptors(
            string value)
        {
            string result =
                value;

            result =
                Regex.Replace(
                    result,
                    @"\b(?:isle\s+of|island\s+of|county\s+of)\b",
                    string.Empty,
                    RegexOptions.IgnoreCase);

            result =
                Regex.Replace(
                    result,
                    @"\s+",
                    " ");

            return result.Trim();
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

        private static bool TryGetDouble(
            JsonElement element,
            string propertyName,
            out double value)
        {
            value = 0;

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

            if (property.ValueKind ==
                JsonValueKind.String)
            {
                return TryParseDouble(
                    property.GetString(),
                    out value);
            }

            return false;
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
                    out long value))
            {
                return value;
            }

            return 0;
        }

        private sealed class Candidate
        {
            public JsonElement Result { get; set; }

            public int NameScore { get; set; }

            public int FeatureScore { get; set; }

            public double Importance { get; set; }

            public bool HasGeometry { get; set; }

            public double SizeNm { get; set; }
        }
    }
}

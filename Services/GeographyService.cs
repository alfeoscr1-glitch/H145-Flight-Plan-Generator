using System;
using System.Collections.Generic;
using System.Globalization;
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

            // -------------------------------------------------
            // SEARCH AREA
            //
            // Keep general place searches in the operating area
            // this application is intended to use.
            //
            // gb = United Kingdom
            // im = Isle of Man
            // gg = Guernsey
            // je = Jersey
            // ie = Ireland
            //
            // This prevents a place such as Anglesey from
            // accidentally resolving to something in America.
            // -------------------------------------------------

            string url =
                $"https://nominatim.openstreetmap.org/search" +
                $"?q={query}" +
                $"&format=jsonv2" +
                $"&limit=20" +
                $"&addressdetails=1" +
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

            // -------------------------------------------------
            // BOUNDS
            //
            // First try to calculate tight bounds from the
            // actual OpenStreetMap geometry.
            //
            // Only fall back to Nominatim's bounding box when
            // actual geometry is unavailable.
            // -------------------------------------------------

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

            // -------------------------------------------------
            // SAFETY CHECK
            //
            // Reject clearly broken/global-sized bounds.
            // The centre point remains usable, and the route
            // generator can use its fallback instead.
            // -------------------------------------------------

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

            // -------------------------------------------------
            // EXPLICIT AVIATION TARGET
            //
            // "Aberystwyth" should mean the place.
            //
            // "Aberystwyth helipad" should mean the helipad.
            // -------------------------------------------------

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

            if (wantsHelipad)
            {
                JsonElement? result =
                    FindByType(
                        results,
                        "helipad");

                if (result != null)
                    return result;
            }

            if (wantsHeliport)
            {
                JsonElement? result =
                    FindByType(
                        results,
                        "heliport");

                if (result != null)
                    return result;
            }

            if (wantsAirport)
            {
                JsonElement? result =
                    FindAirportResult(
                        results);

                if (result != null)
                    return result;
            }

            if (wantsHelipad ||
                wantsHeliport ||
                wantsAirport)
            {
                JsonElement? aviationResult =
                    FindByCategory(
                        results,
                        "aeroway");

                if (aviationResult != null)
                    return aviationResult;

                return results[0];
            }

            // -------------------------------------------------
            // NORMAL PLACE / AREA SEARCH
            //
            // Score all sensible candidates instead of blindly
            // accepting the first result.
            //
            // This lets islands, towns, cities and regions work
            // through the same dynamic system.
            // -------------------------------------------------

            JsonElement? bestResult =
                null;

            int bestScore =
                int.MinValue;

            foreach (JsonElement result
                in results)
            {
                int score =
                    ScoreResult(
                        result,
                        requestedName);

                if (score > bestScore)
                {
                    bestScore =
                        score;

                    bestResult =
                        result;
                }
            }

            return bestResult ??
                   results[0];
        }

        private static int ScoreResult(
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

            string resultName =
                GetString(
                    result,
                    "name");

            string displayName =
                GetString(
                    result,
                    "display_name");

            int score =
                0;

            // -------------------------------------------------
            // NAME MATCHING
            // -------------------------------------------------

            string normalizedRequest =
                NormalizeName(
                    requestedName);

            string normalizedResult =
                NormalizeName(
                    resultName);

            if (!string.IsNullOrWhiteSpace(
                    normalizedResult))
            {
                if (string.Equals(
                    normalizedRequest,
                    normalizedResult,
                    StringComparison.OrdinalIgnoreCase))
                {
                    score += 100;
                }
                else if (normalizedResult.Contains(
                    normalizedRequest,
                    StringComparison.OrdinalIgnoreCase))
                {
                    score += 50;
                }
            }

            if (displayName.StartsWith(
                requestedName,
                StringComparison.OrdinalIgnoreCase))
            {
                score += 20;
            }

            // -------------------------------------------------
            // ISLANDS
            // -------------------------------------------------

            if (string.Equals(
                type,
                "island",
                StringComparison.OrdinalIgnoreCase))
            {
                score += 90;
            }

            if (string.Equals(
                type,
                "islet",
                StringComparison.OrdinalIgnoreCase))
            {
                score += 70;
            }

            // -------------------------------------------------
            // POPULATED PLACES
            // -------------------------------------------------

            if (string.Equals(
                category,
                "place",
                StringComparison.OrdinalIgnoreCase))
            {
                score += 40;

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
                    score += 30;
                }
            }

            // -------------------------------------------------
            // BOUNDARIES
            //
            // Useful for larger regions/islands, but don't let
            // a random administrative boundary automatically
            // beat a strong named-place match.
            // -------------------------------------------------

            if (string.Equals(
                category,
                "boundary",
                StringComparison.OrdinalIgnoreCase))
            {
                score += 25;
            }

            // -------------------------------------------------
            // AVIATION PENALTY
            //
            // Unless explicitly requested, don't let airports,
            // helipads or aeroways steal a normal place search.
            // -------------------------------------------------

            if (string.Equals(
                category,
                "aeroway",
                StringComparison.OrdinalIgnoreCase))
            {
                score -= 500;
            }

            if (string.Equals(
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
                    StringComparison.OrdinalIgnoreCase))
            {
                score -= 500;
            }

            return score;
        }

        private static string NormalizeName(
            string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string normalized =
                value.Trim();

            normalized = Regex.Replace(
                normalized,
                @"^(?:the\s+)",
                string.Empty,
                RegexOptions.IgnoreCase);

            return normalized.Trim();
        }

        private static JsonElement? FindByType(
            List<JsonElement> results,
            string type)
        {
            foreach (JsonElement result in results)
            {
                if (string.Equals(
                    GetString(
                        result,
                        "type"),
                    type,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return result;
                }
            }

            return null;
        }

        private static JsonElement? FindAirportResult(
            List<JsonElement> results)
        {
            string[] airportTypes =
            {
                "aerodrome",
                "airport"
            };

            foreach (string type
                in airportTypes)
            {
                JsonElement? result =
                    FindByType(
                        results,
                        type);

                if (result != null)
                    return result;
            }

            return null;
        }

        private static JsonElement? FindByCategory(
            List<JsonElement> results,
            string category)
        {
            foreach (JsonElement result in results)
            {
                if (string.Equals(
                    GetString(
                        result,
                        "category"),
                    category,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return result;
                }
            }

            return null;
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

        // -----------------------------------------------------
        // ACTUAL GEOMETRY BOUNDING BOX
        // -----------------------------------------------------

        private static bool TryReadGeometryBounds(
            JsonElement result,
            GeographyResult geographyResult)
        {
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

            geographyResult.SouthLatitude =
                minLatitude;

            geographyResult.NorthLatitude =
                maxLatitude;

            geographyResult.WestLongitude =
                minLongitude;

            geographyResult.EastLongitude =
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

            // GeoJSON coordinates are:
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

        // -----------------------------------------------------
        // SANITY CHECK
        // -----------------------------------------------------

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

            // No individual Around target should ever need a
            // world-scale bounding box.
            //
            // This is intentionally generous enough for large
            // UK/Ireland regions while rejecting obviously
            // broken transatlantic/global results.
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
    }
}

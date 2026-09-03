using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
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
        private static readonly HttpClient HttpClient = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient();

            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "H145FlightPlanGenerator/1.0");

            client.Timeout = TimeSpan.FromSeconds(20);

            return client;
        }

        public async Task<GeographyResult?> FindPlaceAsync(
            string placeName,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(placeName))
                return null;

            string cleanedName = placeName.Trim();

            string query =
                Uri.EscapeDataString(cleanedName);

            // Ask for several possibilities instead of blindly
            // accepting the first OpenStreetMap result.
            string url =
                $"https://nominatim.openstreetmap.org/search" +
                $"?q={query}" +
                $"&format=jsonv2" +
                $"&limit=10" +
                $"&addressdetails=1";

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

            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            if (document.RootElement.GetArrayLength() == 0)
                return null;

            var results = new List<JsonElement>();

            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                results.Add(element.Clone());
            }

            JsonElement? selected =
                SelectBestPlaceResult(results);

            if (selected == null)
                return null;

            JsonElement result = selected.Value;

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

            var geographyResult = new GeographyResult
            {
                Name = cleanedName,

                DisplayName =
                    GetString(result, "display_name"),

                Latitude = latitude,

                Longitude = longitude,

                OsmType =
                    GetString(result, "osm_type"),

                OsmId =
                    GetLong(result, "osm_id"),

                PlaceType =
                    GetString(result, "type"),

                Category =
                    GetString(result, "category")
            };

            ReadBoundingBox(
                result,
                geographyResult);

            return geographyResult;
        }

        private static JsonElement? SelectBestPlaceResult(
            List<JsonElement> results)
        {
            if (results.Count == 0)
                return null;

            // -------------------------------------------------
            // PRIORITY 1:
            // Actual populated places.
            // -------------------------------------------------

            string[] preferredPlaceTypes =
            {
                "city",
                "town",
                "village",
                "hamlet",
                "municipality",
                "borough",
                "suburb",
                "quarter",
                "neighbourhood",
                "locality"
            };

            foreach (string preferredType in preferredPlaceTypes)
            {
                JsonElement? match =
                    results.FirstOrDefaultNullable(
                        result =>
                            IsPlaceCategory(result) &&
                            string.Equals(
                                GetString(result, "type"),
                                preferredType,
                                StringComparison.OrdinalIgnoreCase));

                if (match != null)
                    return match;
            }

            // -------------------------------------------------
            // PRIORITY 2:
            // Other OSM objects classified as a place.
            // -------------------------------------------------

            foreach (JsonElement result in results)
            {
                if (IsPlaceCategory(result))
                    return result;
            }

            // -------------------------------------------------
            // PRIORITY 3:
            // Administrative boundaries can represent cities,
            // towns and other named areas.
            // -------------------------------------------------

            foreach (JsonElement result in results)
            {
                string category =
                    GetString(result, "category");

                if (string.Equals(
                    category,
                    "boundary",
                    StringComparison.OrdinalIgnoreCase))
                {
                    return result;
                }
            }

            // -------------------------------------------------
            // FALLBACK:
            // If there genuinely isn't a settlement result,
            // allow the best normal Nominatim result.
            //
            // This means an explicitly searched helipad,
            // heliport, landmark, etc. can still work.
            // -------------------------------------------------

            return results[0];
        }

        private static bool IsPlaceCategory(
            JsonElement result)
        {
            string category =
                GetString(result, "category");

            return string.Equals(
                category,
                "place",
                StringComparison.OrdinalIgnoreCase);
        }

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

            if (boundingBox.ValueKind != JsonValueKind.Array ||
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

            geographyResult.SouthLatitude = south;
            geographyResult.NorthLatitude = north;
            geographyResult.WestLongitude = west;
            geographyResult.EastLongitude = east;
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

            return property.GetString() ?? string.Empty;
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

            if (property.ValueKind == JsonValueKind.Number &&
                property.TryGetInt64(out long number))
            {
                return number;
            }

            return 0;
        }
    }

    internal static class JsonElementExtensions
    {
        public static JsonElement? FirstOrDefaultNullable(
            this IEnumerable<JsonElement> source,
            Func<JsonElement, bool> predicate)
        {
            foreach (JsonElement item in source)
            {
                if (predicate(item))
                    return item;
            }

            return null;
        }
    }
}

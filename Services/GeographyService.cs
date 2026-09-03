using System;
using System.Globalization;
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

            string query =
                Uri.EscapeDataString(placeName.Trim());

            string url =
                $"https://nominatim.openstreetmap.org/search" +
                $"?q={query}" +
                $"&format=jsonv2" +
                $"&limit=1" +
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

            if (document.RootElement.ValueKind !=
                JsonValueKind.Array)
            {
                return null;
            }

            if (document.RootElement.GetArrayLength() == 0)
                return null;

            JsonElement result =
                document.RootElement[0];

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
                Name = placeName.Trim(),

                DisplayName =
                    GetString(result, "display_name"),

                Latitude = latitude,

                Longitude = longitude,

                OsmType =
                    GetString(result, "osm_type"),

                OsmId =
                    GetLong(result, "osm_id"),

                PlaceType =
                    GetString(result, "type")
            };

            ReadBoundingBox(
                result,
                geographyResult);

            return geographyResult;
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
}

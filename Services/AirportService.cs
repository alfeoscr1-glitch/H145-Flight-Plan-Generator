using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace H145FlightPlanner.Services
{
    public class AirportResult
    {
        public string Ident { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public double Latitude { get; set; }

        public double Longitude { get; set; }

        public double ElevationFeet { get; set; }

        public string AerowayType { get; set; } = string.Empty;
    }

    public class AirportService
    {
        private static readonly HttpClient HttpClient = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient();

            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "H145FlightPlanGenerator/1.0");

            client.Timeout = TimeSpan.FromSeconds(30);

            return client;
        }

        public async Task<AirportResult?> FindByIcaoAsync(
            string icao,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(icao))
                return null;

            string cleanedIcao =
                icao.Trim().ToUpperInvariant();

            string overpassQuery =
                $"""
                [out:json][timeout:25];
                (
                  nwr["icao"="{cleanedIcao}"]["aeroway"];
                );
                out center tags 1;
                """;

            string url =
                "https://overpass-api.de/api/interpreter";

            using var content =
                new StringContent(overpassQuery);

            using HttpResponseMessage response =
                await HttpClient.PostAsync(
                    url,
                    content,
                    cancellationToken);

            response.EnsureSuccessStatusCode();

            string json =
                await response.Content.ReadAsStringAsync(
                    cancellationToken);

            using JsonDocument document =
                JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty(
                "elements",
                out JsonElement elements))
            {
                return null;
            }

            if (elements.ValueKind != JsonValueKind.Array ||
                elements.GetArrayLength() == 0)
            {
                return null;
            }

            JsonElement element = elements[0];

            if (!TryGetCoordinates(
                element,
                out double latitude,
                out double longitude))
            {
                return null;
            }

            string name = cleanedIcao;
            string aerowayType = string.Empty;
            double elevationFeet = 0;

            if (element.TryGetProperty(
                "tags",
                out JsonElement tags))
            {
                name =
                    GetString(tags, "name");

                if (string.IsNullOrWhiteSpace(name))
                    name = cleanedIcao;

                aerowayType =
                    GetString(tags, "aeroway");

                elevationFeet =
                    GetElevationFeet(tags);
            }

            return new AirportResult
            {
                Ident = cleanedIcao,
                Name = name,
                Latitude = latitude,
                Longitude = longitude,
                ElevationFeet = elevationFeet,
                AerowayType = aerowayType
            };
        }

        private static bool TryGetCoordinates(
            JsonElement element,
            out double latitude,
            out double longitude)
        {
            latitude = 0;
            longitude = 0;

            if (element.TryGetProperty(
                    "lat",
                    out JsonElement latElement) &&
                element.TryGetProperty(
                    "lon",
                    out JsonElement lonElement))
            {
                latitude = latElement.GetDouble();
                longitude = lonElement.GetDouble();

                return true;
            }

            if (element.TryGetProperty(
                "center",
                out JsonElement center))
            {
                if (center.TryGetProperty(
                        "lat",
                        out latElement) &&
                    center.TryGetProperty(
                        "lon",
                        out lonElement))
                {
                    latitude = latElement.GetDouble();
                    longitude = lonElement.GetDouble();

                    return true;
                }
            }

            return false;
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

        private static double GetElevationFeet(
            JsonElement tags)
        {
            string elevation =
                GetString(tags, "ele");

            if (string.IsNullOrWhiteSpace(elevation))
                return 0;

            string numericPart =
                elevation
                    .Replace("m", "", StringComparison.OrdinalIgnoreCase)
                    .Trim();

            if (!double.TryParse(
                numericPart,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double metres))
            {
                return 0;
            }

            return metres * 3.28084;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
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

            client.Timeout = TimeSpan.FromSeconds(35);

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
                [out:json][timeout:20];
                nwr["icao"="{cleanedIcao}"]["aeroway"];
                out center tags 1;
                """;

            var errors = new List<string>();

            foreach (string endpoint in OverpassEndpoints)
            {
                try
                {
                    AirportResult? result =
                        await TryEndpointAsync(
                            endpoint,
                            overpassQuery,
                            cleanedIcao,
                            cancellationToken);

                    if (result != null)
                        return result;
                }
                catch (OperationCanceledException)
                    when (!cancellationToken.IsCancellationRequested)
                {
                    errors.Add(
                        $"{endpoint}: request timed out");
                }
                catch (HttpRequestException ex)
                {
                    errors.Add(
                        $"{endpoint}: {ex.Message}");
                }
                catch (JsonException ex)
                {
                    errors.Add(
                        $"{endpoint}: invalid response ({ex.Message})");
                }
            }

            if (errors.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Airport {cleanedIcao} could not be looked up because " +
                    $"all OpenStreetMap servers were unavailable.\r\n\r\n" +
                    string.Join("\r\n", errors));
            }

            return null;
        }

        private static async Task<AirportResult?> TryEndpointAsync(
            string endpoint,
            string overpassQuery,
            string cleanedIcao,
            CancellationToken cancellationToken)
        {
            using var content =
                new StringContent(
                    overpassQuery,
                    Encoding.UTF8,
                    "application/x-www-form-urlencoded");

            using HttpResponseMessage response =
                await HttpClient.PostAsync(
                    endpoint,
                    content,
                    cancellationToken);

            if (IsTemporaryFailure(response.StatusCode))
            {
                throw new HttpRequestException(
                    $"Temporary server error: " +
                    $"{(int)response.StatusCode} " +
                    $"{response.ReasonPhrase}");
            }

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
                string osmName =
                    GetString(tags, "name");

                if (!string.IsNullOrWhiteSpace(osmName))
                    name = osmName;

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

        private static bool IsTemporaryFailure(
            HttpStatusCode statusCode)
        {
            return statusCode ==
                       HttpStatusCode.RequestTimeout ||
                   statusCode ==
                       HttpStatusCode.TooManyRequests ||
                   statusCode ==
                       HttpStatusCode.BadGateway ||
                   statusCode ==
                       HttpStatusCode.ServiceUnavailable ||
                   statusCode ==
                       HttpStatusCode.GatewayTimeout;
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

            string value = elevation.Trim();

            bool explicitlyFeet =
                value.EndsWith(
                    "ft",
                    StringComparison.OrdinalIgnoreCase) ||
                value.EndsWith(
                    "feet",
                    StringComparison.OrdinalIgnoreCase);

            value = value
                .Replace(
                    "feet",
                    "",
                    StringComparison.OrdinalIgnoreCase)
                .Replace(
                    "ft",
                    "",
                    StringComparison.OrdinalIgnoreCase)
                .Replace(
                    "m",
                    "",
                    StringComparison.OrdinalIgnoreCase)
                .Trim();

            if (!double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double elevationValue))
            {
                return 0;
            }

            return explicitlyFeet
                ? elevationValue
                : elevationValue * 3.28084;
        }
    }
}

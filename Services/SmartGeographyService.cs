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
    // Context-aware map resolver used by SMART routes. It keeps the normal
    // AirportService for ICAOs but improves ordinary-place disambiguation by
    // scoring Nominatim candidates using the exact spoken qualifiers and the
    // previous route position.
    public class SmartGeographyService
    {
        private static readonly HttpClient HttpClient = CreateHttpClient();
        private readonly AirportService _airportService;

        public SmartGeographyService(AirportService airportService)
        {
            _airportService = airportService;
        }

        public async Task<SmartMapLocation> ResolveAsync(
            string query,
            SmartMapLocation? routeContext = null,
            CancellationToken cancellationToken = default)
        {
            string text = (query ?? string.Empty).Trim();
            if (text.Length == 0)
                throw new InvalidOperationException("A route location was empty.");

            if (Regex.IsMatch(text, @"^[A-Za-z]{4}$"))
            {
                AirportResult? airport =
                    await _airportService.FindByIcaoAsync(text.ToUpperInvariant(), cancellationToken);

                if (airport != null)
                {
                    return new SmartMapLocation
                    {
                        Query = text,
                        Name = airport.Name,
                        DisplayName = airport.Name,
                        Ident = airport.Ident,
                        Latitude = airport.Latitude,
                        Longitude = airport.Longitude,
                        ElevationFeet = airport.ElevationFeet,
                        IsAirport = true
                    };
                }
            }

            List<NominatimCandidate> candidates =
                await SearchCandidatesAsync(text, cancellationToken);

            if (candidates.Count == 0)
                throw new InvalidOperationException($"{text} could not be found on the map.");

            string[] words = Tokenize(text);
            NominatimCandidate selected = candidates
                .OrderByDescending(c => ScoreCandidate(c, words, routeContext))
                .First();

            return new SmartMapLocation
            {
                Query = text,
                Name = selected.Name.Length > 0 ? selected.Name : text,
                DisplayName = selected.DisplayName,
                Latitude = selected.Latitude,
                Longitude = selected.Longitude,
                SouthLatitude = selected.South,
                NorthLatitude = selected.North,
                WestLongitude = selected.West,
                EastLongitude = selected.East,
                HasBoundingBox = selected.North > selected.South && selected.East > selected.West,
                OsmType = selected.OsmType,
                OsmId = selected.OsmId,
                Category = selected.Category,
                PlaceType = selected.Type
            };
        }

        public GeographyResult ToGeographyResult(SmartMapLocation location)
        {
            return new GeographyResult
            {
                Name = location.Query.Length > 0 ? location.Query : location.Name,
                DisplayName = location.DisplayName,
                Latitude = location.Latitude,
                Longitude = location.Longitude,
                SouthLatitude = location.SouthLatitude,
                NorthLatitude = location.NorthLatitude,
                WestLongitude = location.WestLongitude,
                EastLongitude = location.EastLongitude,
                OsmType = location.OsmType,
                OsmId = location.OsmId,
                Category = location.Category,
                PlaceType = location.PlaceType
            };
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("H145FlightPlanGenerator/2.0");
            client.Timeout = TimeSpan.FromSeconds(30);
            return client;
        }

        private static async Task<List<NominatimCandidate>> SearchCandidatesAsync(
            string query,
            CancellationToken cancellationToken)
        {
            string url =
                "https://nominatim.openstreetmap.org/search" +
                $"?q={Uri.EscapeDataString(query)}" +
                "&format=jsonv2" +
                "&limit=20" +
                "&dedupe=0" +
                "&addressdetails=1" +
                "&namedetails=1" +
                "&extratags=1" +
                "&polygon_geojson=0" +
                "&countrycodes=gb,im,ie,gg,je";

            using HttpResponseMessage response =
                await HttpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            using JsonDocument document = JsonDocument.Parse(json);

            var result = new List<NominatimCandidate>();
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return result;

            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                if (!TryDouble(element, "lat", out double lat) ||
                    !TryDouble(element, "lon", out double lon))
                    continue;

                var item = new NominatimCandidate
                {
                    Name = GetString(element, "name"),
                    DisplayName = GetString(element, "display_name"),
                    OsmType = GetString(element, "osm_type"),
                    OsmId = GetLong(element, "osm_id"),
                    Category = GetString(element, "category"),
                    Type = GetString(element, "type"),
                    Importance = GetDouble(element, "importance"),
                    Latitude = lat,
                    Longitude = lon
                };

                if (element.TryGetProperty("boundingbox", out JsonElement bbox) &&
                    bbox.ValueKind == JsonValueKind.Array &&
                    bbox.GetArrayLength() >= 4)
                {
                    item.South = ParseDouble(bbox[0]);
                    item.North = ParseDouble(bbox[1]);
                    item.West = ParseDouble(bbox[2]);
                    item.East = ParseDouble(bbox[3]);
                }

                result.Add(item);
            }

            return result;
        }

        private static double ScoreCandidate(
            NominatimCandidate candidate,
            string[] queryWords,
            SmartMapLocation? context)
        {
            double score = candidate.Importance * 50.0;
            string haystack = (candidate.DisplayName + " " + candidate.Name).ToLowerInvariant();

            foreach (string word in queryWords)
            {
                if (haystack.Contains(word, StringComparison.OrdinalIgnoreCase))
                    score += word.Length >= 5 ? 15 : 7;
                else
                    score -= 4;
            }

            // Prefer real settlements/land features over similarly named POIs.
            if (candidate.Category is "place" or "boundary")
                score += 20;

            if (candidate.Type is "city" or "town" or "village" or "island" or "county" or "administrative")
                score += 16;

            if (candidate.Category is "aeroway" &&
                !queryWords.Any(w => w is "airport" or "helipad" or "heliport"))
                score -= 35;

            // When the wording itself does not fully disambiguate, route
            // continuity is a useful tiebreaker. It never beats explicit words.
            if (context != null)
            {
                double distance = DistanceNm(
                    context.Latitude, context.Longitude,
                    candidate.Latitude, candidate.Longitude);
                score -= Math.Min(25.0, distance / 25.0);
            }

            return score;
        }

        private static string[] Tokenize(string text) =>
            Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9]+")
                .Select(m => m.Value)
                .Where(w => w.Length > 1 && w is not "the" and not "in" and not "of" and not "at")
                .Distinct()
                .ToArray();

        private static bool TryDouble(JsonElement element, string property, out double value)
        {
            value = 0;
            if (!element.TryGetProperty(property, out JsonElement p))
                return false;
            if (p.ValueKind == JsonValueKind.Number)
                return p.TryGetDouble(out value);
            if (p.ValueKind == JsonValueKind.String)
                return double.TryParse(p.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            return false;
        }

        private static double ParseDouble(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out double n))
                return n;
            if (element.ValueKind == JsonValueKind.String &&
                double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out n))
                return n;
            return 0;
        }

        private static string GetString(JsonElement element, string property) =>
            element.TryGetProperty(property, out JsonElement p) && p.ValueKind == JsonValueKind.String
                ? p.GetString() ?? string.Empty
                : string.Empty;

        private static long GetLong(JsonElement element, string property) =>
            element.TryGetProperty(property, out JsonElement p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out long n)
                ? n
                : 0;

        private static double GetDouble(JsonElement element, string property) =>
            element.TryGetProperty(property, out JsonElement p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out double n)
                ? n
                : 0;

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

        private sealed class NominatimCandidate
        {
            public string Name { get; set; } = string.Empty;
            public string DisplayName { get; set; } = string.Empty;
            public string OsmType { get; set; } = string.Empty;
            public long OsmId { get; set; }
            public string Category { get; set; } = string.Empty;
            public string Type { get; set; } = string.Empty;
            public double Importance { get; set; }
            public double Latitude { get; set; }
            public double Longitude { get; set; }
            public double South { get; set; }
            public double North { get; set; }
            public double West { get; set; }
            public double East { get; set; }
        }
    }

    public class SmartMapLocation
    {
        public string Query { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Ident { get; set; } = string.Empty;
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double ElevationFeet { get; set; }
        public bool IsAirport { get; set; }
        public bool HasBoundingBox { get; set; }
        public double SouthLatitude { get; set; }
        public double NorthLatitude { get; set; }
        public double WestLongitude { get; set; }
        public double EastLongitude { get; set; }
        public string OsmType { get; set; } = string.Empty;
        public long OsmId { get; set; }
        public string Category { get; set; } = string.Empty;
        public string PlaceType { get; set; } = string.Empty;
    }
}

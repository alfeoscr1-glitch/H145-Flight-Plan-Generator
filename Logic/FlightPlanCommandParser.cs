using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using H145FlightPlanner.Models;

namespace H145FlightPlanner.Logic
{
    public static class FlightPlanCommandParser
    {
        private static readonly HashSet<string> ReservedWords =
            new HashSet<string>(
                new[]
                {
                    "BACK","FROM","THEN","HEAD","FLY","OVER","NEXT",
                    "ONTO","WITH","MAKE","TAKE","LAND","AREA","CITY",
                    "TOWN","COAST"
                },
                StringComparer.OrdinalIgnoreCase);

        public static FlightPlanRequest Parse(string input)
        {
            var request = new FlightPlanRequest();

            if (string.IsNullOrWhiteSpace(input))
                return request;

            string text = input.Trim();

            request.RouteType = DetectRouteType(text);
            request.FlightRules = ExtractFlightRules(text);
            request.AltitudeFeet = ExtractAltitude(text);

            List<string> icaoCodes = ExtractIcaoCodes(text);

            if (icaoCodes.Count > 0)
                request.Departure = icaoCodes[0];

            if (request.RouteType.Equals("COASTLINE", StringComparison.OrdinalIgnoreCase))
            {
                request.CoastlineMode = DetectCoastlineMode(text);
                request.CoastlineLocation = ExtractCoastlineLocation(text);

                if (icaoCodes.Count > 1)
                    request.Destination = icaoCodes[1];

                string returnIcao = ExtractReturnIcao(text);

                if (!string.IsNullOrWhiteSpace(returnIcao))
                    request.ReturnLocation = returnIcao;
                else if (ContainsReturnInstruction(text))
                    request.ReturnLocation = request.Departure;
            }
            else if (request.RouteType.Equals("ORBIT", StringComparison.OrdinalIgnoreCase))
            {
                request.OrbitLocation = ExtractOrbitLocation(text);

                string? orbitIcao = ExtractOrbitIcao(text);
                if (!string.IsNullOrWhiteSpace(orbitIcao))
                    request.OrbitLocation = orbitIcao;

                string returnIcao = ExtractReturnIcao(text);
                if (!string.IsNullOrWhiteSpace(returnIcao))
                    request.ReturnLocation = returnIcao;
                else if (ContainsReturnInstruction(text))
                    request.ReturnLocation = request.Departure;

                request.Destination = ExtractContinueDestination(text);
            }
            else if (request.RouteType.Equals("DIRECT", StringComparison.OrdinalIgnoreCase))
            {
                if (icaoCodes.Count > 1)
                    request.Destination = icaoCodes[1];
            }
            else if (icaoCodes.Count > 1)
            {
                request.Destination = icaoCodes[^1];
            }

            request.RequestedLocations =
                BuildRequestedLocations(
                    icaoCodes,
                    request.OrbitLocation,
                    request.CoastlineLocation);

            return request;
        }

        private static string DetectRouteType(string text)
        {
            // Coastline first so "around the coastline" never becomes Orbit.
            if (Regex.IsMatch(
                text,
                @"\b(?:coast|coastline|coastal|shoreline|seaboard|sea\s+coast)\b",
                RegexOptions.IgnoreCase))
            {
                return "COASTLINE";
            }

            if (Regex.IsMatch(
                text,
                @"\b(?:orbit|orbits|orbited|orbiting|circle|circles|circled|circling)\b",
                RegexOptions.IgnoreCase))
            {
                return "ORBIT";
            }

            if (Regex.IsMatch(
                text,
                @"\b(?:make|do)\s+(?:a\s+)?circuit\s+(?:around|over)\b",
                RegexOptions.IgnoreCase))
            {
                return "ORBIT";
            }

            if (Regex.IsMatch(
                text,
                @"\bscenic\b|\bsightseeing\b",
                RegexOptions.IgnoreCase))
            {
                return "SCENIC";
            }

            if (Regex.IsMatch(
                text,
                @"\b(?:direct|directly|directing|directed)\b|\bstraight\s+to\b",
                RegexOptions.IgnoreCase))
            {
                return "DIRECT";
            }

            return "DIRECT";
        }

        private static string DetectCoastlineMode(string text)
        {
            if (Regex.IsMatch(
                text,
                @"\b(?:around|round|rounding|circle|circling)\b",
                RegexOptions.IgnoreCase))
            {
                return "AROUND";
            }

            return "ALONG";
        }

        private static string ExtractCoastlineLocation(string text)
        {
            string[] patterns =
            {
                @"\b(?:around|round|circle|circling)\s+(?:the\s+)?(?:coast|coastline|shoreline)\s+(?:of\s+)?(?<place>.+?)(?=\s*(?:,|\.|\bthen\b|\band\s+then\b|\breturn(?:ing)?\b|\bgo\s+back\b|\bhead\s+back\b|\bfly\s+back\b|\bat\s+\d|\b\d[\d,]*[\s-]*(?:feet|foot|ft)\b|\bVFR\b|\bIFR\b|$))",
                @"\b(?:follow|following|fly|flying)\s+(?:the\s+)?(?:coast|coastline|shoreline)\s+(?:around|round)\s+(?:the\s+)?(?<place>.+?)(?=\s*(?:,|\.|\bthen\b|\band\s+then\b|\breturn(?:ing)?\b|\bgo\s+back\b|\bhead\s+back\b|\bfly\s+back\b|\bat\s+\d|\b\d[\d,]*[\s-]*(?:feet|foot|ft)\b|\bVFR\b|\bIFR\b|$))",
                @"\b(?:coast|coastline|shoreline)\s+of\s+(?:the\s+)?(?<place>.+?)(?=\s*(?:,|\.|\bthen\b|\band\s+then\b|\breturn(?:ing)?\b|\bgo\s+back\b|\bhead\s+back\b|\bfly\s+back\b|\bat\s+\d|\b\d[\d,]*[\s-]*(?:feet|foot|ft)\b|\bVFR\b|\bIFR\b|$))"
            };

            foreach (string pattern in patterns)
            {
                Match match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    string place = CleanLocation(match.Groups["place"].Value);
                    place = Regex.Replace(
                        place,
                        @"^(?:the\s+)",
                        string.Empty,
                        RegexOptions.IgnoreCase);

                    return place.Trim();
                }
            }

            return string.Empty;
        }

        private static List<string> ExtractIcaoCodes(string text)
        {
            MatchCollection matches =
                Regex.Matches(
                    text,
                    @"(?<![A-Za-z0-9])[A-Z]{4}(?![A-Za-z0-9])");

            return matches
                .Select(match => match.Value.ToUpperInvariant())
                .Where(code => !ReservedWords.Contains(code))
                .ToList();
        }

        private static string ExtractFlightRules(string text)
        {
            if (Regex.IsMatch(text, @"\bVFR\b", RegexOptions.IgnoreCase))
                return "VFR";

            if (Regex.IsMatch(text, @"\bIFR\b", RegexOptions.IgnoreCase))
                return "IFR";

            return string.Empty;
        }

        private static int? ExtractAltitude(string text)
        {
            Match match = Regex.Match(
                text,
                @"\b(\d{1,3}(?:,\d{3})+|\d{2,5})[\s-]*(?:feet|foot|ft)\b",
                RegexOptions.IgnoreCase);

            if (!match.Success)
                return null;

            string number = match.Groups[1].Value.Replace(",", "");

            return int.TryParse(number, out int altitude)
                ? altitude
                : null;
        }

        private static string ExtractOrbitLocation(string text)
        {
            Match match = Regex.Match(
                text,
                @"\b(?:orbit|orbits|orbited|orbiting|circle|circles|circled|circling)\b\s*(?:around\s+|over\s+)?(?<place>.+?)(?=\s*(?:,|\.|\bthen\b|\band\s+then\b|\breturn(?:ing)?\b|\bgo\s+back\b|\bhead\s+back\b|\bfly\s+back\b|\bcontinue\b|\bproceed\b|\bat\s+\d|\b\d[\d,]*[\s-]*(?:feet|foot|ft)\b|\bVFR\b|\bIFR\b|$))",
                RegexOptions.IgnoreCase);

            return match.Success
                ? CleanLocation(match.Groups["place"].Value)
                : string.Empty;
        }

        private static string? ExtractOrbitIcao(string text)
        {
            Match match = Regex.Match(
                text,
                @"\b(?:orbit|orbits|orbited|orbiting|circle|circles|circled|circling)\b\s*(?:around\s+|over\s+)?(?<icao>[A-Z]{4})(?![A-Za-z0-9])",
                RegexOptions.IgnoreCase);

            if (!match.Success)
                return null;

            string code = match.Groups["icao"].Value.ToUpperInvariant();

            return ReservedWords.Contains(code) ? null : code;
        }

        private static string ExtractReturnIcao(string text)
        {
            Match match = Regex.Match(
                text,
                @"\b(?:return(?:ing)?|go\s+back|head\s+back|fly\s+back)\s+(?:to\s+)?(?<icao>[A-Z]{4})(?![A-Za-z0-9])",
                RegexOptions.IgnoreCase);

            if (!match.Success)
                return string.Empty;

            string code = match.Groups["icao"].Value.ToUpperInvariant();

            return ReservedWords.Contains(code) ? string.Empty : code;
        }

        private static string ExtractContinueDestination(string text)
        {
            Match match = Regex.Match(
                text,
                @"\b(?:continue|proceed)\s+(?:on\s+)?(?:to\s+)?(?<icao>[A-Z]{4})(?![A-Za-z0-9])",
                RegexOptions.IgnoreCase);

            if (!match.Success)
            {
                match = Regex.Match(
                    text,
                    @"\b(?:fly|head)\s+to\s+(?<icao>[A-Z]{4})(?![A-Za-z0-9])",
                    RegexOptions.IgnoreCase);
            }

            if (!match.Success)
                return string.Empty;

            string code = match.Groups["icao"].Value.ToUpperInvariant();

            return ReservedWords.Contains(code) ? string.Empty : code;
        }

        private static bool ContainsReturnInstruction(string text)
        {
            return Regex.IsMatch(
                text,
                @"\b(?:return|returning|go\s+back|head\s+back|fly\s+back)\b",
                RegexOptions.IgnoreCase);
        }

        private static List<string> BuildRequestedLocations(
            List<string> icaoCodes,
            string orbitLocation,
            string coastlineLocation)
        {
            var locations = new List<string>();

            foreach (string icao in icaoCodes)
            {
                if (!locations.Contains(
                    icao,
                    StringComparer.OrdinalIgnoreCase))
                {
                    locations.Add(icao);
                }
            }

            foreach (string place in new[] { orbitLocation, coastlineLocation })
            {
                if (!string.IsNullOrWhiteSpace(place) &&
                    !locations.Contains(
                        place,
                        StringComparer.OrdinalIgnoreCase))
                {
                    locations.Add(place);
                }
            }

            return locations;
        }

        private static string CleanLocation(string value)
        {
            string cleaned = value.Trim();

            cleaned = Regex.Replace(
                cleaned,
                @"^[\s,;:.]+",
                string.Empty);

            cleaned = Regex.Replace(
                cleaned,
                @"[\s,;:.]+$",
                string.Empty);

            return cleaned.Trim();
        }
    }
}

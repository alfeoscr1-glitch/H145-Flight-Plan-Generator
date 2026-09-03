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
                    "BACK",
                    "FROM",
                    "THEN",
                    "HEAD",
                    "FLY",
                    "OVER",
                    "NEXT",
                    "ONTO",
                    "WITH",
                    "MAKE",
                    "TAKE",
                    "LAND",
                    "AREA",
                    "CITY",
                    "TOWN"
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

            List<string> icaoCodes =
                ExtractIcaoCodes(text);

            if (icaoCodes.Count > 0)
            {
                request.Departure =
                    icaoCodes[0];
            }

            // -------------------------------------------------
            // ORBIT
            // -------------------------------------------------

            if (string.Equals(
                request.RouteType,
                "ORBIT",
                StringComparison.OrdinalIgnoreCase))
            {
                request.OrbitLocation =
                    ExtractOrbitLocation(text);

                string? orbitIcao =
                    ExtractOrbitIcao(text);

                if (!string.IsNullOrWhiteSpace(orbitIcao))
                {
                    request.OrbitLocation =
                        orbitIcao;
                }

                string returnIcao =
                    ExtractReturnIcao(text);

                if (!string.IsNullOrWhiteSpace(returnIcao))
                {
                    request.ReturnLocation =
                        returnIcao;
                }
                else if (ContainsReturnInstruction(text))
                {
                    request.ReturnLocation =
                        request.Departure;
                }

                request.Destination =
                    ExtractContinueDestination(text);
            }

            // -------------------------------------------------
            // AROUND
            // -------------------------------------------------

            else if (string.Equals(
                request.RouteType,
                "AROUND",
                StringComparison.OrdinalIgnoreCase))
            {
                request.AroundLocation =
                    ExtractAroundLocation(text);

                string returnIcao =
                    ExtractReturnIcao(text);

                if (!string.IsNullOrWhiteSpace(returnIcao))
                {
                    request.ReturnLocation =
                        returnIcao;
                }
                else if (ContainsReturnInstruction(text))
                {
                    request.ReturnLocation =
                        request.Departure;
                }

                request.Destination =
                    ExtractContinueDestination(text);
            }

            // -------------------------------------------------
            // DIRECT
            // -------------------------------------------------

            else if (string.Equals(
                request.RouteType,
                "DIRECT",
                StringComparison.OrdinalIgnoreCase))
            {
                if (icaoCodes.Count > 1)
                {
                    request.Destination =
                        icaoCodes[1];
                }
            }

            // -------------------------------------------------
            // OTHER ROUTE TYPES
            // -------------------------------------------------

            else
            {
                if (icaoCodes.Count > 1)
                {
                    request.Destination =
                        icaoCodes[^1];
                }
            }

            request.RequestedLocations =
                BuildRequestedLocations(
                    icaoCodes,
                    request.OrbitLocation,
                    request.AroundLocation);

            return request;
        }

        private static string DetectRouteType(
            string text)
        {
            // ORBIT
            if (Regex.IsMatch(
                text,
                @"\b(?:orbit|orbits|orbited|orbiting)\b",
                RegexOptions.IgnoreCase))
            {
                return "ORBIT";
            }

            if (Regex.IsMatch(
                text,
                @"\b(?:circle|circles|circled|circling)\b",
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

            // AROUND
            if (Regex.IsMatch(
                text,
                @"\b(?:go\s+around|going\s+around|fly\s+around|flying\s+around|around|rounding|round)\b",
                RegexOptions.IgnoreCase))
            {
                return "AROUND";
            }

            // COASTLINE
            if (Regex.IsMatch(
                text,
                @"\bcoast(?:line|al)?\b",
                RegexOptions.IgnoreCase))
            {
                return "COASTLINE";
            }

            // SCENIC
            if (Regex.IsMatch(
                text,
                @"\bscenic\b|\bsightseeing\b",
                RegexOptions.IgnoreCase))
            {
                return "SCENIC";
            }

            // DIRECT
            if (Regex.IsMatch(
                text,
                @"\b(?:direct|directly|directing|directed)\b",
                RegexOptions.IgnoreCase))
            {
                return "DIRECT";
            }

            if (Regex.IsMatch(
                text,
                @"\bstraight\s+to\b",
                RegexOptions.IgnoreCase))
            {
                return "DIRECT";
            }

            return "DIRECT";
        }

        private static List<string> ExtractIcaoCodes(
            string text)
        {
            MatchCollection matches =
                Regex.Matches(
                    text,
                    @"(?<![A-Za-z0-9])[A-Z]{4}(?![A-Za-z0-9])");

            return matches
                .Select(match =>
                    match.Value.ToUpperInvariant())
                .Where(code =>
                    !ReservedWords.Contains(code))
                .ToList();
        }

        private static string ExtractFlightRules(
            string text)
        {
            if (Regex.IsMatch(
                text,
                @"\bVFR\b",
                RegexOptions.IgnoreCase))
            {
                return "VFR";
            }

            if (Regex.IsMatch(
                text,
                @"\bIFR\b",
                RegexOptions.IgnoreCase))
            {
                return "IFR";
            }

            return string.Empty;
        }

        private static int? ExtractAltitude(
            string text)
        {
            Match match = Regex.Match(
                text,
                @"\b(\d{1,3}(?:,\d{3})+|\d{2,5})[\s-]*(?:feet|foot|ft)\b",
                RegexOptions.IgnoreCase);

            if (!match.Success)
                return null;

            string number =
                match.Groups[1]
                    .Value
                    .Replace(",", "");

            if (int.TryParse(
                number,
                out int altitude))
            {
                return altitude;
            }

            return null;
        }

        private static string ExtractOrbitLocation(
            string text)
        {
            Match match = Regex.Match(
                text,
                @"\b(?:orbit|orbits|orbited|orbiting|circle|circles|circled|circling)\b\s*(?:around\s+|over\s+)?(?<place>.+?)(?=\s*(?:,|\.|\bthen\b|\band\s+then\b|\breturn(?:ing)?\b|\bgo\s+back\b|\bhead\s+back\b|\bfly\s+back\b|\bcontinue\b|\bproceed\b|\bat\s+\d|\b\d[\d,]*[\s-]*(?:feet|foot|ft)\b|\bVFR\b|\bIFR\b|$))",
                RegexOptions.IgnoreCase);

            if (match.Success)
            {
                return CleanLocation(
                    match.Groups["place"].Value);
            }

            return string.Empty;
        }

        private static string ExtractAroundLocation(
            string text)
        {
            Match match = Regex.Match(
                text,
                @"\b(?:go\s+around|going\s+around|fly\s+around|flying\s+around|around|rounding|round)\b\s+(?:the\s+)?(?<place>.+?)(?=\s*(?:,|\.|\bthen\b|\band\s+then\b|\breturn(?:ing)?\b|\bgo\s+back\b|\bhead\s+back\b|\bfly\s+back\b|\bcontinue\b|\bproceed\b|\bat\s+\d|\b\d[\d,]*[\s-]*(?:feet|foot|ft)\b|\bVFR\b|\bIFR\b|$))",
                RegexOptions.IgnoreCase);

            if (!match.Success)
                return string.Empty;

            return CleanLocation(
                match.Groups["place"].Value);
        }

        private static string? ExtractOrbitIcao(
            string text)
        {
            Match match = Regex.Match(
                text,
                @"\b(?:orbit|orbits|orbited|orbiting|circle|circles|circled|circling)\b\s*(?:around\s+|over\s+)?(?<icao>[A-Z]{4})(?![A-Za-z0-9])",
                RegexOptions.IgnoreCase);

            if (!match.Success)
                return null;

            string code =
                match.Groups["icao"]
                    .Value
                    .ToUpperInvariant();

            if (ReservedWords.Contains(code))
                return null;

            return code;
        }

        private static string ExtractReturnIcao(
            string text)
        {
            Match match = Regex.Match(
                text,
                @"\b(?:return(?:ing)?|go\s+back|head\s+back|fly\s+back)\s+(?:to\s+)?(?<icao>[A-Z]{4})(?![A-Za-z0-9])",
                RegexOptions.IgnoreCase);

            if (!match.Success)
                return string.Empty;

            string code =
                match.Groups["icao"]
                    .Value
                    .ToUpperInvariant();

            if (ReservedWords.Contains(code))
                return string.Empty;

            return code;
        }

        private static string ExtractContinueDestination(
            string text)
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

            string code =
                match.Groups["icao"]
                    .Value
                    .ToUpperInvariant();

            if (ReservedWords.Contains(code))
                return string.Empty;

            return code;
        }

        private static bool ContainsReturnInstruction(
            string text)
        {
            return Regex.IsMatch(
                text,
                @"\b(?:return|returning|go\s+back|head\s+back|fly\s+back)\b",
                RegexOptions.IgnoreCase);
        }

        private static List<string> BuildRequestedLocations(
            List<string> icaoCodes,
            string orbitLocation,
            string aroundLocation)
        {
            var locations =
                new List<string>();

            foreach (string icao in icaoCodes)
            {
                if (!locations.Contains(
                    icao,
                    StringComparer.OrdinalIgnoreCase))
                {
                    locations.Add(icao);
                }
            }

            if (!string.IsNullOrWhiteSpace(
                orbitLocation) &&
                !locations.Contains(
                    orbitLocation,
                    StringComparer.OrdinalIgnoreCase))
            {
                locations.Add(
                    orbitLocation);
            }

            if (!string.IsNullOrWhiteSpace(
                aroundLocation) &&
                !locations.Contains(
                    aroundLocation,
                    StringComparer.OrdinalIgnoreCase))
            {
                locations.Add(
                    aroundLocation);
            }

            return locations;
        }

        private static string CleanLocation(
            string value)
        {
            string cleaned =
                value.Trim();

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

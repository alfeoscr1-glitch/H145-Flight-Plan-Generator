using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using H145FlightPlanner.Models;

namespace H145FlightPlanner.Logic
{
    public static class FlightPlanCommandParser
    {
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

                // Example:
                // EGCK -> orbit EGFD -> return EGCK
                //
                // Departure = EGCK
                // OrbitLocation = EGFD
                // ReturnLocation = EGCK

                if (icaoCodes.Count >= 2)
                {
                    string? orbitIcao =
                        ExtractOrbitIcao(text);

                    if (!string.IsNullOrWhiteSpace(orbitIcao))
                    {
                        request.OrbitLocation =
                            orbitIcao;
                    }
                }

                string returnIcao =
                    ExtractReturnIcao(text);

                if (!string.IsNullOrWhiteSpace(returnIcao))
                {
                    request.ReturnLocation =
                        returnIcao;
                }
                else if (icaoCodes.Count >= 3)
                {
                    request.ReturnLocation =
                        icaoCodes[^1];
                }

                // If the spoken command says "return"
                // but does not repeat an ICAO, return
                // to the departure airport.
                if (ContainsReturnInstruction(text) &&
                    string.IsNullOrWhiteSpace(
                        request.ReturnLocation))
                {
                    request.ReturnLocation =
                        request.Departure;
                }

                // Orbit mode does not need a normal
                // destination unless the user explicitly
                // continues to another airport.
                request.Destination =
                    ExtractContinueDestination(text);

                // If no separate destination exists,
                // the final airport will be ReturnLocation
                // or departure in OrbitRouteGenerator.
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
                    request.OrbitLocation);

            return request;
        }

        private static string DetectRouteType(
            string text)
        {
            // Orbit-specific wording.
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

            // Coastline mode.
            if (Regex.IsMatch(
                text,
                @"\bcoast(?:line|al)?\b",
                RegexOptions.IgnoreCase))
            {
                return "COASTLINE";
            }

            // Keep "fly around" for Scenic mode.
            // It is deliberately NOT treated as Orbit.
            if (Regex.IsMatch(
                text,
                @"\bscenic\b|\bsightseeing\b|\bfly\s+around\b",
                RegexOptions.IgnoreCase))
            {
                return "SCENIC";
            }

            // Explicit Direct phrases.
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

            // A normal A-to-B flight is Direct by default.
            return "DIRECT";
        }

        private static List<string> ExtractIcaoCodes(
            string text)
        {
            MatchCollection matches =
                Regex.Matches(
                    text,
                    @"(?<![A-Za-z0-9])[A-Z]{4}(?![A-Za-z0-9])");

            // Do NOT Distinct().
            //
            // We must keep repeated ICAOs so:
            // EGCK -> orbit EGFD -> return EGCK
            // retains both occurrences of EGCK.
            return matches
                .Select(match =>
                    match.Value.ToUpperInvariant())
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
            // Examples:
            // orbit Aberystwyth
            // orbiting Fishguard
            // orbit Aberystwyth helipad
            // circle around Birmingham
            // circle over London
            //
            // Importantly, "helipad", "heliport",
            // "airport", etc. remain part of the
            // extracted location.

            Match match = Regex.Match(
                text,
                @"\b(?:orbit|orbits|orbited|orbiting|circle|circles|circled|circling)\b\s*(?:around\s+|over\s+)?(?<place>.+?)(?=\s*(?:,|\.|\bthen\b|\band\s+then\b|\breturn(?:ing)?\b|\bgo\s+back\b|\bhead\s+back\b|\bcontinue\b|\bproceed\b|\bat\s+\d|\b\d[\d,]*[\s-]*(?:feet|foot|ft)\b|\bVFR\b|\bIFR\b|$))",
                RegexOptions.IgnoreCase);

            if (match.Success)
            {
                return CleanLocation(
                    match.Groups["place"].Value);
            }

            Match circuitMatch = Regex.Match(
                text,
                @"\b(?:make|do)\s+(?:a\s+)?circuit\s+(?:around|over)\s+(?<place>.+?)(?=\s*(?:,|\.|\bthen\b|\band\s+then\b|\breturn(?:ing)?\b|\bgo\s+back\b|\bhead\s+back\b|\bcontinue\b|\bproceed\b|\bat\s+\d|\b\d[\d,]*[\s-]*(?:feet|foot|ft)\b|\bVFR\b|\bIFR\b|$))",
                RegexOptions.IgnoreCase);

            if (circuitMatch.Success)
            {
                return CleanLocation(
                    circuitMatch.Groups["place"].Value);
            }

            return string.Empty;
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

            return match.Groups["icao"]
                .Value
                .ToUpperInvariant();
        }

        private static string ExtractReturnIcao(
            string text)
        {
            Match match = Regex.Match(
                text,
                @"\b(?:return(?:ing)?|go\s+back|head\s+back)\s+(?:to\s+)?(?<icao>[A-Z]{4})(?![A-Za-z0-9])",
                RegexOptions.IgnoreCase);

            if (!match.Success)
                return string.Empty;

            return match.Groups["icao"]
                .Value
                .ToUpperInvariant();
        }

        private static string ExtractContinueDestination(
            string text)
        {
            Match match = Regex.Match(
                text,
                @"\b(?:continue|proceed|fly|head)\s+(?:on\s+)?(?:to\s+)?(?<icao>[A-Z]{4})(?![A-Za-z0-9])",
                RegexOptions.IgnoreCase);

            if (!match.Success)
                return string.Empty;

            return match.Groups["icao"]
                .Value
                .ToUpperInvariant();
        }

        private static bool ContainsReturnInstruction(
            string text)
        {
            return Regex.IsMatch(
                text,
                @"\b(?:return|returning|go\s+back|head\s+back)\b",
                RegexOptions.IgnoreCase);
        }

        private static List<string> BuildRequestedLocations(
            List<string> icaoCodes,
            string orbitLocation)
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

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

            request.FlightRules = ExtractFlightRules(text);
            request.AltitudeFeet = ExtractAltitude(text);

            List<string> icaoCodes = ExtractIcaoCodes(text);

            if (icaoCodes.Count > 0)
                request.Departure = icaoCodes[0];

            if (icaoCodes.Count > 1)
                request.Destination = icaoCodes[1];

            if (icaoCodes.Count > 2)
                request.ReturnLocation = icaoCodes[^1];

            request.RouteType = DetectRouteType(text);

            request.OrbitLocation =
                ExtractOrbitLocation(text);

            // If this is an orbit request and the orbit target
            // itself is an ICAO, make sure that ICAO becomes
            // the orbit location rather than being mistaken
            // for the final destination.
            if (string.Equals(
                    request.RouteType,
                    "ORBIT",
                    StringComparison.OrdinalIgnoreCase) &&
                LooksLikeIcao(request.OrbitLocation))
            {
                if (icaoCodes.Count >= 2)
                {
                    request.Destination = string.Empty;

                    if (icaoCodes.Count >= 3)
                        request.ReturnLocation = icaoCodes[^1];
                }
            }

            request.RequestedLocations =
                ExtractRequestedLocations(
                    icaoCodes,
                    request.OrbitLocation);

            return request;
        }

        private static List<string> ExtractIcaoCodes(string text)
        {
            MatchCollection matches =
                Regex.Matches(
                    text,
                    @"(?<![A-Za-z0-9])[A-Z]{4}(?![A-Za-z0-9])");

            return matches
                .Select(match => match.Value)
                .ToList();
        }

        private static string ExtractFlightRules(string text)
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

        private static int? ExtractAltitude(string text)
        {
            Match match = Regex.Match(
                text,
                @"\b(\d{1,3}(?:,\d{3})+|\d{2,5})\s*(?:feet|foot|ft)\b",
                RegexOptions.IgnoreCase);

            if (!match.Success)
                return null;

            string number =
                match.Groups[1].Value.Replace(",", "");

            if (int.TryParse(
                number,
                out int altitude))
            {
                return altitude;
            }

            return null;
        }

        private static string DetectRouteType(string text)
        {
            if (Regex.IsMatch(
                text,
                @"\b(?:orbit|orbiting|orbited|circle|circling|circle\s+around|fly\s+around)\b",
                RegexOptions.IgnoreCase))
            {
                return "ORBIT";
            }

            if (Regex.IsMatch(
                text,
                @"\bcoast(?:line|al)?\b",
                RegexOptions.IgnoreCase))
            {
                return "COASTLINE";
            }

            if (Regex.IsMatch(
                text,
                @"\bscenic\b",
                RegexOptions.IgnoreCase))
            {
                return "SCENIC";
            }

            if (Regex.IsMatch(
                text,
                @"\b(?:direct|directing|directly|straight\s+to)\b",
                RegexOptions.IgnoreCase))
            {
                return "DIRECT";
            }

            return "DIRECT";
        }

        private static string ExtractOrbitLocation(string text)
        {
            Match match = Regex.Match(
                text,
                @"\b(?:orbit|orbiting|orbited|circle|circling|circle\s+around|fly\s+around)\s+(?:around\s+|over\s+)?(.+?)(?=,|\bthen\b|\band\s+then\b|\breturn(?:ing)?\b|\bgo\s+back\b|\bcontinue\b|$)",
                RegexOptions.IgnoreCase);

            if (!match.Success)
                return string.Empty;

            return CleanLocation(
                match.Groups[1].Value);
        }

        private static List<string> ExtractRequestedLocations(
            List<string> icaoCodes,
            string orbitLocation)
        {
            var locations =
                new List<string>();

            foreach (string code in icaoCodes)
            {
                locations.Add(code);
            }

            if (!string.IsNullOrWhiteSpace(orbitLocation))
            {
                locations.Add(orbitLocation);
            }

            return locations;
        }

        private static bool LooksLikeIcao(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            return Regex.IsMatch(
                value.Trim(),
                @"^[A-Z]{4}$",
                RegexOptions.IgnoreCase);
        }

        private static string CleanLocation(string value)
        {
            string cleaned = value.Trim();

            cleaned = Regex.Replace(
                cleaned,
                @"[.,;:]+$",
                string.Empty);

            return cleaned.Trim();
        }
    }
}

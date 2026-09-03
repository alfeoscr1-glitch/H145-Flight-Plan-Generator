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

            request.OrbitLocation = ExtractOrbitLocation(text);

            request.RequestedLocations =
                ExtractRequestedLocations(
                    text,
                    icaoCodes,
                    request.OrbitLocation);

            return request;
        }

        private static List<string> ExtractIcaoCodes(string text)
        {
            MatchCollection matches =
                Regex.Matches(
                    text.ToUpperInvariant(),
                    @"\b[A-Z]{4}\b");

            return matches
                .Select(match => match.Value)
                .Distinct()
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
                @"\b(\d{2,5})\s*(?:feet|foot|ft)\b",
                RegexOptions.IgnoreCase);

            if (!match.Success)
                return null;

            if (int.TryParse(
                match.Groups[1].Value,
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
                @"\borbit(?:ing)?\b",
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
                @"\bscenic\b|\bfly around\b",
                RegexOptions.IgnoreCase))
            {
                return "SCENIC";
            }

            return "DIRECT";
        }

        private static string ExtractOrbitLocation(string text)
        {
            Match match = Regex.Match(
                text,
                @"\borbit(?:ing)?\s+(.+?)(?=,|\bthen\b|\band\b|\breturn(?:ing)?\b|$)",
                RegexOptions.IgnoreCase);

            if (!match.Success)
                return string.Empty;

            return CleanLocation(match.Groups[1].Value);
        }

        private static List<string> ExtractRequestedLocations(
            string text,
            List<string> icaoCodes,
            string orbitLocation)
        {
            var locations = new List<string>();

            foreach (string code in icaoCodes)
            {
                if (!locations.Contains(
                    code,
                    StringComparer.OrdinalIgnoreCase))
                {
                    locations.Add(code);
                }
            }

            if (!string.IsNullOrWhiteSpace(orbitLocation))
            {
                if (!locations.Contains(
                    orbitLocation,
                    StringComparer.OrdinalIgnoreCase))
                {
                    locations.Add(orbitLocation);
                }
            }

            return locations;
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

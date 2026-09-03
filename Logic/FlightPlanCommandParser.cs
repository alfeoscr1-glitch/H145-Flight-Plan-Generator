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

            // If the first and last ICAO are the same,
            // this can later represent a return-to-origin route.
            if (icaoCodes.Count > 2)
                request.ReturnLocation = icaoCodes[^1];

            request.RouteType = DetectRouteType(text);

            request.OrbitLocation = ExtractOrbitLocation(text);

            request.RequestedLocations =
                ExtractRequestedLocations(
                    icaoCodes,
                    request.OrbitLocation);

            return request;
        }

        private static List<string> ExtractIcaoCodes(string text)
        {
            /*
             * IMPORTANT:
             *
             * We deliberately DO NOT convert the whole sentence
             * to uppercase.
             *
             * Whisper normally writes aviation identifiers such
             * as EGCK, EGFA, EGNS, EGQS in uppercase.
             *
             * Normal words such as:
             *
             * flight
             * plan
             * from
             * then
             *
             * remain normal words and therefore cannot accidentally
             * become ICAO identifiers.
             *
             * This is generic behaviour. There is no list of
             * hardcoded airports or ignored English words here.
             */

            MatchCollection matches =
                Regex.Matches(
                    text,
                    @"(?<![A-Za-z0-9])[A-Z]{4}(?![A-Za-z0-9])");

            return matches
                .Select(match => match.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
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
            /*
             * Handles:
             *
             * 1000 feet
             * 1000 ft
             * 1,000 feet
             * 1500 foot
             */

            Match match = Regex.Match(
                text,
                @"\b(\d{1,3}(?:,\d{3})+|\d{2,5})\s*(?:feet|foot|ft)\b",
                RegexOptions.IgnoreCase);

            if (!match.Success)
                return null;

            string number =
                match.Groups[1].Value.Replace(",", "");

            if (int.TryParse(number, out int altitude))
                return altitude;

            return null;
        }

        private static string DetectRouteType(string text)
        {
            /*
             * These are route concepts, not complete sentences.
             *
             * We are not teaching the program:
             *
             * "Create a flight plan from X to Y..."
             *
             * We are only detecting general route intentions.
             */

            if (Regex.IsMatch(
                text,
                @"\borbit(?:s|ed|ing)?\b",
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
                @"\bscenic\b|\bfly\s+around\b",
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
                @"\borbit(?:s|ed|ing)?\s+(?:around\s+|over\s+)?(.+?)(?=,|\bthen\b|\breturn(?:ing)?\b|\bcontinue\b|$)",
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

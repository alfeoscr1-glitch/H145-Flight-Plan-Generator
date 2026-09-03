using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using H145FlightPlanner.Models;
using H145FlightPlanner.Services;

namespace H145FlightPlanner.Routing
{
    public class SmartRouteGenerator
    {
        private readonly AirportService _airportService;
        private readonly GeographyService _geographyService;
        private readonly CoastlineGeometryService _coastlineGeometryService;

        public SmartRouteGenerator(
            AirportService airportService,
            GeographyService geographyService,
            CoastlineGeometryService coastlineGeometryService)
        {
            _airportService = airportService;
            _geographyService = geographyService;
            _coastlineGeometryService = coastlineGeometryService;
        }

        public async Task<GeneratedFlightPlan> GenerateAsync(
            FlightPlanRequest request,
            CancellationToken cancellationToken = default)
        {
            SmartRoutePlan plan = request.SmartPlan ??
                throw new InvalidOperationException("No interpreted smart route plan was supplied.");

            int altitude = plan.AltitudeFeet ?? request.AltitudeFeet ?? 1000;
            string rules = !string.IsNullOrWhiteSpace(plan.FlightRules)
                ? plan.FlightRules.ToUpperInvariant()
                : (!string.IsNullOrWhiteSpace(request.FlightRules)
                    ? request.FlightRules.ToUpperInvariant()
                    : "VFR");

            var flightPlan = new GeneratedFlightPlan
            {
                FlightRules = rules,
                CruisingAltitudeFeet = altitude
            };

            ResolvedLocation current =
                await ResolveAsync(plan.Start, cancellationToken);

            AddResolvedWaypoint(flightPlan, current, altitude, "START");

            int coastlineNumber = 1;
            int userNumber = 1;

            foreach (SmartRouteStep step in plan.Steps)
            {
                string action = (step.Action ?? string.Empty).Trim().ToUpperInvariant();

                if (action == "DIRECT")
                {
                    string targetText = FirstNonEmpty(step.To, step.Location);
                    if (string.IsNullOrWhiteSpace(targetText))
                        continue;

                    ResolvedLocation target = await ResolveAsync(targetText, cancellationToken);
                    AddResolvedWaypoint(flightPlan, target, altitude, $"USR{userNumber++:000}");
                    current = target;
                    continue;
                }

                if (action == "COASTLINE_AROUND")
                {
                    string areaName = FirstNonEmpty(step.Location, step.To);
                    if (string.IsNullOrWhiteSpace(areaName))
                        continue;

                    GeographyResult? area =
                        await _geographyService.FindPlaceAsync(areaName, cancellationToken);

                    if (area == null)
                        throw new InvalidOperationException($"{areaName} could not be found on the map.");

                    CoastlineGeometry geometry =
                        await _coastlineGeometryService.GetAroundCoastlineAsync(area, cancellationToken);

                    AddGeometry(flightPlan, geometry, altitude, ref coastlineNumber);

                    if (geometry.Points.Count > 0)
                    {
                        CoastlinePoint last = geometry.Points[^1];
                        current = ResolvedLocation.User(areaName, last.Latitude, last.Longitude);
                    }

                    continue;
                }

                if (action == "COASTLINE_ALONG")
                {
                    ResolvedLocation from = current;

                    if (!string.IsNullOrWhiteSpace(step.From))
                    {
                        from = await ResolveAsync(step.From, cancellationToken);

                        if (!SameLocation(current, from))
                        {
                            AddResolvedWaypoint(
                                flightPlan,
                                from,
                                altitude,
                                $"USR{userNumber++:000}");
                        }
                    }

                    string toText = FirstNonEmpty(step.To, step.Location);
                    if (string.IsNullOrWhiteSpace(toText))
                        throw new InvalidOperationException("A coastline-following step had no destination.");

                    ResolvedLocation to = await ResolveAsync(toText, cancellationToken);

                    CoastlineGeometry geometry =
                        await _coastlineGeometryService.GetAlongCoastlineAsync(
                            from.Latitude,
                            from.Longitude,
                            to.Latitude,
                            to.Longitude,
                            cancellationToken);

                    AddGeometry(flightPlan, geometry, altitude, ref coastlineNumber);
                    AddResolvedWaypoint(flightPlan, to, altitude, $"USR{userNumber++:000}");
                    current = to;
                    continue;
                }

                if (action == "ORBIT")
                {
                    string targetText = FirstNonEmpty(step.Location, step.To);
                    if (string.IsNullOrWhiteSpace(targetText))
                        continue;

                    ResolvedLocation target = await ResolveAsync(targetText, cancellationToken);
                    AddSimpleOrbit(flightPlan, target, altitude, ref userNumber);
                    current = target;
                    continue;
                }

                if (action == "RETURN" || action == "END")
                {
                    string targetText = FirstNonEmpty(step.To, step.Location, plan.End);
                    if (string.IsNullOrWhiteSpace(targetText))
                        continue;

                    ResolvedLocation target = await ResolveAsync(targetText, cancellationToken);
                    AddResolvedWaypoint(flightPlan, target, altitude, $"USR{userNumber++:000}");
                    current = target;
                }
            }

            if (!string.IsNullOrWhiteSpace(plan.End))
            {
                ResolvedLocation final = await ResolveAsync(plan.End, cancellationToken);
                if (!SameLocation(current, final))
                    AddResolvedWaypoint(flightPlan, final, altitude, $"USR{userNumber++:000}");
            }

            if (flightPlan.Waypoints.Count < 2)
                throw new InvalidOperationException("The interpreted route did not produce enough waypoints.");

            return flightPlan;
        }

        private async Task<ResolvedLocation> ResolveAsync(
            string value,
            CancellationToken cancellationToken)
        {
            string text = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException("A route location was empty.");

            if (Regex.IsMatch(text, @"^[A-Za-z]{4}$"))
            {
                AirportResult? airport =
                    await _airportService.FindByIcaoAsync(text.ToUpperInvariant(), cancellationToken);

                if (airport != null)
                    return ResolvedLocation.FromAirport(airport);
            }

            GeographyResult? place =
                await _geographyService.FindPlaceAsync(text, cancellationToken);

            if (place == null)
                throw new InvalidOperationException($"{text} could not be found.");

            return ResolvedLocation.User(
                place.DisplayName.Length > 0 ? place.DisplayName : text,
                place.Latitude,
                place.Longitude);
        }

        private static void AddGeometry(
            GeneratedFlightPlan flightPlan,
            CoastlineGeometry geometry,
            int altitude,
            ref int coastlineNumber)
        {
            foreach (CoastlinePoint point in geometry.Points)
            {
                if (flightPlan.Waypoints.Count > 0)
                {
                    RouteWaypoint previous = flightPlan.Waypoints[^1];
                    if (DistanceNm(previous.Latitude, previous.Longitude, point.Latitude, point.Longitude) < 0.005)
                        continue;
                }

                flightPlan.Waypoints.Add(new RouteWaypoint
                {
                    Name = "Detailed outer edge",
                    Ident = $"CST{coastlineNumber++:000}",
                    Type = "USER",
                    Latitude = point.Latitude,
                    Longitude = point.Longitude,
                    AltitudeFeet = altitude
                });
            }
        }

        private static void AddResolvedWaypoint(
            GeneratedFlightPlan flightPlan,
            ResolvedLocation location,
            int altitude,
            string fallbackIdent)
        {
            if (flightPlan.Waypoints.Count > 0)
            {
                RouteWaypoint previous = flightPlan.Waypoints[^1];
                if (DistanceNm(previous.Latitude, previous.Longitude, location.Latitude, location.Longitude) < 0.01)
                    return;
            }

            flightPlan.Waypoints.Add(new RouteWaypoint
            {
                Name = location.Name,
                Ident = string.IsNullOrWhiteSpace(location.Ident) ? fallbackIdent : location.Ident,
                Type = location.IsAirport ? "AIRPORT" : "USER",
                Latitude = location.Latitude,
                Longitude = location.Longitude,
                AltitudeFeet = location.IsAirport ? location.ElevationFeet : altitude
            });
        }

        private static void AddSimpleOrbit(
            GeneratedFlightPlan flightPlan,
            ResolvedLocation centre,
            int altitude,
            ref int userNumber)
        {
            const double radiusNm = 1.0;
            const int points = 24;

            for (int i = 0; i <= points; i++)
            {
                double angle = i * 360.0 / points;
                (double lat, double lon) = DestinationPoint(
                    centre.Latitude, centre.Longitude, angle, radiusNm);

                flightPlan.Waypoints.Add(new RouteWaypoint
                {
                    Name = $"Orbit {centre.Name}",
                    Ident = $"USR{userNumber++:000}",
                    Type = "USER",
                    Latitude = lat,
                    Longitude = lon,
                    AltitudeFeet = altitude
                });
            }
        }

        private static bool SameLocation(ResolvedLocation a, ResolvedLocation b) =>
            DistanceNm(a.Latitude, a.Longitude, b.Latitude, b.Longitude) < 0.05;

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values)
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            return string.Empty;
        }

        private static double DistanceNm(
            double lat1, double lon1, double lat2, double lon2)
        {
            const double r = 3440.065;
            double p1 = lat1 * Math.PI / 180.0;
            double p2 = lat2 * Math.PI / 180.0;
            double dp = (lat2 - lat1) * Math.PI / 180.0;
            double dl = (lon2 - lon1) * Math.PI / 180.0;
            double a = Math.Sin(dp / 2) * Math.Sin(dp / 2) +
                       Math.Cos(p1) * Math.Cos(p2) *
                       Math.Sin(dl / 2) * Math.Sin(dl / 2);
            return r * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        }

        private static (double Latitude, double Longitude) DestinationPoint(
            double latitude,
            double longitude,
            double bearingDegrees,
            double distanceNm)
        {
            const double r = 3440.065;
            double d = distanceNm / r;
            double b = bearingDegrees * Math.PI / 180.0;
            double p1 = latitude * Math.PI / 180.0;
            double l1 = longitude * Math.PI / 180.0;

            double p2 = Math.Asin(
                Math.Sin(p1) * Math.Cos(d) +
                Math.Cos(p1) * Math.Sin(d) * Math.Cos(b));

            double l2 = l1 + Math.Atan2(
                Math.Sin(b) * Math.Sin(d) * Math.Cos(p1),
                Math.Cos(d) - Math.Sin(p1) * Math.Sin(p2));

            return (p2 * 180.0 / Math.PI, l2 * 180.0 / Math.PI);
        }

        private sealed class ResolvedLocation
        {
            public string Name { get; set; } = string.Empty;
            public string Ident { get; set; } = string.Empty;
            public double Latitude { get; set; }
            public double Longitude { get; set; }
            public double ElevationFeet { get; set; }
            public bool IsAirport { get; set; }

            public static ResolvedLocation FromAirport(AirportResult airport) =>
                new ResolvedLocation
                {
                    Name = airport.Name,
                    Ident = airport.Ident,
                    Latitude = airport.Latitude,
                    Longitude = airport.Longitude,
                    ElevationFeet = airport.ElevationFeet,
                    IsAirport = true
                };

            public static ResolvedLocation User(string name, double latitude, double longitude) =>
                new ResolvedLocation
                {
                    Name = name,
                    Latitude = latitude,
                    Longitude = longitude,
                    IsAirport = false
                };
        }
    }
}

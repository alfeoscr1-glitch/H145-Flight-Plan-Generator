using System;
using System.Threading;
using System.Threading.Tasks;
using H145FlightPlanner.Models;
using H145FlightPlanner.Services;

namespace H145FlightPlanner.Routing
{
    // Executes the ordered plan returned by the invisible local AI. It supports
    // an arbitrary number of route legs; there is no hardcoded 2- or 3-place
    // limit.
    public class SmartRouteGenerator
    {
        private readonly AirportService _airportService;
        private readonly GeographyService _geographyService;
        private readonly CoastlineGeometryService _coastlineGeometryService;
        private readonly SmartGeographyService _smartGeographyService;

        public SmartRouteGenerator(
            AirportService airportService,
            GeographyService geographyService,
            CoastlineGeometryService coastlineGeometryService)
        {
            _airportService = airportService;
            _geographyService = geographyService;
            _coastlineGeometryService = coastlineGeometryService;
            _smartGeographyService = new SmartGeographyService(airportService);
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

            SmartMapLocation current =
                await _smartGeographyService.ResolveAsync(plan.Start, null, cancellationToken);

            AddLocationWaypoint(flightPlan, current, altitude, "START");

            int coastlineNumber = 1;
            int userNumber = 1;

            foreach (SmartRouteStep step in plan.Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string action = (step.Action ?? string.Empty).Trim().ToUpperInvariant();

                if (action == "DIRECT")
                {
                    string targetText = FirstNonEmpty(step.To, step.Location);
                    if (targetText.Length == 0)
                        continue;

                    SmartMapLocation target =
                        await _smartGeographyService.ResolveAsync(targetText, current, cancellationToken);
                    AddLocationWaypoint(flightPlan, target, altitude, $"USR{userNumber++:000}");
                    current = target;
                    continue;
                }

                if (action == "COASTLINE_ALONG")
                {
                    SmartMapLocation from = current;

                    if (!string.IsNullOrWhiteSpace(step.From))
                    {
                        SmartMapLocation explicitFrom =
                            await _smartGeographyService.ResolveAsync(step.From, current, cancellationToken);

                        if (!SameLocation(current, explicitFrom))
                            AddLocationWaypoint(flightPlan, explicitFrom, altitude, $"USR{userNumber++:000}");

                        from = explicitFrom;
                    }

                    string toText = FirstNonEmpty(step.To, step.Location);
                    if (toText.Length == 0)
                        throw new InvalidOperationException("A coastline-following step did not contain a destination.");

                    SmartMapLocation to =
                        await _smartGeographyService.ResolveAsync(toText, from, cancellationToken);

                    CoastlineGeometry geometry =
                        await _coastlineGeometryService.GetAlongCoastlineAsync(
                            from.Latitude, from.Longitude,
                            to.Latitude, to.Longitude,
                            cancellationToken);

                    AddGeometry(flightPlan, geometry, altitude, ref coastlineNumber);
                    AddLocationWaypoint(flightPlan, to, altitude, $"USR{userNumber++:000}");
                    current = to;
                    continue;
                }

                if (action == "COASTLINE_AROUND")
                {
                    string areaText = FirstNonEmpty(step.Location, step.To);
                    if (areaText.Length == 0)
                        throw new InvalidOperationException("A coastline-around step did not contain a landmass/place.");

                    SmartMapLocation areaLocation =
                        await _smartGeographyService.ResolveAsync(areaText, current, cancellationToken);
                    GeographyResult area = _smartGeographyService.ToGeographyResult(areaLocation);

                    CoastlineGeometry geometry =
                        await _coastlineGeometryService.GetAroundCoastlineAsync(area, cancellationToken);

                    AddGeometry(flightPlan, geometry, altitude, ref coastlineNumber);

                    if (geometry.Points.Count > 0)
                    {
                        CoastlinePoint last = geometry.Points[^1];
                        current = new SmartMapLocation
                        {
                            Query = areaText,
                            Name = areaLocation.Name,
                            DisplayName = areaLocation.DisplayName,
                            Latitude = last.Latitude,
                            Longitude = last.Longitude
                        };
                    }
                    continue;
                }

                if (action == "ORBIT")
                {
                    string targetText = FirstNonEmpty(step.Location, step.To);
                    if (targetText.Length == 0)
                        continue;

                    SmartMapLocation target =
                        await _smartGeographyService.ResolveAsync(targetText, current, cancellationToken);
                    AddSimpleOrbit(flightPlan, target, altitude, ref userNumber);
                    current = target;
                    continue;
                }

                if (action == "RETURN" || action == "END")
                {
                    string targetText = FirstNonEmpty(step.To, step.Location, plan.End);
                    if (targetText.Length == 0)
                        continue;

                    SmartMapLocation target =
                        await _smartGeographyService.ResolveAsync(targetText, current, cancellationToken);
                    AddLocationWaypoint(flightPlan, target, altitude, $"USR{userNumber++:000}");
                    current = target;
                }
            }

            if (!string.IsNullOrWhiteSpace(plan.End))
            {
                SmartMapLocation final =
                    await _smartGeographyService.ResolveAsync(plan.End, current, cancellationToken);
                if (!SameLocation(current, final))
                    AddLocationWaypoint(flightPlan, final, altitude, $"USR{userNumber++:000}");
            }

            if (flightPlan.Waypoints.Count < 2)
                throw new InvalidOperationException("The interpreted route did not produce enough waypoints.");

            return flightPlan;
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
                    if (DistanceNm(previous.Latitude, previous.Longitude, point.Latitude, point.Longitude) < 0.003)
                        continue;
                }

                flightPlan.Waypoints.Add(new RouteWaypoint
                {
                    Name = "Detailed coast edge",
                    Ident = $"CST{coastlineNumber++:0000}",
                    Type = "USER",
                    Latitude = point.Latitude,
                    Longitude = point.Longitude,
                    AltitudeFeet = altitude
                });
            }
        }

        private static void AddLocationWaypoint(
            GeneratedFlightPlan flightPlan,
            SmartMapLocation location,
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
                Name = location.DisplayName.Length > 0 ? location.DisplayName : location.Name,
                Ident = location.Ident.Length > 0 ? location.Ident : fallbackIdent,
                Type = location.IsAirport ? "AIRPORT" : "USER",
                Latitude = location.Latitude,
                Longitude = location.Longitude,
                AltitudeFeet = location.IsAirport ? location.ElevationFeet : altitude
            });
        }

        private static void AddSimpleOrbit(
            GeneratedFlightPlan flightPlan,
            SmartMapLocation centre,
            int altitude,
            ref int userNumber)
        {
            const double radiusNm = 1.0;
            const int pointCount = 36;

            for (int i = 0; i <= pointCount; i++)
            {
                double bearing = i * 360.0 / pointCount;
                (double lat, double lon) = Destination(
                    centre.Latitude, centre.Longitude, bearing, radiusNm);

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

        private static bool SameLocation(SmartMapLocation a, SmartMapLocation b) =>
            DistanceNm(a.Latitude, a.Longitude, b.Latitude, b.Longitude) < 0.05;

        private static string FirstNonEmpty(params string?[] values)
        {
            foreach (string? value in values)
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            return string.Empty;
        }

        private static (double Lat, double Lon) Destination(
            double latitude,
            double longitude,
            double bearingDegrees,
            double distanceNm)
        {
            const double radius = 3440.065;
            double angular = distanceNm / radius;
            double bearing = bearingDegrees * Math.PI / 180.0;
            double lat1 = latitude * Math.PI / 180.0;
            double lon1 = longitude * Math.PI / 180.0;
            double lat2 = Math.Asin(Math.Sin(lat1) * Math.Cos(angular) + Math.Cos(lat1) * Math.Sin(angular) * Math.Cos(bearing));
            double lon2 = lon1 + Math.Atan2(Math.Sin(bearing) * Math.Sin(angular) * Math.Cos(lat1), Math.Cos(angular) - Math.Sin(lat1) * Math.Sin(lat2));
            return (lat2 * 180.0 / Math.PI, lon2 * 180.0 / Math.PI);
        }

        private static double DistanceNm(double lat1, double lon1, double lat2, double lon2)
        {
            const double radius = 3440.065;
            double p1 = lat1 * Math.PI / 180.0;
            double p2 = lat2 * Math.PI / 180.0;
            double dp = (lat2 - lat1) * Math.PI / 180.0;
            double dl = (lon2 - lon1) * Math.PI / 180.0;
            double a = Math.Sin(dp / 2) * Math.Sin(dp / 2) + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
            return radius * 2.0 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1.0 - a));
        }
    }
}

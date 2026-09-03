using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using H145FlightPlanner.Models;
using H145FlightPlanner.Services;

namespace H145FlightPlanner.Routing
{
    public class OrbitRouteGenerator
    {
        private readonly AirportService _airportService;
        private readonly GeographyService _geographyService;

        public OrbitRouteGenerator(
            AirportService airportService,
            GeographyService geographyService)
        {
            _airportService = airportService;
            _geographyService = geographyService;
        }

        public async Task<GeneratedFlightPlan> GenerateAsync(
            FlightPlanRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (string.IsNullOrWhiteSpace(request.Departure))
            {
                throw new InvalidOperationException(
                    "No departure airport was found.");
            }

            if (string.IsNullOrWhiteSpace(request.OrbitLocation))
            {
                throw new InvalidOperationException(
                    "No orbit location was found.");
            }

            AirportResult? departure =
                await _airportService.FindByIcaoAsync(
                    request.Departure,
                    cancellationToken);

            if (departure == null)
            {
                throw new InvalidOperationException(
                    $"Departure airport {request.Departure} could not be found.");
            }

            int altitude =
                request.AltitudeFeet ?? 1000;

            string flightRules =
                string.IsNullOrWhiteSpace(request.FlightRules)
                    ? "VFR"
                    : request.FlightRules.ToUpperInvariant();

            var flightPlan = new GeneratedFlightPlan
            {
                FlightRules = flightRules,
                CruisingAltitudeFeet = altitude
            };

            flightPlan.Waypoints.Add(
                new RouteWaypoint
                {
                    Name = departure.Name,
                    Ident = departure.Ident,
                    Type = "AIRPORT",
                    Latitude = departure.Latitude,
                    Longitude = departure.Longitude,
                    AltitudeFeet = departure.ElevationFeet
                });

            int waypointNumber = 1;

            // -------------------------------------------------
            // ORBIT TARGET
            // -------------------------------------------------

            if (LooksLikeIcao(request.OrbitLocation))
            {
                AirportResult? orbitAirport =
                    await _airportService.FindByIcaoAsync(
                        request.OrbitLocation,
                        cancellationToken);

                if (orbitAirport == null)
                {
                    throw new InvalidOperationException(
                        $"Orbit airport {request.OrbitLocation} could not be found.");
                }

                AddCircularOrbit(
                    flightPlan,
                    orbitAirport.Latitude,
                    orbitAirport.Longitude,
                    1.0,
                    altitude,
                    ref waypointNumber);
            }
            else
            {
                GeographyResult? place =
                    await _geographyService.FindPlaceAsync(
                        request.OrbitLocation,
                        cancellationToken);

                if (place == null)
                {
                    throw new InvalidOperationException(
                        $"Orbit location {request.OrbitLocation} could not be found.");
                }

                if (place.HasBoundingBox)
                {
                    AddBoundingAreaOrbit(
                        flightPlan,
                        place,
                        altitude,
                        ref waypointNumber);
                }
                else
                {
                    AddCircularOrbit(
                        flightPlan,
                        place.Latitude,
                        place.Longitude,
                        1.0,
                        altitude,
                        ref waypointNumber);
                }
            }

            // -------------------------------------------------
            // FINAL AIRPORT
            // -------------------------------------------------

            string finalAirportIdent =
                !string.IsNullOrWhiteSpace(request.ReturnLocation)
                    ? request.ReturnLocation
                    : request.Destination;

            if (string.IsNullOrWhiteSpace(finalAirportIdent))
                finalAirportIdent = request.Departure;

            AirportResult? finalAirport =
                await _airportService.FindByIcaoAsync(
                    finalAirportIdent,
                    cancellationToken);

            if (finalAirport == null)
            {
                throw new InvalidOperationException(
                    $"Final airport {finalAirportIdent} could not be found.");
            }

            flightPlan.Waypoints.Add(
                new RouteWaypoint
                {
                    Name = finalAirport.Name,
                    Ident = finalAirport.Ident,
                    Type = "AIRPORT",
                    Latitude = finalAirport.Latitude,
                    Longitude = finalAirport.Longitude,
                    AltitudeFeet = finalAirport.ElevationFeet
                });

            return flightPlan;
        }

        private static bool LooksLikeIcao(string value)
        {
            return Regex.IsMatch(
                value.Trim(),
                @"^[A-Z]{4}$",
                RegexOptions.IgnoreCase);
        }

        private static void AddBoundingAreaOrbit(
            GeneratedFlightPlan flightPlan,
            GeographyResult place,
            int altitude,
            ref int waypointNumber)
        {
            double centreLatitude =
                (place.NorthLatitude + place.SouthLatitude) / 2.0;

            double centreLongitude =
                (place.EastLongitude + place.WestLongitude) / 2.0;

            double halfLatitude =
                (place.NorthLatitude - place.SouthLatitude) / 2.0;

            double halfLongitude =
                (place.EastLongitude - place.WestLongitude) / 2.0;

            // Keep the orbit just outside the returned place bounds.
            halfLatitude *= 1.20;
            halfLongitude *= 1.20;

            double approximateSizeNm =
                EstimateSizeNm(
                    place.SouthLatitude,
                    place.NorthLatitude,
                    place.WestLongitude,
                    place.EastLongitude);

            int pointCount;

            if (approximateSizeNm < 3)
                pointCount = 8;
            else if (approximateSizeNm < 10)
                pointCount = 12;
            else if (approximateSizeNm < 25)
                pointCount = 16;
            else
                pointCount = 20;

            double firstLatitude = 0;
            double firstLongitude = 0;

            for (int i = 0; i < pointCount; i++)
            {
                double angle =
                    (2.0 * Math.PI * i) / pointCount;

                double latitude =
                    centreLatitude +
                    Math.Cos(angle) * halfLatitude;

                double longitude =
                    centreLongitude +
                    Math.Sin(angle) * halfLongitude;

                if (i == 0)
                {
                    firstLatitude = latitude;
                    firstLongitude = longitude;
                }

                flightPlan.Waypoints.Add(
                    new RouteWaypoint
                    {
                        Name = string.Empty,
                        Ident = $"WP{waypointNumber}",
                        Type = "USER",
                        Latitude = latitude,
                        Longitude = longitude,
                        AltitudeFeet = altitude
                    });

                waypointNumber++;
            }

            // Close the orbit completely by returning to
            // the same position as the first orbit waypoint.
            flightPlan.Waypoints.Add(
                new RouteWaypoint
                {
                    Name = string.Empty,
                    Ident = $"WP{waypointNumber}",
                    Type = "USER",
                    Latitude = firstLatitude,
                    Longitude = firstLongitude,
                    AltitudeFeet = altitude
                });

            waypointNumber++;
        }

        private static void AddCircularOrbit(
            GeneratedFlightPlan flightPlan,
            double centreLatitude,
            double centreLongitude,
            double radiusNm,
            int altitude,
            ref int waypointNumber)
        {
            const int pointCount = 12;

            double latitudeRadiusDegrees =
                radiusNm / 60.0;

            double longitudeRadiusDegrees =
                radiusNm /
                (60.0 *
                 Math.Cos(
                     centreLatitude *
                     Math.PI /
                     180.0));

            double firstLatitude = 0;
            double firstLongitude = 0;

            for (int i = 0; i < pointCount; i++)
            {
                double angle =
                    (2.0 * Math.PI * i) / pointCount;

                double latitude =
                    centreLatitude +
                    Math.Cos(angle) *
                    latitudeRadiusDegrees;

                double longitude =
                    centreLongitude +
                    Math.Sin(angle) *
                    longitudeRadiusDegrees;

                if (i == 0)
                {
                    firstLatitude = latitude;
                    firstLongitude = longitude;
                }

                flightPlan.Waypoints.Add(
                    new RouteWaypoint
                    {
                        Name = string.Empty,
                        Ident = $"WP{waypointNumber}",
                        Type = "USER",
                        Latitude = latitude,
                        Longitude = longitude,
                        AltitudeFeet = altitude
                    });

                waypointNumber++;
            }

            // Close the circle.
            flightPlan.Waypoints.Add(
                new RouteWaypoint
                {
                    Name = string.Empty,
                    Ident = $"WP{waypointNumber}",
                    Type = "USER",
                    Latitude = firstLatitude,
                    Longitude = firstLongitude,
                    AltitudeFeet = altitude
                });

            waypointNumber++;
        }

        private static double EstimateSizeNm(
            double south,
            double north,
            double west,
            double east)
        {
            double centreLatitude =
                (north + south) / 2.0;

            double northSouthNm =
                Math.Abs(north - south) * 60.0;

            double eastWestNm =
                Math.Abs(east - west) *
                60.0 *
                Math.Cos(
                    centreLatitude *
                    Math.PI /
                    180.0);

            return Math.Max(
                northSouthNm,
                eastWestNm);
        }
    }
}

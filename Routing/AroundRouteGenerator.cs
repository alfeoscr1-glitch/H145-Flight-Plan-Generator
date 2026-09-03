using System;
using System.Threading;
using System.Threading.Tasks;
using H145FlightPlanner.Models;
using H145FlightPlanner.Services;

namespace H145FlightPlanner.Routing
{
    public class AroundRouteGenerator
    {
        private readonly AirportService _airportService;
        private readonly GeographyService _geographyService;

        public AroundRouteGenerator(
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
                    "No around location was found.");
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

            GeographyResult? area =
                await _geographyService.FindPlaceAsync(
                    request.OrbitLocation,
                    cancellationToken);

            if (area == null)
            {
                throw new InvalidOperationException(
                    $"Around location {request.OrbitLocation} could not be found.");
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

            if (area.HasBoundingBox)
            {
                AddMinimalAroundRoute(
                    flightPlan,
                    area,
                    altitude);
            }
            else
            {
                AddFallbackAroundRoute(
                    flightPlan,
                    area.Latitude,
                    area.Longitude,
                    altitude);
            }

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

        private static void AddMinimalAroundRoute(
            GeneratedFlightPlan flightPlan,
            GeographyResult area,
            int altitude)
        {
            double centreLatitude =
                (area.NorthLatitude + area.SouthLatitude) / 2.0;

            double centreLongitude =
                (area.EastLongitude + area.WestLongitude) / 2.0;

            double halfLatitude =
                (area.NorthLatitude - area.SouthLatitude) / 2.0;

            double halfLongitude =
                (area.EastLongitude - area.WestLongitude) / 2.0;

            // Slightly outside the actual area.
            halfLatitude *= 1.08;
            halfLongitude *= 1.08;

            double north =
                centreLatitude + halfLatitude;

            double south =
                centreLatitude - halfLatitude;

            double east =
                centreLongitude + halfLongitude;

            double west =
                centreLongitude - halfLongitude;

            // Minimum sensible loop:
            // North -> East -> South -> West -> back to North

            AddWaypoint(
                flightPlan,
                "WP1",
                north,
                centreLongitude,
                altitude);

            AddWaypoint(
                flightPlan,
                "WP2",
                centreLatitude,
                east,
                altitude);

            AddWaypoint(
                flightPlan,
                "WP3",
                south,
                centreLongitude,
                altitude);

            AddWaypoint(
                flightPlan,
                "WP4",
                centreLatitude,
                west,
                altitude);

            AddWaypoint(
                flightPlan,
                "WP5",
                north,
                centreLongitude,
                altitude);
        }

        private static void AddFallbackAroundRoute(
            GeneratedFlightPlan flightPlan,
            double centreLatitude,
            double centreLongitude,
            int altitude)
        {
            const double radiusNm = 2.0;

            double latitudeRadius =
                radiusNm / 60.0;

            double longitudeRadius =
                radiusNm /
                (60.0 *
                 Math.Cos(
                     centreLatitude *
                     Math.PI /
                     180.0));

            AddWaypoint(
                flightPlan,
                "WP1",
                centreLatitude + latitudeRadius,
                centreLongitude,
                altitude);

            AddWaypoint(
                flightPlan,
                "WP2",
                centreLatitude,
                centreLongitude + longitudeRadius,
                altitude);

            AddWaypoint(
                flightPlan,
                "WP3",
                centreLatitude - latitudeRadius,
                centreLongitude,
                altitude);

            AddWaypoint(
                flightPlan,
                "WP4",
                centreLatitude,
                centreLongitude - longitudeRadius,
                altitude);

            AddWaypoint(
                flightPlan,
                "WP5",
                centreLatitude + latitudeRadius,
                centreLongitude,
                altitude);
        }

        private static void AddWaypoint(
            GeneratedFlightPlan flightPlan,
            string ident,
            double latitude,
            double longitude,
            int altitude)
        {
            flightPlan.Waypoints.Add(
                new RouteWaypoint
                {
                    Name = string.Empty,
                    Ident = ident,
                    Type = "USER",
                    Latitude = latitude,
                    Longitude = longitude,
                    AltitudeFeet = altitude
                });
        }
    }
}

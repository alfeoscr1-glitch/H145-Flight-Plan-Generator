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

            if (string.IsNullOrWhiteSpace(request.AroundLocation))
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
                    request.AroundLocation,
                    cancellationToken);

            if (area == null)
            {
                throw new InvalidOperationException(
                    $"Around location {request.AroundLocation} could not be found.");
            }

            int altitude =
                request.AltitudeFeet ?? 1000;

            string flightRules =
                string.IsNullOrWhiteSpace(request.FlightRules)
                    ? "VFR"
                    : request.FlightRules.ToUpperInvariant();

            var flightPlan =
                new GeneratedFlightPlan
                {
                    FlightRules = flightRules,
                    CruisingAltitudeFeet = altitude
                };

            // -------------------------------------------------
            // DEPARTURE
            // -------------------------------------------------

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

            // -------------------------------------------------
            // AROUND ROUTE
            // -------------------------------------------------

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

            // -------------------------------------------------
            // FINAL AIRPORT
            // -------------------------------------------------

            string finalAirportIdent =
                !string.IsNullOrWhiteSpace(request.ReturnLocation)
                    ? request.ReturnLocation
                    : request.Destination;

            if (string.IsNullOrWhiteSpace(finalAirportIdent))
            {
                finalAirportIdent =
                    request.Departure;
            }

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

            // Keep the route just outside the area's
            // actual returned bounds.
            //
            // 3% margin keeps the route tight without
            // placing it directly on the edge.
            halfLatitude *= 1.03;
            halfLongitude *= 1.03;

            double north =
                centreLatitude + halfLatitude;

            double south =
                centreLatitude - halfLatitude;

            double east =
                centreLongitude + halfLongitude;

            double west =
                centreLongitude - halfLongitude;

            // -------------------------------------------------
            // MINIMUM PRACTICAL OUTSIDE LOOP
            //
            // Four unique corner waypoints.
            // WP5 repeats WP1 to close the loop.
            // -------------------------------------------------

            AddWaypoint(
                flightPlan,
                "WP1",
                north,
                west,
                altitude);

            AddWaypoint(
                flightPlan,
                "WP2",
                north,
                east,
                altitude);

            AddWaypoint(
                flightPlan,
                "WP3",
                south,
                east,
                altitude);

            AddWaypoint(
                flightPlan,
                "WP4",
                south,
                west,
                altitude);

            AddWaypoint(
                flightPlan,
                "WP5",
                north,
                west,
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

            double cosine =
                Math.Cos(
                    centreLatitude *
                    Math.PI /
                    180.0);

            double longitudeRadius;

            if (Math.Abs(cosine) < 0.000001)
            {
                longitudeRadius =
                    latitudeRadius;
            }
            else
            {
                longitudeRadius =
                    radiusNm /
                    (60.0 * cosine);
            }

            double north =
                centreLatitude + latitudeRadius;

            double south =
                centreLatitude - latitudeRadius;

            double east =
                centreLongitude + longitudeRadius;

            double west =
                centreLongitude - longitudeRadius;

            AddWaypoint(
                flightPlan,
                "WP1",
                north,
                west,
                altitude);

            AddWaypoint(
                flightPlan,
                "WP2",
                north,
                east,
                altitude);

            AddWaypoint(
                flightPlan,
                "WP3",
                south,
                east,
                altitude);

            AddWaypoint(
                flightPlan,
                "WP4",
                south,
                west,
                altitude);

            AddWaypoint(
                flightPlan,
                "WP5",
                north,
                west,
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

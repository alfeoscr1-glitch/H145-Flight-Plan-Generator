using System;
using System.Threading;
using System.Threading.Tasks;
using H145FlightPlanner.Models;
using H145FlightPlanner.Services;

namespace H145FlightPlanner.Routing
{
    public class DirectRouteGenerator
    {
        private readonly AirportService _airportService;

        public DirectRouteGenerator(
            AirportService airportService)
        {
            _airportService = airportService;
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
                    "No departure ICAO was found.");
            }

            if (string.IsNullOrWhiteSpace(request.Destination))
            {
                throw new InvalidOperationException(
                    "No destination ICAO was found.");
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

            AirportResult? destination =
                await _airportService.FindByIcaoAsync(
                    request.Destination,
                    cancellationToken);

            if (destination == null)
            {
                throw new InvalidOperationException(
                    $"Destination airport {request.Destination} could not be found.");
            }

            int cruisingAltitude =
                request.AltitudeFeet ?? 1000;

            string flightRules =
                string.IsNullOrWhiteSpace(request.FlightRules)
                    ? "VFR"
                    : request.FlightRules.ToUpperInvariant();

            var flightPlan = new GeneratedFlightPlan
            {
                FlightRules = flightRules,
                CruisingAltitudeFeet = cruisingAltitude
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

            flightPlan.Waypoints.Add(
                new RouteWaypoint
                {
                    Name = destination.Name,
                    Ident = destination.Ident,
                    Type = "AIRPORT",
                    Latitude = destination.Latitude,
                    Longitude = destination.Longitude,
                    AltitudeFeet = destination.ElevationFeet
                });

            return flightPlan;
        }
    }
}

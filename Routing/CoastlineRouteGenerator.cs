using System;
using System.Threading;
using System.Threading.Tasks;
using H145FlightPlanner.Models;
using H145FlightPlanner.Services;

namespace H145FlightPlanner.Routing
{
    public class CoastlineRouteGenerator
    {
        private readonly AirportService _airportService;
        private readonly GeographyService _geographyService;
        private readonly CoastlineGeometryService _coastlineGeometryService;

        public CoastlineRouteGenerator(
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
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (string.IsNullOrWhiteSpace(request.Departure))
                throw new InvalidOperationException("No departure airport was found.");

            AirportResult? departure =
                await _airportService.FindByIcaoAsync(
                    request.Departure,
                    cancellationToken);

            if (departure == null)
            {
                throw new InvalidOperationException(
                    $"Departure airport {request.Departure} could not be found.");
            }

            int altitude = request.AltitudeFeet ?? 1000;

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

            AddAirportWaypoint(flightPlan, departure);

            if (request.CoastlineMode.Equals(
                "AROUND",
                StringComparison.OrdinalIgnoreCase))
            {
                await GenerateAroundAsync(
                    request,
                    flightPlan,
                    altitude,
                    cancellationToken);
            }
            else
            {
                await GenerateAlongAsync(
                    request,
                    flightPlan,
                    departure,
                    altitude,
                    cancellationToken);
            }

            return flightPlan;
        }

        private async Task GenerateAroundAsync(
            FlightPlanRequest request,
            GeneratedFlightPlan flightPlan,
            int altitude,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(request.CoastlineLocation))
            {
                throw new InvalidOperationException(
                    "No coastline location was found.");
            }

            GeographyResult? area =
                await _geographyService.FindPlaceAsync(
                    request.CoastlineLocation,
                    cancellationToken);

            if (area == null)
            {
                throw new InvalidOperationException(
                    $"Coastline location {request.CoastlineLocation} could not be found.");
            }

            CoastlineGeometry coastline =
                await _coastlineGeometryService.GetAroundCoastlineAsync(
                    area,
                    cancellationToken);

            AddCoastlineWaypoints(
                flightPlan,
                coastline,
                altitude);

            string finalIdent =
                !string.IsNullOrWhiteSpace(request.ReturnLocation)
                    ? request.ReturnLocation
                    : request.Departure;

            AirportResult? finalAirport =
                await _airportService.FindByIcaoAsync(
                    finalIdent,
                    cancellationToken);

            if (finalAirport == null)
            {
                throw new InvalidOperationException(
                    $"Final airport {finalIdent} could not be found.");
            }

            AddAirportWaypoint(
                flightPlan,
                finalAirport);
        }

        private async Task GenerateAlongAsync(
            FlightPlanRequest request,
            GeneratedFlightPlan flightPlan,
            AirportResult departure,
            int altitude,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(request.Destination))
            {
                throw new InvalidOperationException(
                    "A coastline-along route needs a destination airport.");
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

            CoastlineGeometry coastline =
                await _coastlineGeometryService.GetAlongCoastlineAsync(
                    departure,
                    destination,
                    cancellationToken);

            AddCoastlineWaypoints(
                flightPlan,
                coastline,
                altitude);

            AddAirportWaypoint(
                flightPlan,
                destination);
        }

        private static void AddCoastlineWaypoints(
            GeneratedFlightPlan flightPlan,
            CoastlineGeometry coastline,
            int altitude)
        {
            int waypointNumber = 1;

            foreach (CoastlinePoint point in coastline.Points)
            {
                flightPlan.Waypoints.Add(
                    new RouteWaypoint
                    {
                        Name = "Coastline",
                        Ident = $"CST{waypointNumber:000}",
                        Type = "USER",
                        Latitude = point.Latitude,
                        Longitude = point.Longitude,
                        AltitudeFeet = altitude
                    });

                waypointNumber++;
            }
        }

        private static void AddAirportWaypoint(
            GeneratedFlightPlan flightPlan,
            AirportResult airport)
        {
            flightPlan.Waypoints.Add(
                new RouteWaypoint
                {
                    Name = airport.Name,
                    Ident = airport.Ident,
                    Type = "AIRPORT",
                    Latitude = airport.Latitude,
                    Longitude = airport.Longitude,
                    AltitudeFeet = airport.ElevationFeet
                });
        }
    }
}

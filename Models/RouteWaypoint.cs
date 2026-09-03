namespace H145FlightPlanner.Models
{
    public class RouteWaypoint
    {
        public string Name { get; set; } = string.Empty;

        public string Ident { get; set; } = string.Empty;

        public string Type { get; set; } = "USER";

        public double Latitude { get; set; }

        public double Longitude { get; set; }

        public double AltitudeFeet { get; set; }
    }
}

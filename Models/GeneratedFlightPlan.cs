using System.Collections.Generic;

namespace H145FlightPlanner.Models
{
    public class GeneratedFlightPlan
    {
        public string FlightRules { get; set; } = "VFR";

        public int CruisingAltitudeFeet { get; set; } = 1000;

        public List<RouteWaypoint> Waypoints { get; set; } = new();
    }
}

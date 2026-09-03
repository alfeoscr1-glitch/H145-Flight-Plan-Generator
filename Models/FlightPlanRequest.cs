using System.Collections.Generic;

namespace H145FlightPlanner.Models
{
    public class FlightPlanRequest
    {
        public string Departure { get; set; } = string.Empty;
        public string Destination { get; set; } = string.Empty;
        public string RouteType { get; set; } = string.Empty;
        public string OrbitLocation { get; set; } = string.Empty;
        public string CoastlineMode { get; set; } = string.Empty;
        public string CoastlineLocation { get; set; } = string.Empty;
        public string ReturnLocation { get; set; } = string.Empty;
        public int? AltitudeFeet { get; set; }
        public string FlightRules { get; set; } = string.Empty;
        public List<string> RequestedLocations { get; set; } = new();

        // Filled by the hidden local route-understanding model for complex,
        // multi-stage instructions. Existing Direct / Orbit / Coastline code
        // can continue using the legacy properties above.
        public SmartRoutePlan? SmartPlan { get; set; }
    }
}

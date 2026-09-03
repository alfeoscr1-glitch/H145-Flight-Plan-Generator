using System.Collections.Generic;

namespace H145FlightPlanner.Models
{
    public class SmartRoutePlan
    {
        public string Start { get; set; } = string.Empty;
        public string End { get; set; } = string.Empty;
        public int? AltitudeFeet { get; set; }
        public string FlightRules { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;

        // The local AI can return as many ordered steps as the instruction needs.
        // There is intentionally no fixed maximum number of legs.
        public List<SmartRouteStep> Steps { get; set; } = new();
    }

    public class SmartRouteStep
    {
        // Supported actions:
        // DIRECT, COASTLINE_ALONG, COASTLINE_AROUND, ORBIT, RETURN, END
        public string Action { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
        public string From { get; set; } = string.Empty;
        public string To { get; set; } = string.Empty;
        public string Direction { get; set; } = string.Empty;
        public bool KeepCloseToEdge { get; set; } = true;
        public bool CompleteLoop { get; set; }
        public bool AvoidLand { get; set; } = true;
        public string Notes { get; set; } = string.Empty;
    }
}

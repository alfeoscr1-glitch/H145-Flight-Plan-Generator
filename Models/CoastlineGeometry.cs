using System.Collections.Generic;

namespace H145FlightPlanner.Models
{
    public class CoastlinePoint
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
    }

    public class CoastlineGeometry
    {
        public List<CoastlinePoint> Points { get; set; } = new();
        public bool IsClosed { get; set; }
        public string SourceDescription { get; set; } = string.Empty;
    }
}

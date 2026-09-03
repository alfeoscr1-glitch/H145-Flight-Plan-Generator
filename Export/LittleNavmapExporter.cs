using System;
using System.Globalization;
using System.Text;
using System.Xml;
using H145FlightPlanner.Models;

namespace H145FlightPlanner.Export
{
    public static class LittleNavmapExporter
    {
        public static void Export(
            GeneratedFlightPlan flightPlan,
            string filePath)
        {
            if (flightPlan == null)
                throw new ArgumentNullException(nameof(flightPlan));

            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException(
                    "A file path is required.",
                    nameof(filePath));

            if (flightPlan.Waypoints.Count < 2)
            {
                throw new InvalidOperationException(
                    "A flight plan must contain at least two waypoints.");
            }

            var settings = new XmlWriterSettings
            {
                Indent = true,
                Encoding = new UTF8Encoding(false),
                NewLineChars = Environment.NewLine
            };

            using XmlWriter writer =
                XmlWriter.Create(filePath, settings);

            writer.WriteStartDocument();

            writer.WriteStartElement("LittleNavmap");

            writer.WriteAttributeString(
                "xmlns",
                "xsi",
                null,
                "http://www.w3.org/2001/XMLSchema-instance");

            writer.WriteAttributeString(
                "xsi",
                "noNamespaceSchemaLocation",
                "http://www.w3.org/2001/XMLSchema-instance",
                "https://www.littlenavmap.org/schema/lnmpln.xsd");

            writer.WriteStartElement("Flightplan");

            // -------------------------------------------------
            // HEADER
            // -------------------------------------------------

            writer.WriteStartElement("Header");

            writer.WriteElementString(
                "FlightplanType",
                string.IsNullOrWhiteSpace(flightPlan.FlightRules)
                    ? "VFR"
                    : flightPlan.FlightRules.ToUpperInvariant());

            writer.WriteElementString(
                "CruisingAlt",
                flightPlan.CruisingAltitudeFeet.ToString(
                    CultureInfo.InvariantCulture));

            writer.WriteElementString(
                "CruisingAltF",
                flightPlan.CruisingAltitudeFeet.ToString(
                    "F8",
                    CultureInfo.InvariantCulture));

            writer.WriteElementString(
                "CreationDate",
                DateTimeOffset.Now.ToString(
                    "yyyy-MM-ddTHH:mm:sszzz",
                    CultureInfo.InvariantCulture));

            writer.WriteElementString(
                "FileVersion",
                "1.2");

            writer.WriteElementString(
                "ProgramName",
                "H145 Flight Plan Generator");

            writer.WriteElementString(
                "ProgramVersion",
                "1.0");

            writer.WriteElementString(
                "Documentation",
                "https://www.littlenavmap.org/lnmpln.html");

            writer.WriteEndElement();

            // -------------------------------------------------
            // NAVIGATION DATA
            // -------------------------------------------------

            writer.WriteStartElement("SimData");
            writer.WriteAttributeString("Cycle", "1801");
            writer.WriteString("NAVIGRAPH");
            writer.WriteEndElement();

            writer.WriteStartElement("NavData");
            writer.WriteAttributeString("Cycle", "1801");
            writer.WriteString("NAVIGRAPH");
            writer.WriteEndElement();

            // -------------------------------------------------
            // H145 AIRCRAFT PERFORMANCE
            // -------------------------------------------------

            writer.WriteStartElement("AircraftPerformance");

            writer.WriteElementString(
                "FilePath",
                "Airbus H145.lnmperf");

            writer.WriteElementString(
                "Type",
                "BK17");

            writer.WriteElementString(
                "Name",
                "H145 (MB-BK117 D-2)");

            writer.WriteEndElement();

            // -------------------------------------------------
            // WAYPOINTS
            // -------------------------------------------------

            writer.WriteStartElement("Waypoints");

            foreach (RouteWaypoint waypoint in flightPlan.Waypoints)
            {
                writer.WriteStartElement("Waypoint");

                if (!string.IsNullOrWhiteSpace(waypoint.Name))
                {
                    writer.WriteElementString(
                        "Name",
                        waypoint.Name);
                }

                writer.WriteElementString(
                    "Ident",
                    waypoint.Ident);

                writer.WriteElementString(
                    "Type",
                    waypoint.Type);

                writer.WriteStartElement("Pos");

                writer.WriteAttributeString(
                    "Lon",
                    waypoint.Longitude.ToString(
                        "F6",
                        CultureInfo.InvariantCulture));

                writer.WriteAttributeString(
                    "Lat",
                    waypoint.Latitude.ToString(
                        "F6",
                        CultureInfo.InvariantCulture));

                writer.WriteAttributeString(
                    "Alt",
                    waypoint.AltitudeFeet.ToString(
                        "F2",
                        CultureInfo.InvariantCulture));

                writer.WriteEndElement();

                writer.WriteEndElement();
            }

            writer.WriteEndElement();

            writer.WriteEndElement();
            writer.WriteEndElement();

            writer.WriteEndDocument();
        }
    }
}

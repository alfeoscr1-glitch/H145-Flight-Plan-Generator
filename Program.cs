using System;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using H145FlightPlanner.Export;
using H145FlightPlanner.Logic;
using H145FlightPlanner.Models;
using H145FlightPlanner.Routing;
using H145FlightPlanner.Services;
using H145FlightPlanner.Speech;

namespace H145FlightPlanner
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
        }
    }

    public class MainForm : Form
    {
        private readonly WhisperSpeechService _whisperService;

        private readonly AirportService _airportService;
        private readonly GeographyService _geographyService;

        private readonly DirectRouteGenerator _directRouteGenerator;
        private readonly OrbitRouteGenerator _orbitRouteGenerator;

        private readonly TextBox _commandBox;
        private readonly Button _microphoneButton;
        private readonly Label _microphoneStatus;

        private readonly Label _departureValue;
        private readonly Label _destinationValue;
        private readonly Label _routeTypeValue;

        private readonly TextBox _flightPlanBox;
        private readonly Button _exportButton;

        private bool _isListening;

        private GeneratedFlightPlan? _currentFlightPlan;

        public MainForm()
        {
            _whisperService =
                new WhisperSpeechService();

            _airportService =
                new AirportService();

            _geographyService =
                new GeographyService();

            _directRouteGenerator =
                new DirectRouteGenerator(
                    _airportService);

            _orbitRouteGenerator =
                new OrbitRouteGenerator(
                    _airportService,
                    _geographyService);

            _whisperService.TranscriptionReceived +=
                OnTranscriptionReceived;

            _whisperService.StatusChanged +=
                OnWhisperStatusChanged;

            _whisperService.SpeechError +=
                OnWhisperError;

            Text =
                "H145 Flight Plan Generator";

            StartPosition =
                FormStartPosition.CenterScreen;

            ClientSize =
                new Size(1000, 700);

            MinimumSize =
                new Size(900, 600);

            BackColor =
                Color.FromArgb(
                    240,
                    240,
                    240);

            Font =
                new Font(
                    "Segoe UI",
                    10);

            Panel header =
                new Panel
                {
                    Dock =
                        DockStyle.Top,

                    Height =
                        105,

                    BackColor =
                        Color.FromArgb(
                            32,
                            32,
                            32)
                };

            Label title =
                new Label
                {
                    Text =
                        "H145 FLIGHT PLAN GENERATOR",

                    ForeColor =
                        Color.White,

                    Font =
                        new Font(
                            "Segoe UI",
                            22,
                            FontStyle.Bold),

                    AutoSize =
                        true,

                    Location =
                        new Point(
                            30,
                            22)
                };

            Label subtitle =
                new Label
                {
                    Text =
                        "Create helicopter flight plans for Little Navmap",

                    ForeColor =
                        Color.LightGray,

                    Font =
                        new Font(
                            "Segoe UI",
                            11),

                    AutoSize =
                        true,

                    Location =
                        new Point(
                            32,
                            63)
                };

            header.Controls.Add(
                title);

            header.Controls.Add(
                subtitle);

            GroupBox commandGroup =
                new GroupBox
                {
                    Text =
                        "FLIGHT PLAN",

                    Location =
                        new Point(
                            25,
                            125),

                    Size =
                        new Size(
                            950,
                            205),

                    Font =
                        new Font(
                            "Segoe UI",
                            10,
                            FontStyle.Bold)
                };

            Label instruction =
                new Label
                {
                    Text =
                        "Tell the program what you would like to do:",

                    AutoSize =
                        true,

                    Location =
                        new Point(
                            20,
                            35),

                    Font =
                        new Font(
                            "Segoe UI",
                            10,
                            FontStyle.Regular)
                };

            _commandBox =
                new TextBox
                {
                    Multiline =
                        true,

                    ScrollBars =
                        ScrollBars.Vertical,

                    Location =
                        new Point(
                            20,
                            65),

                    Size =
                        new Size(
                            910,
                            70),

                    Font =
                        new Font(
                            "Segoe UI",
                            11),

                    Anchor =
                        AnchorStyles.Top |
                        AnchorStyles.Left |
                        AnchorStyles.Right,

                    PlaceholderText =
                        "Type your flight plan here, or use the microphone..."
                };

            Button generateButton =
                new Button
                {
                    Text =
                        "GENERATE FLIGHT PLAN",

                    Location =
                        new Point(
                            20,
                            150),

                    Size =
                        new Size(
                            220,
                            38),

                    Font =
                        new Font(
                            "Segoe UI",
                            10,
                            FontStyle.Bold)
                };

            generateButton.Click +=
                GenerateButton_Click;

            _microphoneButton =
                new Button
                {
                    Text =
                        "🎤 START SPEAKING",

                    Location =
                        new Point(
                            255,
                            150),

                    Size =
                        new Size(
                            190,
                            38),

                    Font =
                        new Font(
                            "Segoe UI",
                            10,
                            FontStyle.Bold)
                };

            _microphoneButton.Click +=
                MicrophoneButton_Click;

            _microphoneStatus =
                new Label
                {
                    Text =
                        "Loading Whisper...",

                    AutoSize =
                        true,

                    ForeColor =
                        Color.DarkOrange,

                    Location =
                        new Point(
                            460,
                            161),

                    Font =
                        new Font(
                            "Segoe UI",
                            9,
                            FontStyle.Regular)
                };

            commandGroup.Controls.Add(
                instruction);

            commandGroup.Controls.Add(
                _commandBox);

            commandGroup.Controls.Add(
                generateButton);

            commandGroup.Controls.Add(
                _microphoneButton);

            commandGroup.Controls.Add(
                _microphoneStatus);

            GroupBox routeGroup =
                new GroupBox
                {
                    Text =
                        "ROUTE INFORMATION",

                    Location =
                        new Point(
                            25,
                            345),

                    Size =
                        new Size(
                            950,
                            120),

                    Font =
                        new Font(
                            "Segoe UI",
                            10,
                            FontStyle.Bold)
                };

            Label departureLabel =
                new Label
                {
                    Text =
                        "Departure:",

                    AutoSize =
                        true,

                    Location =
                        new Point(
                            20,
                            35),

                    Font =
                        new Font(
                            "Segoe UI",
                            10,
                            FontStyle.Regular)
                };

            _departureValue =
                new Label
                {
                    Text =
                        "—",

                    AutoSize =
                        true,

                    Location =
                        new Point(
                            120,
                            35),

                    Font =
                        new Font(
                            "Segoe UI",
                            10,
                            FontStyle.Bold)
                };

            Label destinationLabel =
                new Label
                {
                    Text =
                        "Destination:",

                    AutoSize =
                        true,

                    Location =
                        new Point(
                            330,
                            35),

                    Font =
                        new Font(
                            "Segoe UI",
                            10,
                            FontStyle.Regular)
                };

            _destinationValue =
                new Label
                {
                    Text =
                        "—",

                    AutoSize =
                        true,

                    Location =
                        new Point(
                            440,
                            35),

                    Font =
                        new Font(
                            "Segoe UI",
                            10,
                            FontStyle.Bold)
                };

            Label routeTypeLabel =
                new Label
                {
                    Text =
                        "Route type:",

                    AutoSize =
                        true,

                    Location =
                        new Point(
                            680,
                            35),

                    Font =
                        new Font(
                            "Segoe UI",
                            10,
                            FontStyle.Regular)
                };

            _routeTypeValue =
                new Label
                {
                    Text =
                        "—",

                    AutoSize =
                        true,

                    Location =
                        new Point(
                            775,
                            35),

                    Font =
                        new Font(
                            "Segoe UI",
                            10,
                            FontStyle.Bold)
                };

            routeGroup.Controls.Add(
                departureLabel);

            routeGroup.Controls.Add(
                _departureValue);

            routeGroup.Controls.Add(
                destinationLabel);

            routeGroup.Controls.Add(
                _destinationValue);

            routeGroup.Controls.Add(
                routeTypeLabel);

            routeGroup.Controls.Add(
                _routeTypeValue);

            GroupBox outputGroup =
                new GroupBox
                {
                    Text =
                        "FLIGHT PLAN",

                    Location =
                        new Point(
                            25,
                            480),

                    Size =
                        new Size(
                            950,
                            190),

                    Font =
                        new Font(
                            "Segoe UI",
                            10,
                            FontStyle.Bold),

                    Anchor =
                        AnchorStyles.Top |
                        AnchorStyles.Bottom |
                        AnchorStyles.Left |
                        AnchorStyles.Right
                };

            _flightPlanBox =
                new TextBox
                {
                    Multiline =
                        true,

                    ReadOnly =
                        true,

                    ScrollBars =
                        ScrollBars.Vertical,

                    Location =
                        new Point(
                            20,
                            35),

                    Size =
                        new Size(
                            910,
                            90),

                    Font =
                        new Font(
                            "Consolas",
                            10),

                    Text =
                        "No flight plan generated yet.",

                    Anchor =
                        AnchorStyles.Top |
                        AnchorStyles.Bottom |
                        AnchorStyles.Left |
                        AnchorStyles.Right
                };

            _exportButton =
                new Button
                {
                    Text =
                        "EXPORT TO LITTLE NAVMAP",

                    Location =
                        new Point(
                            20,
                            135),

                    Size =
                        new Size(
                            220,
                            38),

                    Enabled =
                        false,

                    Font =
                        new Font(
                            "Segoe UI",
                            10,
                            FontStyle.Bold),

                    Anchor =
                        AnchorStyles.Bottom |
                        AnchorStyles.Left
                };

            _exportButton.Click +=
                ExportButton_Click;

            outputGroup.Controls.Add(
                _flightPlanBox);

            outputGroup.Controls.Add(
                _exportButton);

            Controls.Add(
                header);

            Controls.Add(
                commandGroup);

            Controls.Add(
                routeGroup);

            Controls.Add(
                outputGroup);

            Load +=
                MainForm_Load;

            FormClosing +=
                MainForm_FormClosing;
        }

        private async void MainForm_Load(
            object? sender,
            EventArgs e)
        {
            _microphoneButton.Enabled =
                false;

            await _whisperService.InitializeAsync();

            _microphoneButton.Enabled =
                true;
        }

        private void MicrophoneButton_Click(
            object? sender,
            EventArgs e)
        {
            if (_isListening)
            {
                _whisperService.StopListening();

                _isListening =
                    false;

                _microphoneButton.Text =
                    "🎤 START SPEAKING";
            }
            else
            {
                _whisperService.StartListening();

                _isListening =
                    true;

                _microphoneButton.Text =
                    "🛑 STOP SPEAKING";
            }
        }

        private async void GenerateButton_Click(
            object? sender,
            EventArgs e)
        {
            try
            {
                string input =
                    _commandBox.Text.Trim();

                if (string.IsNullOrWhiteSpace(input))
                {
                    MessageBox.Show(
                        "Please type or speak a flight-plan request first.",
                        "No Flight Plan Request",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);

                    return;
                }

                _currentFlightPlan =
                    null;

                _exportButton.Enabled =
                    false;

                FlightPlanRequest request =
                    FlightPlanCommandParser.Parse(
                        input);

                _departureValue.Text =
                    string.IsNullOrWhiteSpace(
                        request.Departure)
                        ? "—"
                        : request.Departure;

                string displayedDestination =
                    !string.IsNullOrWhiteSpace(
                        request.ReturnLocation)
                        ? request.ReturnLocation
                        : request.Destination;

                _destinationValue.Text =
                    string.IsNullOrWhiteSpace(
                        displayedDestination)
                        ? "—"
                        : displayedDestination;

                _routeTypeValue.Text =
                    string.IsNullOrWhiteSpace(
                        request.RouteType)
                        ? "—"
                        : request.RouteType;

                _flightPlanBox.Text =
                    "Looking up locations and generating route...";

                // ---------------------------------------------
                // DIRECT
                // ---------------------------------------------

                if (string.Equals(
                    request.RouteType,
                    "DIRECT",
                    StringComparison.OrdinalIgnoreCase))
                {
                    _currentFlightPlan =
                        await _directRouteGenerator.GenerateAsync(
                            request);
                }

                // ---------------------------------------------
                // ORBIT
                // ---------------------------------------------

                else if (string.Equals(
                    request.RouteType,
                    "ORBIT",
                    StringComparison.OrdinalIgnoreCase))
                {
                    _currentFlightPlan =
                        await _orbitRouteGenerator.GenerateAsync(
                            request);
                }

                // ---------------------------------------------
                // NOT IMPLEMENTED YET
                // ---------------------------------------------

                else
                {
                    _flightPlanBox.Text =
                        "This route type has not been implemented yet.";

                    return;
                }

                DisplayGeneratedFlightPlan(
                    _currentFlightPlan);

                _exportButton.Enabled =
                    true;
            }
            catch (Exception ex)
            {
                _currentFlightPlan =
                    null;

                _exportButton.Enabled =
                    false;

                _flightPlanBox.Text =
                    $"Flight plan could not be generated.\r\n\r\n" +
                    ex.Message;

                MessageBox.Show(
                    ex.Message,
                    "Flight Plan Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void DisplayGeneratedFlightPlan(
            GeneratedFlightPlan flightPlan)
        {
            var output =
                new StringBuilder();

            output.AppendLine(
                $"Flight rules: {flightPlan.FlightRules}");

            output.AppendLine(
                $"Cruising altitude: " +
                $"{flightPlan.CruisingAltitudeFeet} ft");

            output.AppendLine();

            output.AppendLine(
                "Route:");

            for (int i = 0;
                 i < flightPlan.Waypoints.Count;
                 i++)
            {
                RouteWaypoint waypoint =
                    flightPlan.Waypoints[i];

                output.AppendLine(
                    $"{i + 1}. " +
                    $"{waypoint.Ident}" +
                    (string.IsNullOrWhiteSpace(
                        waypoint.Name)
                        ? string.Empty
                        : $" - {waypoint.Name}"));

                output.AppendLine(
                    $"   Lat: {waypoint.Latitude:F6}");

                output.AppendLine(
                    $"   Lon: {waypoint.Longitude:F6}");

                output.AppendLine(
                    $"   Alt: {waypoint.AltitudeFeet:F0} ft");
            }

            _flightPlanBox.Text =
                output.ToString();
        }

        private void ExportButton_Click(
            object? sender,
            EventArgs e)
        {
            try
            {
                if (_currentFlightPlan == null ||
                    _currentFlightPlan.Waypoints.Count < 2)
                {
                    MessageBox.Show(
                        "Generate a flight plan before exporting.",
                        "No Flight Plan",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);

                    return;
                }

                RouteWaypoint departure =
                    _currentFlightPlan.Waypoints[0];

                RouteWaypoint destination =
                    _currentFlightPlan.Waypoints[
                        _currentFlightPlan.Waypoints.Count - 1];

                string defaultFileName =
                    $"{departure.Ident} to " +
                    $"{destination.Ident}.lnmpln";

                using var saveDialog =
                    new SaveFileDialog
                    {
                        Title =
                            "Export Little Navmap Flight Plan",

                        Filter =
                            "Little Navmap Flight Plan (*.lnmpln)|*.lnmpln",

                        DefaultExt =
                            "lnmpln",

                        AddExtension =
                            true,

                        FileName =
                            defaultFileName,

                        OverwritePrompt =
                            true
                    };

                if (saveDialog.ShowDialog(this) !=
                    DialogResult.OK)
                {
                    return;
                }

                LittleNavmapExporter.Export(
                    _currentFlightPlan,
                    saveDialog.FileName);

                MessageBox.Show(
                    $"Flight plan exported successfully.\r\n\r\n" +
                    $"{saveDialog.FileName}",
                    "Export Complete",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"The flight plan could not be exported.\r\n\r\n" +
                    ex.Message,
                    "Export Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void OnWhisperStatusChanged(
            object? sender,
            string status)
        {
            if (InvokeRequired)
            {
                Invoke(
                    new Action(
                        () =>
                            OnWhisperStatusChanged(
                                sender,
                                status)));

                return;
            }

            _microphoneStatus.Text =
                status;

            if (status.Contains(
                "Listening",
                StringComparison.OrdinalIgnoreCase))
            {
                _microphoneStatus.ForeColor =
                    Color.DarkRed;
            }
            else if (status.Contains(
                "Processing",
                StringComparison.OrdinalIgnoreCase))
            {
                _microphoneStatus.ForeColor =
                    Color.DarkOrange;
            }
            else if (status.Contains(
                "Ready",
                StringComparison.OrdinalIgnoreCase))
            {
                _microphoneStatus.ForeColor =
                    Color.DarkGreen;
            }
            else
            {
                _microphoneStatus.ForeColor =
                    Color.DimGray;
            }
        }

        private void OnTranscriptionReceived(
            object? sender,
            string text)
        {
            if (InvokeRequired)
            {
                Invoke(
                    new Action(
                        () =>
                            OnTranscriptionReceived(
                                sender,
                                text)));

                return;
            }

            if (string.IsNullOrWhiteSpace(text))
                return;

            if (!string.IsNullOrWhiteSpace(
                _commandBox.Text))
            {
                _commandBox.AppendText(
                    " ");
            }

            _commandBox.AppendText(
                text.Trim());

            _commandBox.SelectionStart =
                _commandBox.Text.Length;

            _commandBox.ScrollToCaret();
        }

        private void OnWhisperError(
            object? sender,
            string message)
        {
            if (InvokeRequired)
            {
                Invoke(
                    new Action(
                        () =>
                            OnWhisperError(
                                sender,
                                message)));

                return;
            }

            _microphoneButton.Enabled =
                true;

            _microphoneStatus.Text =
                "Whisper error";

            _microphoneStatus.ForeColor =
                Color.DarkRed;

            MessageBox.Show(
                message,
                "Whisper Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        private void MainForm_FormClosing(
            object? sender,
            FormClosingEventArgs e)
        {
            _whisperService.Dispose();
        }
    }
}

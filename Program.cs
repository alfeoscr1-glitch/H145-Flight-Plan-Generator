using System;
using System.Drawing;
using System.Windows.Forms;

namespace H145FlightPlanner;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        Form window = new Form
        {
            Text = "H145 Flight Plan Generator",
            Width = 1000,
            Height = 700,
            StartPosition = FormStartPosition.CenterScreen,
            MinimumSize = new Size(900, 600),
            BackColor = Color.FromArgb(245, 246, 248)
        };

        // -------------------------------------------------
        // HEADER
        // -------------------------------------------------

        Panel header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 80,
            BackColor = Color.FromArgb(30, 35, 42)
        };

        Label title = new Label
        {
            Text = "H145 FLIGHT PLAN GENERATOR",
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 20, FontStyle.Bold),
            AutoSize = true,
            Left = 30,
            Top = 20
        };

        Label subtitle = new Label
        {
            Text = "Create helicopter flight plans for Little Navmap",
            ForeColor = Color.LightGray,
            Font = new Font("Segoe UI", 10),
            AutoSize = true,
            Left = 33,
            Top = 50
        };

        header.Controls.Add(title);
        header.Controls.Add(subtitle);

        // -------------------------------------------------
        // MAIN CONTENT
        // -------------------------------------------------

        Panel main = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(30)
        };

        // Flight plan instruction
        Label instruction = new Label
        {
            Text = "What would you like to do?",
            Font = new Font("Segoe UI", 14, FontStyle.Bold),
            AutoSize = true,
            Left = 30,
            Top = 25
        };

        // Command input
        TextBox commandBox = new TextBox
        {
            Left = 30,
            Top = 65,
            Width = 880,
            Height = 90,
            Multiline = true,
            Font = new Font("Segoe UI", 12),
            PlaceholderText = "Example: Create a flight plan from EGCK to EGFA..."
        };

        // Generate button
        Button generateButton = new Button
        {
            Text = "GENERATE FLIGHT PLAN",
            Left = 30,
            Top = 175,
            Width = 230,
            Height = 45,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };

        // -------------------------------------------------
        // ROUTE INFORMATION
        // -------------------------------------------------

        GroupBox routeGroup = new GroupBox
        {
            Text = "Route Information",
            Left = 30,
            Top = 245,
            Width = 880,
            Height = 130,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };

        Label departureLabel = new Label
        {
            Text = "Departure:",
            Left = 20,
            Top = 30,
            Width = 100
        };

        Label departureValue = new Label
        {
            Text = "—",
            Left = 130,
            Top = 30,
            Width = 250
        };

        Label destinationLabel = new Label
        {
            Text = "Destination:",
            Left = 20,
            Top = 65,
            Width = 100
        };

        Label destinationValue = new Label
        {
            Text = "—",
            Left = 130,
            Top = 65,
            Width = 250
        };

        Label routeTypeLabel = new Label
        {
            Text = "Route type:",
            Left = 450,
            Top = 30,
            Width = 100
        };

        Label routeTypeValue = new Label
        {
            Text = "—",
            Left = 560,
            Top = 30,
            Width = 250
        };

        routeGroup.Controls.Add(departureLabel);
        routeGroup.Controls.Add(departureValue);
        routeGroup.Controls.Add(destinationLabel);
        routeGroup.Controls.Add(destinationValue);
        routeGroup.Controls.Add(routeTypeLabel);
        routeGroup.Controls.Add(routeTypeValue);

        // -------------------------------------------------
        // FLIGHT PLAN OUTPUT
        // -------------------------------------------------

        GroupBox outputGroup = new GroupBox
        {
            Text = "Flight Plan",
            Left = 30,
            Top = 395,
            Width = 880,
            Height = 150,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };

        TextBox outputBox = new TextBox
        {
            Left = 20,
            Top = 30,
            Width = 840,
            Height = 70,
            Multiline = true,
            ReadOnly = true,
            Font = new Font("Consolas", 10),
            Text = "No flight plan generated yet."
        };

        Button exportButton = new Button
        {
            Text = "EXPORT TO LITTLE NAVMAP",
            Left = 20,
            Top = 105,
            Width = 220,
            Height = 30,
            Enabled = false
        };

        outputGroup.Controls.Add(outputBox);
        outputGroup.Controls.Add(exportButton);

        // -------------------------------------------------
        // TEMPORARY BUTTON BEHAVIOUR
        // -------------------------------------------------

        generateButton.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(commandBox.Text))
            {
                MessageBox.Show(
                    "Please enter a flight plan request first.",
                    "H145 Flight Plan Generator",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);

                return;
            }

            outputBox.Text =
                "UI TEST — Route generation will be added next.\r\n\r\n" +
                "Your request:\r\n" +
                commandBox.Text;

            exportButton.Enabled = true;
        };

        // -------------------------------------------------
        // ADD EVERYTHING TO THE WINDOW
        // -------------------------------------------------

        main.Controls.Add(instruction);
        main.Controls.Add(commandBox);
        main.Controls.Add(generateButton);
        main.Controls.Add(routeGroup);
        main.Controls.Add(outputGroup);

        window.Controls.Add(main);
        window.Controls.Add(header);

        Application.Run(window);
    }
}

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
            Width = 650,
            Height = 400,
            StartPosition = FormStartPosition.CenterScreen
        };

        Label title = new Label
        {
            Text = "H145 Flight Plan Generator",
            Font = new Font("Segoe UI", 20, FontStyle.Bold),
            AutoSize = true,
            Left = 140,
            Top = 90
        };

        Label message = new Label
        {
            Text = "GitHub EXE build test successful!",
            Font = new Font("Segoe UI", 12),
            AutoSize = true,
            Left = 185,
            Top = 160
        };

        Button button = new Button
        {
            Text = "Test Button",
            Width = 140,
            Height = 40,
            Left = 245,
            Top = 215
        };

        button.Click += (_, _) =>
        {
            MessageBox.Show(
                "The application is working.",
                "H145 Flight Plan Generator");
        };

        window.Controls.Add(title);
        window.Controls.Add(message);
        window.Controls.Add(button);

        Application.Run(window);
    }
}

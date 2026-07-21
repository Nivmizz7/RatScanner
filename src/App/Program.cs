using System;
using System.Windows.Forms;

namespace RatScanner;

internal static class Program
{
    [STAThread]
    public static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        App app = new();
        app.InitializeComponent();
        app.Run();
    }
}

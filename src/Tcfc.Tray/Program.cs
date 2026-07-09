namespace Tcfc.Tray;

/// <summary>
/// Tray app entry point. The app lives entirely in the notification area;
/// there is no main window (see <see cref="TrayApp"/>).
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}

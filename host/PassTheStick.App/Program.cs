using Velopack;

namespace PassTheStick.App;

internal static class Program
{
    /// <summary>
    /// Velopack must run first: when the installer or updater starts the app with a hook argument (install, update,
    /// uninstall) it handles that and exits before any window or driver code runs.
    /// </summary>
    [STAThread]
    private static void Main()
    {
        VelopackApp.Build().Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}

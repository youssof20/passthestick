using System.Diagnostics;
using System.Windows;

namespace PassTheStick.Launcher;

public partial class App : Application
{
    private void App_OnStartup(object sender, StartupEventArgs e)
    {
        bool guest = false, host = false;
        foreach (var arg in e.Args)
        {
            if (arg.Equals("--guest", StringComparison.OrdinalIgnoreCase)) guest = true;
            if (arg.Equals("--host", StringComparison.OrdinalIgnoreCase)) host = true;
        }
        string hostPath = Path.Combine(AppContext.BaseDirectory, "PassTheStick.Host.exe");
        string guestPath = Path.Combine(AppContext.BaseDirectory, "PassTheStick.Guest.exe");
        if (guest && File.Exists(guestPath))
        {
            Process.Start(guestPath);
            Shutdown();
            return;
        }
        if (host && File.Exists(hostPath))
        {
            Process.Start(hostPath);
            Shutdown();
            return;
        }
        var choice = new LauncherWindow();
        if (choice.ShowDialog() == true)
        {
            if (choice.IsGuest && File.Exists(guestPath))
                Process.Start(guestPath);
            else if (File.Exists(hostPath))
                Process.Start(hostPath);
        }
        Shutdown();
    }
}

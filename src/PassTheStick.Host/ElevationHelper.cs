using System.Diagnostics;
using System.Windows;

namespace PassTheStick.Host;

public static class ElevationHelper
{
    public static bool IsRunningAsAdmin()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static void RestartAsAdmin()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? Application.ResourceAssembly.Location,
            UseShellExecute = true,
            Verb = "runas"
        };
        try
        {
            Process.Start(startInfo);
            Application.Current.Shutdown();
        }
        catch
        {
            // User cancelled UAC or start failed
        }
    }
}

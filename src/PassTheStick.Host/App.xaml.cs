using System.Net.Http;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace PassTheStick.Host;

public partial class App : System.Windows.Application
{
    private void App_OnStartup(object sender, StartupEventArgs e)
    {
        SessionEnding += (_, args) =>
        {
            try
            {
                Current.MainWindow?.Close();
            }
            finally
            {
                args.Cancel = false;
                Shutdown();
            }
        };

        if (!ElevationHelper.IsRunningAsAdmin())
        {
            var result = System.Windows.MessageBox.Show(
                "Some games require admin rights to receive input. Restart as administrator?",
                "PassTheStick",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
                ElevationHelper.RestartAsAdmin();
        }
        var shell = new ShellWindow();
        MainWindow = shell;
        shell.Show();
        shell.Closed += (_, _) => Shutdown();

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                await CheckForUpdatesAsync(shell);
            }
            catch
            {
                // Never crash on update check.
            }
        });
    }

    private static async Task CheckForUpdatesAsync(ShellWindow shell)
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PassTheStick");

            var json = await http.GetStringAsync("https://api.github.com/repos/youssof20/passthestick/releases/latest");
            using var doc = JsonDocument.Parse(json);

            var tag = doc.RootElement.GetProperty("tag_name").GetString()?.Trim();
            var htmlUrl = doc.RootElement.GetProperty("html_url").GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(htmlUrl))
                return;

            var latestVersionText = tag.TrimStart('v');
            if (!Version.TryParse(latestVersionText, out var latestVersion))
                return;

            var currentVersion = typeof(App).Assembly.GetName().Version;
            if (currentVersion == null)
                return;

            // Compare by major/minor/build (ignore revision).
            var currentTriplet = new Version(currentVersion.Major, currentVersion.Minor, currentVersion.Build);
            var latestTriplet = new Version(latestVersion.Major, latestVersion.Minor, latestVersion.Build);

            if (latestTriplet > currentTriplet)
                main.ShowUpdateNotification(latestTriplet.ToString(3), htmlUrl);
        }
        catch
        {
            // Never crash on update check failure.
        }
    }
}

using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using PassTheStick.Shared;

namespace PassTheStick.Host;

public partial class SettingsPage : UserControl
{
    public Action? OnSettingsSaved { get; set; }

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            var s = SettingsStore.Load();
            SettingsRelayUrlText.Text = s.RelayUrlOverride ?? string.Empty;
            SettingsSoundsCheck.IsChecked = s.EnableStickSounds;
        };
    }

    public void SetVersionLabel(string versionText)
    {
        try
        {
            AboutVersionText.Text = $"PassTheStick {versionText} — shared keyboard for couch co-op over the network.";
        }
        catch
        {
            // ignore
        }
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var s = SettingsStore.Load();
            var url = (SettingsRelayUrlText.Text ?? string.Empty).Trim();
            s.RelayUrlOverride = string.IsNullOrEmpty(url) ? null : url;
            s.EnableStickSounds = SettingsSoundsCheck.IsChecked == true;
            SettingsStore.Save(s);
            OnSettingsSaved?.Invoke();
            System.Windows.MessageBox.Show("Settings saved.", "PassTheStick", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "PassTheStick", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void GitHub_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/youssof20/passthestick",
                UseShellExecute = true
            });
        }
        catch
        {
            // ignore
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
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
            {
                System.Windows.MessageBox.Show("Could not read release info.", "PassTheStick", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var latestVersionText = tag.TrimStart('v');
            if (!Version.TryParse(latestVersionText, out var latestVersion))
            {
                System.Windows.MessageBox.Show("Could not parse version.", "PassTheStick", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var currentVersion = Assembly.GetExecutingAssembly().GetName().Version;
            if (currentVersion == null) return;
            var currentTriplet = new Version(currentVersion.Major, currentVersion.Minor, currentVersion.Build);
            var latestTriplet = new Version(latestVersion.Major, latestVersion.Minor, latestVersion.Build);

            if (latestTriplet > currentTriplet)
            {
                var go = System.Windows.MessageBox.Show(
                    $"A newer version is available: {tag}\n\nOpen the download page?",
                    "PassTheStick",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);
                if (go == MessageBoxResult.Yes)
                    Process.Start(new ProcessStartInfo { FileName = htmlUrl, UseShellExecute = true });
            }
            else
            {
                System.Windows.MessageBox.Show("You're on the latest release.", "PassTheStick", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("Update check failed:\n" + ex.Message, "PassTheStick", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

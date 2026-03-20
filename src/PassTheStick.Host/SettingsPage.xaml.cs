using System.Diagnostics;
using System.Net.WebSockets;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PassTheStick.Shared;

namespace PassTheStick.Host;

public partial class SettingsPage : UserControl
{
    public Action? OnSettingsSaved { get; set; }

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += SettingsPage_Loaded;
    }

    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        ReloadFromStore();
    }

    /// <summary>Reload controls from disk (e.g. after migration).</summary>
    public void ReloadFromStore()
    {
        var s = SettingsStore.Load();
        SettingsRelayUrlText.Text = s.RelayUrlOverride ?? string.Empty;
        SettingsSoundsCheck.IsChecked = s.EnableStickSounds;
        AutoFocusGameCheck.IsChecked = s.AutoFocusGameOnStickReceive;
        ReleaseHeldKeysCheck.IsChecked = s.ReleaseHeldKeysOnStickPass;
        ShowOverlayCheck.IsChecked = s.ShowOverlayDuringSessions;
        RefreshHotkeyLabels(s);
        RelayTestResult.Text = string.Empty;
    }

    private void RefreshHotkeyLabels(AppSettings? s = null)
    {
        s ??= SettingsStore.Load();
        PassHotkeyLabel.Text = KeyNames.FormatRegisterHotKey(s.PassStickHotkeyModifiers, s.PassStickHotkeyVk);
        TakeBackHotkeyLabel.Text = KeyNames.FormatRegisterHotKey(s.TakeStickBackHotkeyModifiers, s.TakeStickBackHotkeyVk);
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

    private void PersistRelayUrlFromUi()
    {
        var s = SettingsStore.Load();
        var url = (SettingsRelayUrlText.Text ?? string.Empty).Trim();
        s.RelayUrlOverride = string.IsNullOrEmpty(url) ? null : url;
        SettingsStore.Save(s);
        OnSettingsSaved?.Invoke();
    }

    private void SettingsRelayUrlText_LostFocus(object sender, RoutedEventArgs e) =>
        PersistRelayUrlFromUi();

    private async void TestRelayConnection_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            RelayTestResult.Text = "Testing…";
            RelayTestResult.Foreground = (Brush)FindResource("PtsBrushTextBody");

            var raw = (SettingsRelayUrlText.Text ?? string.Empty).Trim();
            var probe = new AppSettings
            {
                RelayUrlOverride = string.IsNullOrEmpty(raw) ? null : raw,
                SettingsSchemaVersion = 2
            };
            var wsUrl = Constants.ResolveRelayUrl(probe);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var ws = new ClientWebSocket();
            ws.Options.Proxy = null;
            var sw = Stopwatch.StartNew();
            await ws.ConnectAsync(new Uri(wsUrl), cts.Token);
            sw.Stop();

            RelayTestResult.Foreground = (Brush)FindResource("PtsBrushGreen");
            RelayTestResult.Text = $"✓ Connected ({sw.ElapsedMilliseconds}ms)";
            PersistRelayUrlFromUi();
        }
        catch (Exception ex)
        {
            RelayTestResult.Foreground = (Brush)FindResource("PtsBrushRed");
            RelayTestResult.Text = "✗ Failed: " + ex.Message;
        }
    }

    private void ChangePassHotkey_Click(object sender, RoutedEventArgs e) =>
        RunHotkeyCapture(isPassStick: true);

    private void ChangeTakeBackHotkey_Click(object sender, RoutedEventArgs e) =>
        RunHotkeyCapture(isPassStick: false);

    private void RunHotkeyCapture(bool isPassStick)
    {
        var owner = Window.GetWindow(this);
        var cap = new HotkeyCaptureWindow { Owner = owner };
        cap.Completed += (mods, vk) =>
        {
            var s = SettingsStore.Load();
            if (isPassStick)
            {
                s.PassStickHotkeyModifiers = mods;
                s.PassStickHotkeyVk = vk;
            }
            else
            {
                s.TakeStickBackHotkeyModifiers = mods;
                s.TakeStickBackHotkeyVk = vk;
            }

            if (s.PassStickHotkeyModifiers == s.TakeStickBackHotkeyModifiers &&
                s.PassStickHotkeyVk == s.TakeStickBackHotkeyVk)
            {
                System.Windows.MessageBox.Show(
                    "Pass and take-back shortcuts must be different.",
                    "PassTheStick",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            SettingsStore.Save(s);
            RefreshHotkeyLabels(s);
            OnSettingsSaved?.Invoke();
        };
        cap.ShowDialog();
    }

    private void BehaviorCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;

        try
        {
            var s = SettingsStore.Load();
            s.AutoFocusGameOnStickReceive = AutoFocusGameCheck.IsChecked == true;
            s.ReleaseHeldKeysOnStickPass = ReleaseHeldKeysCheck.IsChecked == true;
            s.ShowOverlayDuringSessions = ShowOverlayCheck.IsChecked == true;
            s.EnableStickSounds = SettingsSoundsCheck.IsChecked == true;
            SettingsStore.Save(s);
            OnSettingsSaved?.Invoke();
        }
        catch
        {
            // ignore
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

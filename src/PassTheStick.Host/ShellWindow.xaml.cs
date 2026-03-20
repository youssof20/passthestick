using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PassTheStick.Shared;

namespace PassTheStick.Host;

public partial class ShellWindow : Window
{
    private readonly HostPage _hostPage = new();
    private readonly GuestPage _guestPage = new();
    private readonly SettingsPage _settingsPage = new();
    private string _nav = "host";
    private int _onboardingStep;
    private string? _latestUpdateUrl;

    public ShellWindow()
    {
        InitializeComponent();

        _settingsPage.OnSettingsSaved = () => _hostPage.RefreshSettingsFromStore();

        MainContent.Content = _hostPage;
        ApplyNavVisuals();

        Loaded += ShellWindow_Loaded;
    }

    private void ShellWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            var text = v == null ? "v—" : $"v{v.Major}.{v.Minor}.{v.Build}";
            SidebarVersionText.Text = text;
            _settingsPage.SetVersionLabel(text);

            var loadedSettings = SettingsStore.Load();
            if (!loadedSettings.OnboardingCompleted)
                ShowOnboarding();

            _settingsPage.ReloadFromStore();
        }
        catch
        {
            // ignore
        }
    }

    public void ShowUpdateNotification(string latestVersion, string url)
    {
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                _latestUpdateUrl = url;
                UpdateBannerText.Text = $"Update available: v{latestVersion}";
                UpdateBanner.Visibility = Visibility.Visible;
                _hostPage.ShowUpdateToast(latestVersion, url);
            });
        }
        catch
        {
            // ignore
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
            try { DragMove(); } catch { /* drag can fail */ }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        Close();

    private void UpdateDownload_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_latestUpdateUrl))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _latestUpdateUrl,
                    UseShellExecute = true
                });
        }
        catch
        {
            // ignore
        }
    }

    private void NavButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag)
            return;
        _nav = tag;
        MainContent.Content = tag switch
        {
            "guest" => _guestPage,
            "settings" => _settingsPage,
            _ => _hostPage
        };
        ApplyNavVisuals();
    }

    private void ApplyNavVisuals()
    {
        SetNav(NavHost, HostNavBorder, _nav == "host");
        SetNav(NavGuest, GuestNavBorder, _nav == "guest");
        SetNav(NavSettings, SettingsNavBorder, _nav == "settings");
    }

    private void SetNav(Button btn, Border accentBorder, bool active)
    {
        var primary = TryFindResource("PtsBrushPrimary") as SolidColorBrush;
        var muted = TryFindResource("PtsBrushTextMuted") as SolidColorBrush;
        accentBorder.BorderBrush = active ? primary ?? Brushes.Transparent : Brushes.Transparent;

        var sp = btn.Content as StackPanel;
        if (sp == null || sp.Children.Count < 2) return;
        var icon = sp.Children[0] as TextBlock;
        var label = sp.Children[1] as TextBlock;
        if (active)
        {
            if (icon != null) icon.Foreground = primary ?? new SolidColorBrush(Color.FromRgb(0x00, 0xC8, 0x96));
            if (label != null) label.Foreground = primary ?? new SolidColorBrush(Color.FromRgb(0x00, 0xC8, 0x96));
        }
        else
        {
            if (icon != null) icon.Foreground = muted ?? new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
            if (label != null) label.Foreground = muted ?? new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
        }

        btn.Background = active
            ? new SolidColorBrush(Color.FromArgb(0x26, 0x00, 0xC8, 0x96))
            : Brushes.Transparent;
    }

    private void ShowOnboarding()
    {
        _onboardingStep = 0;
        OnboardingOverlay.Visibility = Visibility.Visible;
        ApplyOnboardingStep();
    }

    private void ApplyOnboardingStep()
    {
        switch (_onboardingStep)
        {
            case 0:
                OnboardingTitle.Text = "Welcome to PassTheStick";
                OnboardingBody.Text =
                    "Share one keyboard (and gamepad) between friends over the network while everyone watches the same screen.";
                OnboardingPrimaryButton.Content = "Next";
                break;
            case 1:
                OnboardingTitle.Text = "Stay connected";
                OnboardingBody.Text =
                    "Use the cloud relay by default, start a local relay from the tray for LAN, or set a custom WebSocket URL in Settings.";
                OnboardingPrimaryButton.Content = "Next";
                break;
            default:
                OnboardingTitle.Text = "You're ready";
                OnboardingBody.Text =
                    "Open Host, pin your game window, share the room code, and use Ctrl+Shift+Right to pass the stick.";
                OnboardingPrimaryButton.Content = "Get started";
                break;
        }
    }

    private void OnboardingPrimary_Click(object sender, RoutedEventArgs e)
    {
        if (_onboardingStep < 2)
        {
            _onboardingStep++;
            ApplyOnboardingStep();
        }
        else
            FinishOnboarding();
    }

    private void OnboardingSkip_Click(object sender, RoutedEventArgs e) => FinishOnboarding();

    private void FinishOnboarding()
    {
        try
        {
            var s = SettingsStore.Load();
            s.OnboardingCompleted = true;
            SettingsStore.Save(s);
        }
        catch
        {
            // ignore
        }
        OnboardingOverlay.Visibility = Visibility.Collapsed;
    }
}

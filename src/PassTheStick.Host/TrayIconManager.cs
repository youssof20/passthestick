using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Hardcodet.Wpf.TaskbarNotification;
using PassTheStick.Shared;

namespace PassTheStick.Host;

/// <summary>WPF tray icon (no WinForms). Uses Hardcodet.NotifyIcon.Wpf.</summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly TaskbarIcon _taskbarIcon;
    private readonly ContextMenu _contextMenu = new();
    private readonly Func<IReadOnlyList<PlayerInfo>> _getPlayers;
    private readonly Action<PlayerInfo> _passStickTo;
    private readonly Action _pinGame;
    private readonly Action _soloTest;
    private readonly Action _takeStickBack;
    private readonly Action _showConnectionStatus;
    private readonly Func<bool> _isRelayRunning;
    private readonly Action _startRelay;
    private readonly Action _stopRelay;
    private readonly Action _endSession;
    private readonly Action _testInjection;
    private readonly Action _exit;
    private string? _pendingUpdateUrl;

    public TrayIconManager(
        SessionManager session,
        Func<IReadOnlyList<PlayerInfo>> getPlayers,
        Action<PlayerInfo> passStickTo,
        Action pinGame,
        Action soloTest,
        Action takeStickBack,
        Action showConnectionStatus,
        Func<bool> isRelayRunning,
        Action startRelay,
        Action stopRelay,
        Action endSession,
        Action testInjection,
        Action exit)
    {
        _ = session;
        _getPlayers = getPlayers;
        _passStickTo = passStickTo;
        _pinGame = pinGame;
        _soloTest = soloTest;
        _takeStickBack = takeStickBack;
        _showConnectionStatus = showConnectionStatus;
        _isRelayRunning = isRelayRunning;
        _startRelay = startRelay;
        _stopRelay = stopRelay;
        _endSession = endSession;
        _testInjection = testInjection;
        _exit = exit;

        var iconInfo =
            System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/appicon.png"))
            ?? System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/passthestick.ico"));
        if (iconInfo == null)
            throw new InvalidOperationException("Missing embedded app icon resource.");

        using var ms = new MemoryStream();
        iconInfo.Stream.CopyTo(ms);
        ms.Position = 0;

        _taskbarIcon = new TaskbarIcon
        {
            ToolTipText = "PassTheStick",
            IconSource = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad)
        };

        _contextMenu.Opened += ContextMenu_Opened;
        _taskbarIcon.ContextMenu = _contextMenu;
        _taskbarIcon.TrayMouseDoubleClick += (_, _) =>
            System.Windows.Application.Current.Dispatcher.Invoke(_pinGame);

        _taskbarIcon.TrayBalloonTipClicked += (_, _) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_pendingUpdateUrl)) return;
                var psi = new System.Diagnostics.ProcessStartInfo(_pendingUpdateUrl) { UseShellExecute = true };
                System.Diagnostics.Process.Start(psi);
            }
            catch { }
        };
    }

    private void ContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        _contextMenu.Items.Clear();

        AddItem("Pin current window as game", _pinGame);
        AddItem("Take stick back", _takeStickBack);
        AddItem("End session (close room)", _endSession);
        AddItem("Connection status…", _showConnectionStatus);
        _contextMenu.Items.Add(new Separator());

        _contextMenu.Items.Add(new MenuItem { Header = "Pass stick to:", IsEnabled = false });

        var players = _getPlayers();
        foreach (var p in players)
        {
            var pl = p;
            AddItem(pl.Name, () => _passStickTo(pl));
        }

        if (players.Count == 0)
            _contextMenu.Items.Add(new MenuItem { Header = "(no guests yet)", IsEnabled = false });

        _contextMenu.Items.Add(new Separator());

        var relayRunning = _isRelayRunning();
        AddItem(relayRunning ? "Relay running (stop)" : "Start relay server", () =>
        {
            if (_isRelayRunning()) _stopRelay(); else _startRelay();
        });

        AddItem("Test injection", _testInjection);
        AddItem("Solo test mode", _soloTest);
        _contextMenu.Items.Add(new Separator());
        AddItem("Exit", _exit);
    }

    private void AddItem(string header, Action action)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => System.Windows.Application.Current.Dispatcher.Invoke(action);
        _contextMenu.Items.Add(mi);
    }

    private static void Ui(Action a)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d == null) return;
        if (d.CheckAccess()) a();
        else d.Invoke(a);
    }

    public void ShowToast(string title, string message) =>
        Ui(() => _taskbarIcon.ShowBalloonTip(title, message, BalloonIcon.Info));

    public void ShowUpdateToast(string title, string message, string updateUrl)
    {
        _pendingUpdateUrl = updateUrl;
        Ui(() => _taskbarIcon.ShowBalloonTip(title, message, BalloonIcon.Info));
    }

    public void Dispose()
    {
        try { _taskbarIcon.Dispose(); }
        catch { }
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using PassTheStick.Shared;

namespace PassTheStick.Host;

public partial class MainWindow : Window
{
    private readonly SessionManager _sessionManager;
    private readonly HookManager _hookManager;
    private readonly GameWindowTracker _gameTracker;
    private readonly ViGEmControllerInjector _vigem = new();
    private readonly HotkeyManager _hotkey = new();
    private OverlayWindow? _overlay;
    private PassStickPickerWindow? _picker;
    private RelayClient? _relay;
    private TrayIconManager? _tray;
    private RelayProcessManager? _relayProcess;
    private CancellationTokenSource? _reconnectCts;

    public MainWindow()
    {
        InitializeComponent();
        _sessionManager = new SessionManager();
        _hookManager = new HookManager(_sessionManager);
        _gameTracker = new GameWindowTracker();
        _hookManager.Install();
        StickToggle.Checked += (_, _) => _sessionManager.SetActivePlayer(_sessionManager.LocalPlayerId);
        StickToggle.Unchecked += (_, _) => _sessionManager.SetActivePlayer("__remote__");
        Closed += (_, _) =>
        {
            _reconnectCts?.Cancel();
            _tray?.Dispose();
            _relayProcess?.Dispose();
            _hotkey.Dispose();
            _overlay?.Close();
            _picker?.Close();
            _hookManager.Dispose();
            _vigem.Dispose();
            _relay?.Dispose();
        };
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        while (true)
        {
            try
            {
                _relay = new RelayClient();
                _relay.PlayerListReceived += OnPlayerList;
                _relay.KeyEventReceived += OnKeyEvent;
                _relay.PadStateReceived += OnPadState;
                _relay.Disconnected += OnDisconnected;
                await _relay.ConnectAsync();
                var code = await _relay.CreateRoomAsync();
                _sessionManager.LocalPlayerId = _relay.MyId;
                _sessionManager.SetActivePlayer(_relay.MyId);
                RoomCodeLabel.Text = "Room code: " + code;
                StatusText.Text = "Connected. Pin your game window. Use Ctrl+Shift+Right to pass the stick.";
                _overlay = new OverlayWindow();
                _overlay.SetPlayerName("Host");
                _overlay.Show();
                _picker = new PassStickPickerWindow(OnPickGuest);
                _picker.SetPlayers(_sessionManager.Players);
                _relayProcess = new RelayProcessManager();
                _tray = new TrayIconManager(
                    _sessionManager,
                    () => _sessionManager.Players,
                    OnPickGuest,
                    () => _gameTracker.PinCurrentForeground(),
                    SoloTestModeAsync,
                    () => _relayProcess.IsRunning,
                    StartRelayServerFromTray,
                    StopRelayServerFromTray);
                _tray.ShowToast("PassTheStick", "Host session started. Share the room code with friends.");
                var helper = new WindowInteropHelper(this);
                helper.EnsureHandle();
                _hotkey.Register(helper.Handle);
                var src = HwndSource.FromHwnd(helper.Handle);
                src?.AddHook(WndProc);
                break;
            }
            catch
            {
                var dlg = new PassTheStick.Shared.RelayConnectionDialog { Owner = this };
                dlg.ShowDialog();
                if (dlg.ShouldChangeUrl)
                {
                    var input = Microsoft.VisualBasic.Interaction.InputBox(
                        "Enter relay URL (ws://... or wss://...).",
                        "PassTheStick",
                        PassTheStick.Shared.Constants.RelayWebSocketUrl);
                    if (!string.IsNullOrWhiteSpace(input))
                    {
                        var s = PassTheStick.Shared.SettingsStore.Load();
                        s.RelayUrlOverride = input.Trim();
                        PassTheStick.Shared.SettingsStore.Save(s);
                    }
                }
                if (!dlg.ShouldRetry && !dlg.ShouldChangeUrl)
                {
                    StatusText.Text = "Can't reach the relay server.";
                    break;
                }
            }
        }
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (HotkeyManager.IsHotkeyMessage(msg, wParam))
        {
            Dispatcher.Invoke(() =>
            {
                _picker?.SetPlayers(_sessionManager.Players);
                _picker?.ShowNearCursor();
            });
            handled = true;
        }
        return nint.Zero;
    }

    private void OnPickGuest(PlayerInfo p)
    {
        if (_relay == null) return;
        _relay.SendPassStickAsync(p.Id);
        _sessionManager.SetActivePlayer(p.Id);
        StickToggle.IsChecked = false;
        _overlay?.SetPlayerName(p.Name);
        _overlay?.SetBanner(null);
        StatusText.Text = "Stick passed to " + p.Name;
    }

    private void OnPlayerList(List<PlayerInfo> players)
    {
        Dispatcher.Invoke(() =>
        {
            _sessionManager.UpdatePlayers(players);
            PlayersList.ItemsSource = null;
            PlayersList.ItemsSource = _sessionManager.Players;
            PassStickButton.IsEnabled = _sessionManager.Players.Count > 0;
            _picker?.SetPlayers(_sessionManager.Players);
            UpdateOverlayName();
        });
    }

    private void UpdateOverlayName()
    {
        var id = _sessionManager.ActivePlayerId;
        if (string.IsNullOrEmpty(id) || id == _sessionManager.LocalPlayerId)
            _overlay?.SetPlayerName("Host");
        else
        {
            var p = _sessionManager.Players.FirstOrDefault(x => x.Id == id);
            _overlay?.SetPlayerName(p?.Name ?? "Guest");
        }
    }

    private void OnKeyEvent(KeyEventMessage msg)
    {
        if (!_gameTracker.IsGameForeground()) return;
        KeyboardInjectionHelper.InjectKeyEvent(msg);
    }

    private void OnPadState(PadStateMessage msg)
    {
        if (!_gameTracker.IsGameForeground()) return;
        try
        {
            _vigem.EnsureConnected();
            _vigem.FeedReport(msg);
        }
        catch
        {
            _tray?.ShowToast(
                "PassTheStick",
                "Controller support requires ViGEmBus. Download it from github.com/nefarius/ViGEmBus/releases and restart.");
        }
    }

    private void OnDisconnected(string _)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = "Connection lost — reconnecting…";
            _overlay?.SetBanner("Connection lost — reconnecting…");
        });
        StartReconnectLoop();
    }

    private void StartRelayServerFromTray()
    {
        try
        {
            var port = _relayProcess?.StartRelayWithPortFallback(AppContext.BaseDirectory, 8080, 8082) ?? 8080;
            var s = SettingsStore.Load();
            s.RelayUrlOverride = $"ws://localhost:{port}";
            SettingsStore.Save(s);
            _tray?.ShowToast("PassTheStick", $"Relay server started on ws://localhost:{port}");
        }
        catch
        {
            MessageBox.Show(
                "Can't start the relay server.\n\nMake sure PassTheStick was installed with the bundled relay runtime, or run the relay manually:\n\ncd src/PassTheStick.Relay\nnpm install && node server.js",
                "PassTheStick",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void StopRelayServerFromTray()
    {
        _relayProcess?.Stop();
        _tray?.ShowToast("PassTheStick", "Relay server stopped.");
    }

    private async void SoloTestModeAsync()
    {
        _tray?.ShowToast("PassTheStick", "Solo test starting…");
        await Task.Delay(TimeSpan.FromSeconds(2));
        _sessionManager.SetActivePlayer("__test__");
        StickToggle.IsChecked = false;
        _overlay?.SetPlayerName("Test Player");
        _overlay?.SetBanner("Test Player has the stick");
        await Task.Delay(TimeSpan.FromSeconds(5));
        _sessionManager.SetActivePlayer(_sessionManager.LocalPlayerId);
        StickToggle.IsChecked = true;
        _overlay?.SetPlayerName("Host");
        _overlay?.SetBanner(null);
        _tray?.ShowToast("PassTheStick", "Solo test complete — keyboard blocking and passing both work correctly.");
    }

    private void StartReconnectLoop()
    {
        _reconnectCts?.Cancel();
        _reconnectCts = new CancellationTokenSource();
        var ct = _reconnectCts.Token;
        _ = Task.Run(async () =>
        {
            for (int attempt = 1; attempt <= 10 && !ct.IsCancellationRequested; attempt++)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    _relay?.Dispose();
                    _relay = new RelayClient();
                    _relay.PlayerListReceived += OnPlayerList;
                    _relay.KeyEventReceived += OnKeyEvent;
                    _relay.PadStateReceived += OnPadState;
                    _relay.Disconnected += OnDisconnected;
                    await _relay.ConnectAsync();
                    var code = await _relay.CreateRoomAsync();
                    Dispatcher.Invoke(() =>
                    {
                        RoomCodeLabel.Text = "Room code: " + code;
                        StatusText.Text = "Reconnected.";
                        _overlay?.SetBanner(null);
                    });
                    return;
                }
                catch
                {
                    // keep retrying
                }
            }
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = "Could not reconnect. Please restart the session.";
                _overlay?.SetBanner("Could not reconnect. Please restart the session.");
            });
        }, ct);
    }

    private void PinGameButton_Click(object sender, RoutedEventArgs e)
    {
        _gameTracker.PinCurrentForeground();
        StatusText.Text = "Game window pinned. Focus the game and pass the stick to test.";
            if (!ElevationHelper.IsRunningAsAdmin())
            {
                _tray?.ShowToast(
                    "PassTheStick",
                    "This game may need PassTheStick to run as administrator. Right-click the app and choose Run as administrator.");
            }
    }

    private void PassStickButton_Click(object sender, RoutedEventArgs e)
    {
        if (PlayersList.SelectedItem is not PlayerInfo p || _relay == null) return;
        OnPickGuest(p);
    }
}

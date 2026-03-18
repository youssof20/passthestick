using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using PassTheStick.Shared;
using WinForms = System.Windows.Forms;

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
    private bool _sessionStarted;

    public MainWindow()
    {
        InitializeComponent();
        _sessionManager = new SessionManager();
        _gameTracker = new GameWindowTracker();
        _hookManager = new HookManager(_sessionManager, _gameTracker);
        _hookManager.Install();
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

        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();
        _hotkey.Register(helper.Handle);
        var src = HwndSource.FromHwnd(helper.Handle);
        src?.AddHook(WndProc);

        RoomCodeLabel.Text = "Room code: —";
        StatusText.Text = "Select and pin your game window to start a session.";
        PassStickButton.IsEnabled = false;
        RefreshWindows();

        await Task.CompletedTask;
    }

    private void RefreshWindows()
    {
        try
        {
            var items = WindowEnumerator.GetCandidateWindows();
            WindowPicker.ItemsSource = items;
            if (items.Count > 0)
                WindowPicker.SelectedIndex = 0;
        }
        catch
        {
            WindowPicker.ItemsSource = Array.Empty<WindowInfo>();
        }
    }

    private void RefreshWindowsButton_Click(object sender, RoutedEventArgs e) => RefreshWindows();

    private async void PinSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowPicker.SelectedItem is not WindowInfo wi)
        {
            System.Windows.MessageBox.Show(
                "Please select a game window first.",
                "PassTheStick",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            _gameTracker.PinWindow(wi.Hwnd);
            PinnedGameLabel.Text = "Game: " + wi.Title;
            StatusText.Text = "Game pinned. Starting session…";
            await EnsureSessionStartedAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                "Couldn't pin that window.\n\n" + ex.Message,
                "PassTheStick",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private async Task EnsureSessionStartedAsync()
    {
        if (_sessionStarted) return;
        if (!_gameTracker.IsPinned)
        {
            StatusText.Text = "Please pin a game window first.";
            return;
        }

        while (true)
        {
            try
            {
                _relay = new RelayClient();
                _relay.PlayerListReceived += OnPlayerList;
                _relay.PassStickReceived += OnPassStickBroadcast;
                _relay.KeyEventReceived += OnKeyEvent;
                _relay.PadStateReceived += OnPadState;
                _relay.Disconnected += OnDisconnected;
                await _relay.ConnectAsync();
                var code = await _relay.CreateRoomAsync();
                _sessionManager.LocalPlayerId = _relay.MyId;
                _sessionManager.SetActivePlayer(_relay.MyId);

                _overlay = new OverlayWindow();
                _overlay.SetPlayerName("Host");
                _overlay.Show();

                _picker = new PassStickPickerWindow(OnPickGuest);
                _picker.SetPlayers(_sessionManager.Players);

                RoomCodeLabel.Text = "Room code: " + code;
                StatusText.Text = "Connected. Use Ctrl+Shift+Right to pass the stick.";
                _tray?.ShowToast("PassTheStick", "Host session started. Share the room code with friends.");
                _sessionStarted = true;
                break;
            }
            catch
            {
                var dlg = new PassTheStick.Shared.RelayConnectionDialog
                {
                    Owner = this,
                    StartRelayRequested = StartRelayServerFromTray
                };
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

    private void OnPassStickBroadcast(string toId)
    {
        Dispatcher.Invoke(() =>
        {
            _sessionManager.SetActivePlayer(toId);
            UpdateOverlayName();
        });
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
            PassStickButton.IsEnabled = _sessionStarted && _gameTracker.IsPinned && _sessionManager.Players.Count > 0;
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
            _relayProcess ??= new RelayProcessManager();
            var port = _relayProcess.StartRelayWithPortFallback(AppContext.BaseDirectory, 8080, 8082);
            var s = SettingsStore.Load();
            s.RelayUrlOverride = $"ws://localhost:{port}";
            SettingsStore.Save(s);
            _tray?.ShowToast("PassTheStick", $"Relay server started on ws://localhost:{port}");
        }
        catch
        {
            System.Windows.MessageBox.Show(
                "Can't start the relay server.\n\nMake sure PassTheStick was installed with the bundled relay runtime.",
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
                    _relay.PassStickReceived += OnPassStickBroadcast;
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

    private void PassStickButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_sessionStarted || _relay == null)
            return;
        if (!_gameTracker.IsPinned)
        {
            System.Windows.MessageBox.Show(
                "Please pin a game window first.",
                "PassTheStick",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
        if (PlayersList.SelectedItem is not PlayerInfo p) return;
        OnPickGuest(p);
    }
}

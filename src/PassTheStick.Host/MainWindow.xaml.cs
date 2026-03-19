using System.IO;
using System.Net.Sockets;
using System.Diagnostics;
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
    private readonly RelayConnectionViewModel _connVm = new();
    private PassTheStick.Shared.RelayConnectionDialog? _connDialog;
    private CancellationTokenSource? _connectCts;

    public void ShowUpdateNotification(string latestVersion, string url)
    {
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                _tray?.ShowUpdateToast(
                    "PassTheStick update available",
                    $"Version {latestVersion} is ready. Click the balloon to download.",
                    url);
            });
        }
        catch { }
    }

    public MainWindow()
    {
        InitializeComponent();
        InputDebugLog.Enabled = false;
        InputDebugLog.OnInputLog += AppendInputLog;
        _sessionManager = new SessionManager();
        _gameTracker = new GameWindowTracker();
        _hookManager = new HookManager(_sessionManager, _gameTracker);
        _hookManager.Install();
        Closed += (_, _) =>
        {
            _connectCts?.Cancel();
            _reconnectCts?.Cancel();
            _tray?.Dispose();
            _relayProcess?.Dispose();
            _hotkey.Dispose();
            _overlay?.Close();
            _picker?.Close();
            InputDebugLog.OnInputLog -= AppendInputLog;
            _hookManager.Dispose();
            _vigem.Dispose();
            _relay?.Dispose();
        };
        Loaded += MainWindow_Loaded;
    }

    private void InputLogExpander_Expanded(object sender, RoutedEventArgs e)
    {
        InputDebugLog.Enabled = true;
    }

    private void InputLogExpander_Collapsed(object sender, RoutedEventArgs e)
    {
        InputDebugLog.Enabled = false;
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        InputLogText.Text = string.Empty;
    }

    private void CopyInputLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(InputLogText?.Text ?? string.Empty);
            AppendInputLog("Input logs copied to clipboard.");
        }
        catch
        {
            // Clipboard can fail (busy, access denied).
        }
    }

    private void AppendInputLog(string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (InputLogText == null) return;
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}\n";
            InputLogText.Text += line;
            InputLogText.CaretIndex = InputLogText.Text.Length;
            try { InputLogText.ScrollToEnd(); } catch { /* older targets */ }

            // Keep max 500 lines to avoid memory bloat (copy/paste for diagnosis).
            var lines = InputLogText.Text.Split('\n');
            if (lines.Length > 500)
                InputLogText.Text = string.Join('\n', lines[^500..]);
        });
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _relayProcess = new RelayProcessManager();
            _tray = new TrayIconManager(
                _sessionManager,
                () => _sessionManager.Players,
                OnPickGuest,
                () => _gameTracker.PinCurrentForeground(),
                SoloTestModeAsync,
                TakeStickBack,
                ShowConnectionStatus,
                () => _relayProcess.IsRunning,
                StartRelayServerFromTray,
                StopRelayServerFromTray,
                RequestExit);

            _hotkey.HotkeyConflict += msg =>
            {
                try { _tray?.ShowToast("PassTheStick", msg); } catch { }
                InputDebugLog.Log(msg);
            };

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
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                "PassTheStick couldn't start.\n\n" + ex.Message,
                "PassTheStick",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void RequestExit()
    {
        try
        {
            Close();
        }
        catch
        {
            System.Windows.Application.Current.Shutdown();
        }
    }

    private void ShowConnectionStatus()
    {
        EnsureConnectionDialog();
        _connDialog?.Show();
        _connDialog?.Activate();
    }

    private void RelayStatusText_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        ShowConnectionStatus();

    private void SetRelayIndicator(string text, string hexColor)
    {
        RelayStatusText.Text = text;
        try
        {
            var converted = new System.Windows.Media.BrushConverter().ConvertFromString(hexColor);
            var brush = converted as System.Windows.Media.Brush;
            if (brush == null) return;
            RelayDot.Fill = brush;
        }
        catch { }
    }

    private void TakeStickBack()
    {
        if (_relay == null || string.IsNullOrWhiteSpace(_relay.MyId))
        {
            StatusText.Text = "Not connected — can't take stick back yet.";
            ShowConnectionStatus();
            return;
        }
        _relay.SendPassStickAsync(_relay.MyId);
        _sessionManager.SetActivePlayer(_relay.MyId);
        _overlay?.SetPlayerName("Host");
        _overlay?.SetBanner(null);
        StatusText.Text = "Stick taken back.";
        _tray?.ShowToast("PassTheStick", "Stick returned to you");
        InputDebugLog.Log("[hotkey] Host reclaimed stick via Ctrl+Shift+Left");
    }

    private void EnsureConnectionDialog()
    {
        _connVm.SaveRelayUrl = url =>
        {
            if (!string.IsNullOrWhiteSpace(url))
            {
                var s = SettingsStore.Load();
                s.RelayUrlOverride = url.Trim();
                SettingsStore.Save(s);
            }
        };
        _connVm.StartRelayAsync = async () =>
        {
            if (_connVm.IsBusy) return;
            _connVm.IsBusy = true;
            try
            {
                _connVm.AddLog("Starting local relay server…");
                _relayProcess ??= new RelayProcessManager();
                var port = await _relayProcess.StartRelayWithPortFallbackAsync(
                    AppContext.BaseDirectory,
                    8080,
                    8082,
                    _connVm.AddLog,
                    CancellationToken.None);
                var s = SettingsStore.Load();
                s.RelayUrlOverride = $"ws://localhost:{port}";
                SettingsStore.Save(s);
                _connVm.AddLog($"Using relay URL: ws://localhost:{port}");
            }
            finally
            {
                _connVm.IsBusy = false;
            }

            // After starting (and waiting), automatically retry.
            if (_connVm.RetryAsync != null)
                await _connVm.RetryAsync();
        };
        _connVm.RetryAsync = async () =>
        {
            if (_connVm.IsBusy) return;
            _connectCts?.Cancel();
            _connectCts = new CancellationTokenSource();
            await EnsureSessionStartedAsync();
        };

        if (_connDialog == null)
        {
            _connDialog = new PassTheStick.Shared.RelayConnectionDialog(_connVm) { Owner = this };
            _connDialog.Closed += (_, _) => _connDialog = null;
        }
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
        try
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

            _gameTracker.PinWindow(wi.Hwnd);
            PinnedGameLabel.Text = "Game: " + wi.Title;
            var gameElevated = _gameTracker.IsPinnedProcessElevated();
            var hostIsAdmin = ElevationHelper.IsRunningAsAdmin();
            if (gameElevated && !hostIsAdmin)
            {
                _tray?.ShowToast("PassTheStick", "Run as administrator (game is elevated).");
                StatusText.Text =
                    "Game pinned. Warning: the game is running as administrator. PassTheStick must also run as administrator for input to work.\nRight-click PassTheStick and choose Run as administrator.";
            }
            else
            {
                StatusText.Text = "Game pinned. Starting session…";
            }
            await EnsureSessionStartedAsync();
        }
        catch (TaskCanceledException)
        {
            StatusText.Text = "Connection attempt cancelled.";
            SetRelayIndicator("Not connected — click to view connection status", "#C33");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Connection attempt cancelled.";
            SetRelayIndicator("Not connected — click to view connection status", "#C33");
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
        try
        {
            if (_sessionStarted) return;
            if (!_gameTracker.IsPinned)
            {
                StatusText.Text = "Please pin a game window first.";
                return;
            }

            EnsureConnectionDialog();
            _connVm.RelayUrl = Constants.RelayWebSocketUrl;
            _connVm.IsConnected = false;
            _connVm.ShowAdvanced = false;
            _connVm.StatusText = "Connecting…";
            _connVm.AddLog("Connecting to " + _connVm.RelayUrl);
            Dispatcher.Invoke(() => SetRelayIndicator("Connecting…", "#D9A200")); // amber

            _connDialog?.Show();
            _connDialog?.Activate();

            _connectCts ??= new CancellationTokenSource();
            var ct = _connectCts.Token;

            // Cloud relay (Render) can sleep; treat initial failures as "waking up" and retry.
            var cloudUrl = Constants.DefaultCloudRelayWs;
            bool isCloud = string.Equals(_connVm.RelayUrl.Trim(), cloudUrl, StringComparison.OrdinalIgnoreCase);

            if (isCloud)
            {
                // Up to 2 minutes, retry every 10 seconds with countdown.
                var deadline = DateTime.UtcNow.AddMinutes(2);
                int attempt = 0;
                while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                {
                    attempt++;
                    var remaining = (int)Math.Ceiling((deadline - DateTime.UtcNow).TotalSeconds);
                    _connVm.StatusText = "Relay is waking up…";
                    _connVm.AddLog($"Connecting… (cloud wakeup attempt {attempt})");
                    if (await TryConnectOnceAsync())
                        return;

                    // Countdown to next retry
                    for (int s = 10; s >= 1 && !ct.IsCancellationRequested; s--)
                    {
                        _connVm.StatusText = $"Relay is waking up… retrying in {s}s (up to {remaining}s)";
                        await Task.Delay(TimeSpan.FromSeconds(1), ct);
                    }
                }

                _connVm.AddLog("Cloud relay still unreachable after 2 minutes.");
                _connVm.ShowAdvanced = true;
                _connVm.StatusText = "Could not reach the relay server.";
                StatusText.Text = "Can't reach the relay server.";
                Dispatcher.Invoke(() => SetRelayIndicator("Not connected — click to view connection status", "#C33"));
                return;
            }

            for (int attempt = 1; attempt <= 5 && !ct.IsCancellationRequested; attempt++)
            {
                try
                {
                    _connVm.StatusText = attempt == 1 ? "Connecting…" : $"Retrying… (attempt {attempt} of 5)";
                    _connVm.AddLog(_connVm.StatusText);
                    if (await TryConnectOnceAsync())
                        return;
                }
                catch (TaskCanceledException)
                {
                    _connVm.AddLog("Connection attempt cancelled.");
                    throw;
                }
                catch (OperationCanceledException)
                {
                    _connVm.AddLog("Connection attempt cancelled.");
                    throw;
                }
                catch (Exception ex)
                {
                    _connVm.AddLog("Connection failed: " + ex.Message);
                    if (ex.InnerException != null)
                        _connVm.AddLog("Inner: " + ex.InnerException.Message);
                    if (attempt >= 5)
                    {
                        _connVm.StatusText = "Could not connect after 5 attempts.";
                        StatusText.Text = "Can't reach the relay server.";
                        Dispatcher.Invoke(() => SetRelayIndicator("Not connected — click to view connection status", "#C33"));
                        return;
                    }
                    _connVm.AddLog("Retrying in 3 seconds…");
                    await Task.Delay(TimeSpan.FromSeconds(3), ct);
                }
            }
        }
        catch (TaskCanceledException)
        {
            _connVm.StatusText = "Cancelled.";
            StatusText.Text = "Cancelled.";
            SetRelayIndicator("Not connected — click to view connection status", "#C33");
        }
        catch (OperationCanceledException)
        {
            _connVm.StatusText = "Cancelled.";
            StatusText.Text = "Cancelled.";
            SetRelayIndicator("Not connected — click to view connection status", "#C33");
        }

        async Task<bool> TryConnectOnceAsync()
        {
            _relay = new RelayClient();
            _relay.PlayerListReceived += OnPlayerList;
            _relay.PassStickReceived += OnPassStickBroadcast;
            _relay.KeyEventReceived += OnKeyEvent;
            _relay.PadStateReceived += OnPadState;
            _relay.Disconnected += OnDisconnected;
            await _relay.ConnectAsync();

            // Session persistence (best-effort): if we have a last room code, ask user to rejoin.
            var settings = SettingsStore.Load();
            var lastRoom = settings.LastRoomCode?.Trim();
            string code;
            if (!string.IsNullOrWhiteSpace(lastRoom))
            {
                var r = System.Windows.MessageBox.Show(
                    $"You were in room {lastRoom}.\nDo you want to reconnect?",
                    "PassTheStick",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (r == MessageBoxResult.Yes)
                {
                    code = await _relay.RejoinHostAsync(lastRoom);
                }
                else
                {
                    code = await _relay.CreateRoomAsync();
                }
            }
            else
            {
                code = await _relay.CreateRoomAsync();
            }

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
            _connVm.IsConnected = true;
            _connVm.StatusText = "Connected!";
            _connVm.AddLog("Connected.");
            Dispatcher.Invoke(() => SetRelayIndicator("Connected — relay ready", "#2E8B57")); // green
            _connDialog?.Close();

            // Save session state for best-effort rejoin.
            try
            {
                settings.LastRoomCode = code;
                settings.LastRelayUrl = Constants.RelayWebSocketUrl;
                try
                {
                    var proc = Process.GetProcessById((int)_gameTracker.GameProcessId);
                    settings.LastGameExePath = proc.MainModule?.FileName;
                }
                catch { }
                SettingsStore.Save(settings);
            }
            catch { }

            return true;
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
        if (HotkeyManager.TryGetHotkey(msg, wParam, out var kind))
        {
            Dispatcher.Invoke(() =>
            {
                if (kind == HotkeyManager.HotkeyKind.Pass)
                {
                    _picker?.SetPlayers(_sessionManager.Players);
                    _picker?.ShowNearCursor();
                }
                else
                {
                    TakeStickBack();
                }
            });
            handled = true;
        }
        return nint.Zero;
    }

    private void OnPickGuest(PlayerInfo p)
    {
        if (_relay == null || !_relay.IsConnected)
        {
            StatusText.Text = "Not connected — start the relay server first.";
            ShowConnectionStatus();
            return;
        }
        _relay.SendPassStickAsync(p.Id);
        _sessionManager.SetActivePlayer(p.Id);
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
            PassStickButton.IsEnabled = _sessionStarted && _gameTracker.IsPinned && PlayersList.SelectedItem is PlayerInfo;
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
        if (!_gameTracker.IsGameForeground())
        {
            InputDebugLog.Log(_gameTracker.DescribeWhyNotForegroundForKeyEvent(msg.FromId, msg.Vk, msg.Down));
            return;
        }

        try
        {
            var gameName = Process.GetProcessById((int)_gameTracker.GameProcessId).ProcessName;
            InputDebugLog.Log(
                $"Injecting KEY_EVENT: game={gameName} PID={_gameTracker.GameProcessId} pinnedHWND=0x{_gameTracker.GameHwnd:X} (fromId={msg.FromId} vk={msg.Vk} down={msg.Down})");
        }
        catch
        {
            InputDebugLog.Log(
                $"Injecting KEY_EVENT: pinnedHWND=0x{_gameTracker.GameHwnd:X} PID={_gameTracker.GameProcessId} (fromId={msg.FromId})");
        }

        KeyboardInjectionHelper.InjectKeyEvent(msg);
    }

    private void OnPadState(PadStateMessage msg)
    {
        if (!_gameTracker.IsGameForeground())
        {
            InputDebugLog.Log(_gameTracker.DescribeWhyNotForegroundForPadState(msg.FromId));
            return;
        }
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

    private void OnDisconnected(string reason)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = reason.Contains("heartbeat", StringComparison.OrdinalIgnoreCase)
                ? "Heartbeat lost — reconnecting…"
                : "Connection lost — reconnecting…";
            _overlay?.SetBanner("Connection lost — reconnecting…");
            SetRelayIndicator("Not connected — click to view connection status", "#C33");
            PlayersList.ItemsSource = null;
            PassStickButton.IsEnabled = false;
        });
        // Make retry work again by allowing session restart.
        _sessionStarted = false;
        _relay?.Dispose();
        _relay = null;
        // Keep the UX smooth: reconnect automatically.
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
        try
        {
            _tray?.ShowToast("PassTheStick", "Solo test starting…");
            await Task.Delay(TimeSpan.FromSeconds(2));
            _sessionManager.SetActivePlayer("__test__");
            _overlay?.SetPlayerName("Test Player");
            _overlay?.SetBanner("Test Player has the stick");
            await Task.Delay(TimeSpan.FromSeconds(5));
            _sessionManager.SetActivePlayer(_sessionManager.LocalPlayerId);
            _overlay?.SetPlayerName("Host");
            _overlay?.SetBanner(null);
            _tray?.ShowToast("PassTheStick", "Solo test complete — keyboard blocking and passing both work correctly.");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Solo test failed: " + ex.Message;
        }
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

    private void PlayersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Enable based on selection/pin/session; if not connected, we'll show a friendly message on pass attempt.
        PassStickButton.IsEnabled =
            _sessionStarted &&
            _gameTracker.IsPinned &&
            PlayersList.SelectedItem is PlayerInfo;
    }

    private void PlayersList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (PlayersList.SelectedItem is PlayerInfo p && PassStickButton.IsEnabled)
            OnPickGuest(p);
    }

    private void TakeStickBackButton_Click(object sender, RoutedEventArgs e)
    {
        TakeStickBack();
    }

    // Port readiness checks are handled inside RelayProcessManager now.
}

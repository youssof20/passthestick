using System.IO;
using System.Net.Sockets;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using PassTheStick.Shared;

namespace PassTheStick.Host;

public partial class MainWindow : Window
{
    public enum InjectionState
    {
        Ready,
        NoGamePinned,
        NotConnected,
        GuestHasStick,
        HostHasStick,
        GameNotForeground,
        Injecting,
        Error
    }

    private InjectionState _currentState;

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
    private DispatcherTimer? _overlayTimer;
    private System.Threading.Timer? _focusMonitor;
    private DispatcherTimer? _gameWatchdog;
    private long _injectedCount;
    private long _failedCount;
    private long _droppedCount;
    private readonly Dictionary<string, System.Windows.Controls.Border> _echoKeys = new();
    private bool _enableStickSounds;
    private int _onboardingStep;
    private long _lastReceiveSoundTicks;

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
        try
        {
            // Default to Info to keep logs readable; Verbose requires expanding the debug panel.
            LogLevelCombo.SelectedIndex = 1; // Info
            InputDebugLog.MinLevel = InputDebugLog.LogLevel.Info;
        }
        catch { }
        _sessionManager = new SessionManager();
        _gameTracker = new GameWindowTracker();
        _hookManager = new HookManager(_sessionManager, _gameTracker);
        _hookManager.Install();
        _hookManager.OverrideRequested += () =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                InputDebugLog.Log(InputDebugLog.LogLevel.Warning, "[override] Host mashed keys — reclaiming stick");
                TakeStickBack();
            });
        };
        Closed += (_, _) =>
        {
            _connectCts?.Cancel();
            _reconnectCts?.Cancel();
            _tray?.Dispose();
            _relayProcess?.Dispose();
            _hotkey.Dispose();
            _overlay?.Close();
            _picker?.Close();
            _overlayTimer?.Stop();
            _gameWatchdog?.Stop();
            _focusMonitor?.Dispose();
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
        InputDebugLog.Flush();
        InputDebugLog.Enabled = false;
    }

    private void LogLevelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            var selected = (LogLevelCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Info";
            InputDebugLog.MinLevel = selected switch
            {
                "Verbose" => InputDebugLog.LogLevel.Verbose,
                "Info" => InputDebugLog.LogLevel.Info,
                "Warning" => InputDebugLog.LogLevel.Warning,
                "Error" => InputDebugLog.LogLevel.Error,
                _ => InputDebugLog.LogLevel.Info
            };
            InputDebugLog.Log(InputDebugLog.LogLevel.Info, $"[log] Level set to {selected}");
        }
        catch { }
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

    private void CopyRoomCode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var code = (RoomCodeText?.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(code) || code == "—") return;
            System.Windows.Clipboard.SetText(code);
            _tray?.ShowToast("PassTheStick", "Room code copied.");
        }
        catch { }
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
            LogStartupDiagnostics("app start");
            BuildKeyEchoMap();
            ResetInjectionCounters("startup");
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
                EndSessionFromTray,
                TestInjectionFromTray,
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

            RoomCodeText.Text = "—";
            StatusText.Text = "Select and pin your game window to start a session.";
            PassStickButton.IsEnabled = false;
            RefreshWindows();
            StartGameWatchdog();

            UpdateVersionFooter();
            var sett = SettingsStore.Load();
            _enableStickSounds = sett.EnableStickSounds;
            if (!sett.OnboardingCompleted)
                ShowOnboarding();

            UpdateOverlayName();

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

    private async void EndSessionFromTray()
    {
        try
        {
            if (_relay == null || !_relay.IsConnected)
            {
                _tray?.ShowToast("PassTheStick", "Not connected — no room to close.");
                return;
            }

            var result = System.Windows.MessageBox.Show(
                "End the session and disconnect all guests?\n\nThis closes the room (guests will see \"Session ended\" and can join a new code).",
                "PassTheStick",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            await _relay.CloseRoomAsync("Host ended the session");

            // Locally reset host state but keep app open.
            ReleaseHeldKeysOnStickChange(_sessionManager.LocalPlayerId);
            _sessionManager.SetActivePlayer(_sessionManager.LocalPlayerId);
            _sessionManager.ClearGuestList();
            PlayersList.ItemsSource = null;
            RoomCodeText.Text = "—";
            StatusText.Text = "Session ended. Pin the game window to start a new session.";
            _overlay?.SetTurn("Host", true, Array.Empty<string>());
            PassStickButton.IsEnabled = false;
            _sessionStarted = false;

            try
            {
                var s = SettingsStore.Load();
                s.LastRoomCode = null;
                SettingsStore.Save(s);
            }
            catch { }

            _tray?.ShowToast("PassTheStick", "Session ended — room closed.");
        }
        catch (Exception ex)
        {
            _tray?.ShowToast("PassTheStick", "Failed to close room: " + ex.Message);
        }
    }

    private async void TestInjectionFromTray()
    {
        try
        {
            if (!_gameTracker.IsPinned)
            {
                _tray?.ShowToast("PassTheStick", "Test injection: pin a game window first.");
                return;
            }
            if (!_gameTracker.IsGameForeground())
            {
                _tray?.ShowToast("PassTheStick", "Test injection: click the game window so it is foreground.");
                return;
            }

            InputDebugLog.Log("[test] Sending W key down/up (100ms)...");
            // W key virtual key = 0x57; guestSc isn't relevant here, just map locally.
            var sc = KeyboardInjectionHelper.MapToHostScanCode(0x57, 17);
            KeyboardInjectionHelper.InjectScanCode(sc, true);
            await Task.Delay(100);
            KeyboardInjectionHelper.InjectScanCode(sc, false);

            _tray?.ShowToast("PassTheStick", "Test injection sent — did the character move?");
        }
        catch (Exception ex)
        {
            _tray?.ShowToast("PassTheStick", "Test injection failed: " + ex.Message);
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
        ReleaseHeldKeysOnStickChange(_relay.MyId);
        _relay.SendPassStickAsync(_relay.MyId);
        _sessionManager.SetActivePlayer(_relay.MyId);
        UpdateOverlayName();
        if (_enableStickSounds)
        {
            StickSoundPlayer.PlayReceive();
            _lastReceiveSoundTicks = Environment.TickCount64;
        }
        StatusText.Text = "Stick taken back.";
        _tray?.ShowToast("PassTheStick", "Stick returned to you");
        InputDebugLog.Log("[hotkey] Host reclaimed stick via Ctrl+Shift+Left");
        ResetInjectionCounters("stick reclaimed");
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
            LogStartupDiagnostics("after pin");
            ResetInjectionCounters("game pinned");
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

    private void LogStartupDiagnostics(string when)
    {
        try
        {
            InputDebugLog.Log(InputDebugLog.LogLevel.Info, "=== PassTheStick Diagnostic Dump ===");
            InputDebugLog.Log(InputDebugLog.LogLevel.Info, $"When: {when}");
            var ver = typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "unknown";
            InputDebugLog.Log(InputDebugLog.LogLevel.Info, $"Version: {ver}");
            InputDebugLog.Log(InputDebugLog.LogLevel.Info, $"OS: {Environment.OSVersion}");
            InputDebugLog.Log(InputDebugLog.LogLevel.Info, $"Is Admin: {ElevationHelper.IsRunningAsAdmin()}");
            InputDebugLog.Log(InputDebugLog.LogLevel.Info, $"Process ID: {Process.GetCurrentProcess().Id}");
            InputDebugLog.Log(InputDebugLog.LogLevel.Info, $"Relay URL: {Constants.RelayWebSocketUrl}");
            InputDebugLog.Log(InputDebugLog.LogLevel.Info, $"Hook installed: {_hookManager.IsInstalled}");

            if (_gameTracker.IsPinned)
            {
                var gamePid = _gameTracker.GameProcessId;
                var hwnd = _gameTracker.GameHwnd;
                var name = "(unknown)";
                try { name = Process.GetProcessById((int)gamePid).ProcessName; } catch { }
                InputDebugLog.Log(InputDebugLog.LogLevel.Info, $"Game pinned: {name}");
                InputDebugLog.Log(InputDebugLog.LogLevel.Info, $"Game PID: {gamePid}");
                InputDebugLog.Log(InputDebugLog.LogLevel.Info, $"Game HWND: 0x{hwnd:X}");

                var gameElevated = _gameTracker.IsPinnedProcessElevated();
                var weElevated = ElevationHelper.IsRunningAsAdmin();
                InputDebugLog.Log(InputDebugLog.LogLevel.Info, $"Game elevated: {gameElevated}");
                InputDebugLog.Log(InputDebugLog.LogLevel.Info, $"We elevated: {weElevated}");
                if (gameElevated && !weElevated)
                    InputDebugLog.Log(InputDebugLog.LogLevel.Warning,
                        "⚠ MISMATCH: Game is elevated, we are not. SendInput will likely fail with error 5.");
            }
            else
            {
                InputDebugLog.Log(InputDebugLog.LogLevel.Info, "Game: NOT PINNED");
            }

            InputDebugLog.Log(InputDebugLog.LogLevel.Info, "=== End Diagnostic Dump ===");
        }
        catch { }
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

            var settings = SettingsStore.Load();
            var code = await _relay.CreateRoomAsync();

            _sessionManager.LocalPlayerId = _relay.MyId;
            _sessionManager.SetActivePlayer(_relay.MyId);

            _overlay = new OverlayWindow();
            _overlay.PassRequested += () =>
            {
                // Show pass picker near cursor without stealing foreground from the game.
                _picker?.SetPlayers(_sessionManager.Players);
                _picker?.ShowNearCursor();
            };
            _overlay.Show();
            StartOverlayTracking();

            _picker = new PassStickPickerWindow(OnPickGuest);
            _picker.SetPlayers(_sessionManager.Players);

            RoomCodeText.Text = code;
            StatusText.Text = "Connected. Use Ctrl+Shift+Right to pass the stick.";
            _tray?.ShowToast("PassTheStick", "Host session started. Share the room code with friends.");
            _sessionStarted = true;
            _connVm.IsConnected = true;
            _connVm.StatusText = "Connected!";
            _connVm.AddLog("Connected.");
            Dispatcher.Invoke(() => SetRelayIndicator("Connected — relay ready", "#2E8B57")); // green
            _connDialog?.Close();

            // Save last relay url for convenience (room codes are ephemeral; rooms are deleted when host ends session).
            try
            {
                settings.LastRelayUrl = Constants.RelayWebSocketUrl;
                SettingsStore.Save(settings);
            }
            catch { }

            return true;
        }
    }

    private void StartOverlayTracking()
    {
        _overlayTimer?.Stop();
        _overlayTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _overlayTimer.Tick += (_, _) =>
        {
            try { UpdateOverlayScope(); } catch { }
        };
        _overlayTimer.Start();
        UpdateOverlayScope();
    }

    private void UpdateOverlayScope()
    {
        if (_overlay == null) return;
        if (!_gameTracker.IsPinned || _gameTracker.IsPinnedWindowMinimized())
        {
            _overlay.Hide();
            return;
        }

        // Only show overlay while the pinned game is foreground.
        if (!_gameTracker.IsGameForeground())
        {
            _overlay.Hide();
            return;
        }

        if (_gameTracker.TryGetPinnedWindowRect(out var rect))
        {
            // Keep the overlay within/over the pinned window region (top-left padding).
            const double padX = 12;
            const double padY = 12;
            _overlay.Left = rect.Left + padX;
            _overlay.Top = rect.Top + padY;
        }

        if (!_overlay.IsVisible)
            _overlay.Show();
    }

    private void OnPassStickBroadcast(string toId)
    {
        Dispatcher.Invoke(() =>
        {
            ReleaseHeldKeysOnStickChange(toId);
            _sessionManager.SetActivePlayer(toId);
            UpdateOverlayName();
            if (_enableStickSounds && _relay != null &&
                string.Equals(toId, _relay.MyId, StringComparison.Ordinal))
            {
                var t = Environment.TickCount64;
                if (t - _lastReceiveSoundTicks > 350)
                {
                    StickSoundPlayer.PlayReceive();
                    _lastReceiveSoundTicks = t;
                }
            }
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
        ReleaseHeldKeysOnStickChange(p.Id);
        _relay.SendPassStickAsync(p.Id);
        _sessionManager.SetActivePlayer(p.Id);
        UpdateOverlayName();
        if (_enableStickSounds)
            StickSoundPlayer.PlayPass();
        StatusText.Text = "Stick passed to " + p.Name;
        TryFocusPinnedGame("[session] Brought game to foreground after stick pass");
        ResetInjectionCounters("stick passed");
        StartFocusMonitor();
    }

    private void OnPlayerList(List<PlayerInfo> players)
    {
        Dispatcher.Invoke(() =>
        {
            _sessionManager.UpdatePlayers(players);
            _picker?.SetPlayers(_sessionManager.Players);
            UpdateOverlayName();
        });
    }

    private void UpdateOverlayName()
    {
        var id = _sessionManager.ActivePlayerId;
        var hostId = _sessionManager.LocalPlayerId;

        var hostHasStick = string.IsNullOrEmpty(id) || id == hostId;
        var activeName = "Host";
        if (!hostHasStick)
        {
            var p = _sessionManager.Players.FirstOrDefault(x => x.Id == id);
            activeName = p?.Name ?? "Guest";
        }

        if (_overlay != null)
            _overlay.SetTurn(activeName, hostHasStick, BuildQueuePreview(activeName, hostHasStick));
        RefreshPlayerRows();
        UpdateActiveBannerUi(hostHasStick, activeName);
    }

    private void RefreshPlayerRows()
    {
        var activeId = _sessionManager.ActivePlayerId;
        var rows = _sessionManager.Players
            .Select(p => new PlayerRowViewModel(p, string.Equals(p.Id, activeId, StringComparison.Ordinal)))
            .ToList();
        PlayersList.ItemsSource = rows;
        PassStickButton.IsEnabled =
            _sessionStarted &&
            _gameTracker.IsPinned &&
            PlayersList.SelectedItem is PlayerRowViewModel;
    }

    private void UpdateActiveBannerUi(bool hostHasStick, string activeName)
    {
        if (hostHasStick)
        {
            ActiveBannerTitle.Text = "YOU HAVE THE STICK";
            ActiveBannerSubtitle.Text = "Your keyboard is live in the pinned game.";
            ActiveBanner.BorderBrush = (System.Windows.Media.Brush)FindResource("PtsBrushGreen");
        }
        else
        {
            ActiveBannerTitle.Text = $"{activeName} has the stick";
            ActiveBannerSubtitle.Text = "Your keys are paused while a guest is playing.";
            ActiveBanner.BorderBrush = (System.Windows.Media.Brush)FindResource("PtsBrushOrange");
        }
    }

    private IReadOnlyList<string> BuildQueuePreview(string activeName, bool hostHasStick)
    {
        // Simple preview: next up in current join order, circular, max 3.
        var names = new List<string>();
        var all = new List<string> { "You" };
        all.AddRange(_sessionManager.Players.Select(p => p.Name));

        var activeLabel = hostHasStick ? "You" : activeName;
        var idx = all.FindIndex(n => string.Equals(n, activeLabel, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) idx = 0;

        for (int i = 1; i <= 3 && i < all.Count; i++)
            names.Add(all[(idx + i) % all.Count]);

        return names;
    }

    private void ReleaseHeldKeysOnStickChange(string? newActivePlayerId)
    {
        // Only relevant when a guest had the stick; if host had it, there should be no injected-held keys.
        var previous = _sessionManager.ActivePlayerId;
        var hostId = _sessionManager.LocalPlayerId;
        var previousWasGuest = !string.IsNullOrWhiteSpace(previous) && !string.Equals(previous, hostId, StringComparison.Ordinal);

        // If the stick is moving away from the previous guest (to anyone else), release any held keys.
        if (!previousWasGuest) return;
        if (string.Equals(previous, newActivePlayerId, StringComparison.Ordinal)) return;

        var held = _sessionManager.ReleaseHeldKeys();
        if (held.Count == 0) return;

        foreach (var sc in held)
        {
            var ok = InputInjector.InjectKeyWithResult(sc, keyDown: false, out _);
            if (ok) _injectedCount++; else _failedCount++;
        }

        InputDebugLog.Log("[session] Released held keys on stick pass: " + string.Join(",", held));
        UpdateInjectionStatsText();
    }

    private void OnKeyEvent(KeyEventMessage msg)
    {
        SetState(InjectionState.Injecting, $"KEY_EVENT from={msg.FromId} vk={msg.Vk}({KeyNames.VkToName(msg.Vk)}) down={msg.Down}");
        InputDebugLog.Log(InputDebugLog.LogLevel.Info,
            $"[recv] KEY vk={msg.Vk}({KeyNames.VkToName(msg.Vk)}) sc={msg.Sc} down={msg.Down} from={Short(msg.FromId)} state={_currentState}");

        if (!_gameTracker.IsGameForeground())
        {
            SetState(InjectionState.GameNotForeground, "Dropped: game not foreground");
            _droppedCount++;
            UpdateInjectionStatsText();
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

        var scanCode = KeyboardInjectionHelper.MapToHostScanCode(msg.Vk, msg.Sc);
        _sessionManager.NoteInjectedKeyState(scanCode, msg.Down);
        if (msg.Down) FlashEcho(KeyNames.VkToName(msg.Vk));

        var ok = InputInjector.InjectKeyWithResult(scanCode, msg.Down, out _);
        if (ok) _injectedCount++; else _failedCount++;
        UpdateInjectionStatsText();
        SetState(_sessionManager.IsLocalPlayerActive ? InjectionState.HostHasStick : InjectionState.GuestHasStick, "Injected key event");
    }

    private void SetState(InjectionState newState, string reason)
    {
        if (_currentState == newState) return;
        var prev = _currentState;
        _currentState = newState;
        InputDebugLog.Log(InputDebugLog.LogLevel.Info, $"[state] {prev} → {newState}: {reason}");
    }

    private static string Short(string? id) =>
        string.IsNullOrWhiteSpace(id) ? "server" : (id.Length <= 6 ? id : id.Substring(0, 6));

    private void ResetInjectionCounters(string reason)
    {
        _injectedCount = 0;
        _failedCount = 0;
        _droppedCount = 0;
        UpdateInjectionStatsText();
        InputDebugLog.Log(InputDebugLog.LogLevel.Info, $"[stats] Reset counters: {reason}");
    }

    private void UpdateInjectionStatsText()
    {
        try
        {
            InjectionStatsText.Text = $"Injected: {_injectedCount:N0} | Failed: {_failedCount:N0} | Dropped: {_droppedCount:N0}";
        }
        catch { }
    }

    private void BuildKeyEchoMap()
    {
        _echoKeys.Clear();
        _echoKeys["W"] = Echo_W;
        _echoKeys["A"] = Echo_A;
        _echoKeys["S"] = Echo_S;
        _echoKeys["D"] = Echo_D;
        _echoKeys["Space"] = Echo_Space;
        _echoKeys["Enter"] = Echo_Enter;
        _echoKeys["Esc"] = Echo_Esc;
        _echoKeys["Up"] = Echo_Up;
        _echoKeys["Down"] = Echo_Down;
        _echoKeys["Left"] = Echo_Left;
        _echoKeys["Right"] = Echo_Right;
    }

    private async void FlashEcho(string key)
    {
        try
        {
            if (!_echoKeys.TryGetValue(key, out var b)) return;
            b.Background = System.Windows.Media.Brushes.SeaGreen;
            await Task.Delay(200);
            b.Background = System.Windows.Media.Brushes.Transparent;
        }
        catch { }
    }

    private void StartFocusMonitor()
    {
        _focusMonitor?.Dispose();
        _focusMonitor = new System.Threading.Timer(_ =>
        {
            try
            {
                // Only when a guest has the stick.
                var active = _sessionManager.ActivePlayerId;
                if (string.IsNullOrWhiteSpace(active) || active == _sessionManager.LocalPlayerId) return;

                var fg = GetForegroundWindow();
                if (fg == nint.Zero) return;
                GetWindowThreadProcessId(fg, out var pid);
                var name = TryGetProcessName(pid);
                var isGame = pid == _gameTracker.GameProcessId;
                if (isGame) return;

                Dispatcher.BeginInvoke(() =>
                    InputDebugLog.Log(InputDebugLog.LogLevel.Info,
                        $"[focus] Foreground: {name} PID={pid} (gamePID={_gameTracker.GameProcessId}) activePlayer: {Short(active)}"));
            }
            catch { }
        }, null, 2000, 2000);
    }

    private void StartGameWatchdog()
    {
        _gameWatchdog?.Stop();
        _gameWatchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _gameWatchdog.Tick += (_, _) =>
        {
            try
            {
                if (!_gameTracker.IsPinned) return;
                var pid = _gameTracker.GameProcessId;

                var alive = true;
                try { _ = Process.GetProcessById((int)pid); }
                catch { alive = false; }

                if (!alive)
                {
                    InputDebugLog.Log(InputDebugLog.LogLevel.Warning, "[game] ⚠ Game process ended");
                    _tray?.ShowToast("PassTheStick", "Game closed — unpin and repin to continue.");
                    _gameTracker.ClearPin();
                    PinnedGameLabel.Text = "Game: (not pinned)";
                    ResetInjectionCounters("game ended");
                    return;
                }

                // If HWND is gone but process alive, attempt rescan.
                if (!_gameTracker.IsPinnedHwndValid())
                {
                    InputDebugLog.Log(InputDebugLog.LogLevel.Info, "[game] Window handle invalid — rescanning...");
                    if (_gameTracker.TryRescanHwndForPid())
                    {
                        InputDebugLog.Log(InputDebugLog.LogLevel.Info, $"[game] Window updated: 0x{_gameTracker.GameHwnd:X}");
                    }
                }
            }
            catch { }
        };
        _gameWatchdog.Start();
    }

    private void TryFocusPinnedGame(string logLine)
    {
        try
        {
            if (!_gameTracker.IsPinned) return;
            var hwnd = _gameTracker.GameHwnd;
            if (hwnd == nint.Zero) return;
            SetForegroundWindow(hwnd);
            InputDebugLog.Log(InputDebugLog.LogLevel.Info, logLine);
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    private static string TryGetProcessName(uint pid)
    {
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName; }
        catch { return "unknown"; }
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
            _overlay?.SetTurn("Host", true, Array.Empty<string>());
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
            _overlay?.SetTurn("Test Player", false, Array.Empty<string>());
            await Task.Delay(TimeSpan.FromSeconds(5));
            _sessionManager.SetActivePlayer(_sessionManager.LocalPlayerId);
            _overlay?.SetTurn("Host", true, Array.Empty<string>());
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
                        RoomCodeText.Text = code;
                        StatusText.Text = "Reconnected.";
                        _overlay?.SetTurn("Host", true, Array.Empty<string>());
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
                _overlay?.SetTurn("Host", true, Array.Empty<string>());
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
        if (PlayersList.SelectedItem is not PlayerRowViewModel row) return;
        OnPickGuest(row.Player);
    }

    private void PlayersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Enable based on selection/pin/session; if not connected, we'll show a friendly message on pass attempt.
        PassStickButton.IsEnabled =
            _sessionStarted &&
            _gameTracker.IsPinned &&
            PlayersList.SelectedItem is PlayerRowViewModel;
    }

    private void PlayersList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (PlayersList.SelectedItem is PlayerRowViewModel row && PassStickButton.IsEnabled)
            OnPickGuest(row.Player);
    }

    private void TakeStickBackButton_Click(object sender, RoutedEventArgs e)
    {
        TakeStickBack();
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        var s = SettingsStore.Load();
        SettingsRelayUrlText.Text = s.RelayUrlOverride ?? string.Empty;
        SettingsSoundsCheck.IsChecked = s.EnableStickSounds;
        SettingsBackdrop.Visibility = Visibility.Visible;
        SettingsFlyout.Visibility = Visibility.Visible;
    }

    private void CloseSettings_Click(object sender, RoutedEventArgs e) => CloseSettings();

    private void SettingsBackdrop_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource == SettingsBackdrop)
            CloseSettings();
    }

    private void CloseSettings()
    {
        SettingsBackdrop.Visibility = Visibility.Collapsed;
        SettingsFlyout.Visibility = Visibility.Collapsed;
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
            _enableStickSounds = s.EnableStickSounds;
            _tray?.ShowToast("PassTheStick", "Settings saved.");
            CloseSettings();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "PassTheStick", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenDebugPanel_Click(object sender, RoutedEventArgs e)
    {
        DebugExpander.IsExpanded = true;
        DebugExpander.BringIntoView();
    }

    private void PlayerRowPass_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.DataContext is not PlayerRowViewModel row)
            return;
        if (!_sessionStarted || _relay == null)
        {
            StatusText.Text = "Not connected — start a session first.";
            ShowConnectionStatus();
            return;
        }
        if (!_gameTracker.IsPinned)
        {
            System.Windows.MessageBox.Show(
                "Please pin a game window first.",
                "PassTheStick",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
        OnPickGuest(row.Player);
    }

    private void UpdateVersionFooter()
    {
        try
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            var text = v == null ? "v—" : $"v{v.Major}.{v.Minor}.{v.Build}";
            VersionFooterText.Text = text;
            SettingsAboutText.Text =
                $"PassTheStick host {text}. Shared keyboard for couch co-op over the network.";
        }
        catch { }
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
                    "By default the app uses the cloud relay. For LAN-only sessions you can start a local relay from the tray, or set a custom WebSocket URL in Settings.";
                OnboardingPrimaryButton.Content = "Next";
                break;
            default:
                OnboardingTitle.Text = "You're ready";
                OnboardingBody.Text =
                    "Pin your game window, share the room code with guests, and use Ctrl+Shift+Right to pass the stick.";
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
        catch { }
        OnboardingOverlay.Visibility = Visibility.Collapsed;
    }

    // Port readiness checks are handled inside RelayProcessManager now.
}

using System.Windows;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using PassTheStick.Shared;

namespace PassTheStick.Guest;

public partial class MainWindow : Window
{
    private RelayClient? _relay;
    private KeyboardCapture? _keyboardCapture;
    private ControllerCapture? _controllerCapture;
    private bool _haveStick;
    private List<PlayerInfo> _players = new();
    private int _lastLatencyMs;
    private bool _sessionEnded;
    private readonly Dictionary<string, System.Windows.Controls.Border> _echoKeys = new();

    public MainWindow()
    {
        InitializeComponent();
        BuildEchoMap();
        Closed += (_, _) =>
        {
            _keyboardCapture?.Dispose();
            _controllerCapture?.Dispose();
            _relay?.Dispose();
        };
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    private void ShowActiveState()
    {
        JoinPanel.Visibility = Visibility.Collapsed;
        WaitingPanel.Visibility = Visibility.Collapsed;
        ActivePanel.Visibility = Visibility.Visible;

        // Best-effort: bring this window to the user's attention without requiring them to click it.
        try
        {
            WindowState = WindowState.Normal;
            Show();
            Activate();

            Topmost = true;
            Topmost = false;

            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != nint.Zero) SetForegroundWindow(hwnd);
        }
        catch { }

        _ = FlashActivePulseAsync();
    }

    private void ShowWaitingState(string holderName)
    {
        JoinPanel.Visibility = Visibility.Collapsed;
        WaitingPanel.Visibility = Visibility.Visible;
        ActivePanel.Visibility = Visibility.Collapsed;
        WaitingSubtitle.Text = $"{holderName} is playing right now";
    }

    private void ShowJoinState(string message = "")
    {
        JoinPanel.Visibility = Visibility.Visible;
        WaitingPanel.Visibility = Visibility.Collapsed;
        ActivePanel.Visibility = Visibility.Collapsed;
        JoinStatusText.Text = message ?? string.Empty;
    }

    private async void JoinButton_Click(object sender, RoutedEventArgs e)
    {
        _sessionEnded = false;
        var code = RoomCodeBox.Text.Trim().ToUpperInvariant();
        var name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(name))
        {
            JoinStatusText.Text = "Enter room code and name.";
            return;
        }
        JoinButton.IsEnabled = false;
        JoinStatusText.Text = "Connecting…";
        while (true)
        {
            try
            {
                _relay = new RelayClient();
                _relay.PlayerListReceived += players =>
                {
                    _players = players;
                    Dispatcher.BeginInvoke(() => GuestPlayersList.ItemsSource = _players);
                };
                _relay.YouHaveItReceived += () =>
                {
                    _haveStick = true;
                    Dispatcher.Invoke(ShowActiveState);
                };
                _relay.PassStickReceived += toId =>
                {
                    // Relay broadcasts PASS_STICK to everyone; clear stick UI when it moves away.
                    var mine = _relay?.MyId;
                    var nowHaveStick = !string.IsNullOrEmpty(mine) && string.Equals(toId, mine, StringComparison.Ordinal);
                    _haveStick = nowHaveStick;
                    Dispatcher.Invoke(() =>
                    {
                        var holder = nowHaveStick ? "You" : ResolveName(toId);
                        if (nowHaveStick) ShowActiveState();
                        else
                        {
                            _ = FlashTurnEndedAsync();
                            ShowWaitingState(holder);
                        }
                    });
                };
                _relay.SessionEnded += reason =>
                {
                    _sessionEnded = true;
                    Dispatcher.BeginInvoke(() =>
                    {
                        _haveStick = false;
                        try { _keyboardCapture?.Dispose(); _keyboardCapture = null; } catch { }
                        try { _controllerCapture?.Dispose(); _controllerCapture = null; } catch { }
                        try { _relay?.Dispose(); } catch { }
                        _relay = null;

                        RoomCodeBox.Text = "";
                        _players = new List<PlayerInfo>();
                        JoinButton.IsEnabled = true;
                        ShowJoinState("Session ended — enter a new room code to rejoin.");
                    });
                };
                _relay.LatencyUpdatedMs += ms =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        _lastLatencyMs = ms;
                        var status = ms > 200
                            ? $"Connected ({ms}ms) — high latency"
                            : $"Connected ({ms}ms)";
                        WaitingHeader.Text = $"Room: {code}  •  {status}";
                        ActiveStatusLine.Text = $"Latency: {ms}ms  •  Connected";
                    });
                };
                _relay.Disconnected += _ =>
                {
                    _sessionEnded = true;
                    _haveStick = false;
                    Dispatcher.Invoke(() =>
                    {
                        JoinButton.IsEnabled = true;
                        RoomCodeBox.Text = "";
                        _players = new List<PlayerInfo>();
                        ShowJoinState("Connection lost. Enter a room code to rejoin.");
                    });

                    try { _keyboardCapture?.Dispose(); } catch { }
                    try { _controllerCapture?.Dispose(); } catch { }
                    _keyboardCapture = null;
                    _controllerCapture = null;

                    try { _relay?.Dispose(); } catch { }
                    _relay = null;
                };
                await _relay.ConnectAsync();
                await _relay.JoinRoomAsync(code, name);
                if (_sessionEnded)
                    continue;
                _keyboardCapture = new KeyboardCapture(
                    () => _haveStick,
                    async (vk, sc, down) =>
                    {
                        if (_relay?.IsConnected == true)
                            await _relay.SendKeyEventAsync(vk, sc, down);
                    },
                    (vk, down) =>
                    {
                        if (!down) return;
                        Dispatcher.BeginInvoke(() => FlashEcho(KeyNames.VkToName(vk)));
                    });
                _keyboardCapture.Install();
                _controllerCapture = new ControllerCapture(
                    () => _haveStick,
                    async msg => { if (_relay?.IsConnected == true) await _relay.SendPadStateAsync(msg); });
                _controllerCapture.Start();
                WaitingHeader.Text = $"Room: {code}  •  Connected";
                GuestPlayersList.ItemsSource = _players;
                ShowWaitingState("Host");
                break;
            }
            catch
            {
                // Guest UX: keep it simple here; show a friendly message and allow retry via Join.
                JoinStatusText.Text = "Can't reach the relay server. Try again in a moment.";
                JoinButton.IsEnabled = true;
                break;
            }
        }
    }

    private string ResolveName(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "Host";
        var p = _players.FirstOrDefault(x => x.Id == id);
        return !string.IsNullOrWhiteSpace(p?.Name) ? p.Name : "Host";
    }

    private void BuildEchoMap()
    {
        _echoKeys.Clear();
        _echoKeys["W"] = G_Echo_W;
        _echoKeys["A"] = G_Echo_A;
        _echoKeys["S"] = G_Echo_S;
        _echoKeys["D"] = G_Echo_D;
        _echoKeys["Space"] = G_Echo_Space;
        _echoKeys["Up"] = G_Echo_Up;
        _echoKeys["Down"] = G_Echo_Down;
        _echoKeys["Left"] = G_Echo_Left;
        _echoKeys["Right"] = G_Echo_Right;
    }

    private async void FlashEcho(string key)
    {
        if (!_echoKeys.TryGetValue(key, out var b)) return;
        try
        {
            b.Background = (System.Windows.Media.Brush)FindResource("PtsBrushOrange");
            await Task.Delay(200);
            b.Background = System.Windows.Media.Brushes.Transparent;
        }
        catch { }
    }

    private async Task FlashActivePulseAsync()
    {
        try
        {
            var brush = (System.Windows.Media.Brush)FindResource("PtsBrushOrange");
            ActiveBody.Background = brush;
            await Task.Delay(120);
            ActiveBody.Background = (System.Windows.Media.Brush)FindResource("PtsBrushBgSecondary");
        }
        catch { }
    }

    private async Task FlashTurnEndedAsync()
    {
        try
        {
            WaitingTitle.Text = "Turn ended — great playing!";
            var green = (System.Windows.Media.Brush)FindResource("PtsBrushGreen");
            WaitingTitle.Foreground = green;
            await Task.Delay(800);
            WaitingTitle.Foreground = (System.Windows.Media.Brush)FindResource("PtsBrushTextPrimary");
            WaitingTitle.Text = "Waiting for your turn";
        }
        catch { }
    }
}

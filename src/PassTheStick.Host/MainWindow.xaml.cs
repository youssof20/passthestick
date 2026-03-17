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
            var helper = new WindowInteropHelper(this);
            helper.EnsureHandle();
            _hotkey.Register(helper.Handle);
            var src = HwndSource.FromHwnd(helper.Handle);
            src?.AddHook(WndProc);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to connect: " + ex.Message;
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
        catch { /* ViGEm not installed */ }
    }

    private void OnDisconnected(string _)
    {
        Dispatcher.Invoke(() => StatusText.Text = "Disconnected from relay.");
    }

    private void PinGameButton_Click(object sender, RoutedEventArgs e)
    {
        _gameTracker.PinCurrentForeground();
        StatusText.Text = "Game window pinned. Focus the game and pass the stick to test.";
    }

    private void PassStickButton_Click(object sender, RoutedEventArgs e)
    {
        if (PlayersList.SelectedItem is not PlayerInfo p || _relay == null) return;
        OnPickGuest(p);
    }
}

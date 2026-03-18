using System.Windows;
using PassTheStick.Shared;

namespace PassTheStick.Guest;

public partial class MainWindow : Window
{
    private RelayClient? _relay;
    private KeyboardCapture? _keyboardCapture;
    private ControllerCapture? _controllerCapture;
    private bool _haveStick;

    public MainWindow()
    {
        InitializeComponent();
        Closed += (_, _) =>
        {
            _keyboardCapture?.Dispose();
            _controllerCapture?.Dispose();
            _relay?.Dispose();
        };
    }

    private async void JoinButton_Click(object sender, RoutedEventArgs e)
    {
        var code = RoomCodeBox.Text.Trim().ToUpperInvariant();
        var name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(name))
        {
            StatusText.Text = "Enter room code and name.";
            return;
        }
        JoinButton.IsEnabled = false;
        StatusText.Text = "Connecting…";
        while (true)
        {
            try
            {
                _relay = new RelayClient();
                _relay.YouHaveItReceived += () =>
                {
                    _haveStick = true;
                    Dispatcher.Invoke(() => StatusText.Text = "You have the stick!");
                };
                _relay.PassStickReceived += toId =>
                {
                    // Relay broadcasts PASS_STICK to everyone; clear stick UI when it moves away.
                    var mine = _relay?.MyId;
                    var nowHaveStick = !string.IsNullOrEmpty(mine) && string.Equals(toId, mine, StringComparison.Ordinal);
                    _haveStick = nowHaveStick;
                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = nowHaveStick
                            ? "You have the stick!"
                            : "Joined. Wait for the host to pass you the stick.";
                    });
                };
                _relay.Disconnected += _ =>
                {
                    _haveStick = false;
                    Dispatcher.Invoke(() => { StatusText.Text = "Connection lost."; JoinButton.IsEnabled = true; });
                };
                await _relay.ConnectAsync();
                await _relay.JoinRoomAsync(code, name);
                _keyboardCapture = new KeyboardCapture(
                    () => _haveStick,
                    async (vk, sc, down) =>
                    {
                        if (_relay?.IsConnected == true)
                            await _relay.SendKeyEventAsync(vk, sc, down);
                    });
                _keyboardCapture.Install();
                _controllerCapture = new ControllerCapture(
                    () => _haveStick,
                    async msg => { if (_relay?.IsConnected == true) await _relay.SendPadStateAsync(msg); });
                _controllerCapture.Start();
                StatusText.Text = "Joined. Wait for the host to pass you the stick.";
                break;
            }
            catch
            {
                var dlg = new RelayConnectionDialog { Owner = this };
                dlg.ShowDialog();
                if (dlg.ShouldChangeUrl)
                {
                    var input = Microsoft.VisualBasic.Interaction.InputBox(
                        "Enter relay URL (ws://... or wss://...).",
                        "PassTheStick",
                        Constants.RelayWebSocketUrl);
                    if (!string.IsNullOrWhiteSpace(input))
                    {
                        var s = SettingsStore.Load();
                        s.RelayUrlOverride = input.Trim();
                        SettingsStore.Save(s);
                    }
                }

                if (!dlg.ShouldRetry && !dlg.ShouldChangeUrl)
                {
                    StatusText.Text = "Can't reach the relay server.";
                    JoinButton.IsEnabled = true;
                    break;
                }
            }
        }
    }
}

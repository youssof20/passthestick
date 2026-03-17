using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using PassTheStick.Shared;

namespace PassTheStick.Host;

/// <summary>
/// Virtual Xbox 360 controller via ViGEm. Feeds PAD_STATE from relay into the virtual device.
/// </summary>
public sealed class ViGEmControllerInjector : IDisposable
{
    private ViGEmClient? _client;
    private IXbox360Controller? _target;

    public void EnsureConnected()
    {
        if (_target != null) return;
        _client = new ViGEmClient();
        _target = _client.CreateXbox360Controller();
        _target.Connect();
    }

    public void FeedReport(PadStateMessage msg)
    {
        if (_target == null) return;
        int btns = msg.Btns;
        for (int i = 0; i < _target.ButtonCount; i++)
            _target.SetButtonState(i, (btns & (1 << i)) != 0);
        _target.SetAxisValue(0, msg.Lx);
        _target.SetAxisValue(1, msg.Ly);
        _target.SetAxisValue(2, msg.Rx);
        _target.SetAxisValue(3, msg.Ry);
        _target.SetSliderValue(0, msg.Lt);
        _target.SetSliderValue(1, msg.Rt);
        _target.SubmitReport();
    }

    public void Dispose()
    {
        _target?.Disconnect();
        _client?.Dispose();
    }
}

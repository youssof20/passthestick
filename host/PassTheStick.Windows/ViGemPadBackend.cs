using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Exceptions;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using PassTheStick.Platform;
using PassTheStick.Protocol;

namespace PassTheStick.Windows;

/// <summary>One virtual Xbox 360 pad per guest slot, created on first use and removed on dispose.</summary>
public sealed class ViGemPadBackend : IVirtualPadBackend, IDisposable
{
    private readonly ViGEmClient _client;
    private readonly Dictionary<int, IXbox360Controller> _pads = [];
    private readonly Lock _gate = new();

    private ViGemPadBackend(ViGEmClient client) => _client = client;

    /// <summary>Raised with slot, large motor and small motor (0-255) when the game rumbles a pad.</summary>
    public event Action<int, byte, byte>? Rumble;

    /// <summary>Returns null with a plain-language reason when ViGEmBus is not installed or cannot be opened.</summary>
    public static ViGemPadBackend? TryCreate(out string? problem)
    {
        try
        {
            problem = null;
            return new ViGemPadBackend(new ViGEmClient());
        }
        catch (VigemBusNotFoundException)
        {
            problem = "ViGEmBus is not installed. Install it to give guests virtual controllers. Keyboard sessions do not need it.";
        }
        catch (VigemBusAccessFailedException)
        {
            problem = "ViGEmBus is installed but PassTheStick cannot open it. Restart the PC if the driver was just installed.";
        }
        catch (VigemBusVersionMismatchException)
        {
            problem = "The installed ViGEmBus version does not match this app. Install the latest ViGEmBus release.";
        }

        return null;
    }

    public void Submit(int slot, in PadState state)
    {
        lock (_gate)
        {
            var pad = PadFor(slot);
            foreach (var (flag, button) in XInputMap.Buttons)
            {
                pad.SetButtonState(button, (state.Buttons & flag) != 0);
            }

            pad.SetAxisValue(Xbox360Axis.LeftThumbX, state.Lx);
            pad.SetAxisValue(Xbox360Axis.LeftThumbY, state.Ly);
            pad.SetAxisValue(Xbox360Axis.RightThumbX, state.Rx);
            pad.SetAxisValue(Xbox360Axis.RightThumbY, state.Ry);
            pad.SetSliderValue(Xbox360Slider.LeftTrigger, state.Lt);
            pad.SetSliderValue(Xbox360Slider.RightTrigger, state.Rt);
            pad.SubmitReport();
        }
    }

    public void Neutralize(int slot) => Submit(slot, PadState.Neutral);

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var pad in _pads.Values)
            {
                pad.Disconnect();
            }

            _pads.Clear();
        }

        _client.Dispose();
    }

    private IXbox360Controller PadFor(int slot)
    {
        if (_pads.TryGetValue(slot, out var existing))
        {
            return existing;
        }

        var pad = _client.CreateXbox360Controller();
        pad.AutoSubmitReport = false;
        pad.FeedbackReceived += (_, e) => Rumble?.Invoke(slot, e.LargeMotor, e.SmallMotor);
        pad.Connect();
        _pads[slot] = pad;
        return pad;
    }
}

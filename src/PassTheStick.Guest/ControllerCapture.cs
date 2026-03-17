using System.Diagnostics;
using PassTheStick.Shared;
using SharpDX.XInput;

namespace PassTheStick.Guest;

/// <summary>
/// Polls XInput controller at 60 Hz. Sends PAD_STATE only when state differs from the previous frame (avoids spamming the relay).
/// </summary>
public sealed class ControllerCapture : IDisposable
{
    private const int PollIntervalMs = 1000 / 60; // 60 Hz
    private readonly Func<bool> _hasStick;
    private readonly Func<PadStateMessage, Task> _sendPadState;
    private readonly CancellationTokenSource _cts = new();
    private Task? _pollTask;
    private Controller? _controller;
    private State _prevState;

    public ControllerCapture(Func<bool> hasStick, Func<PadStateMessage, Task> sendPadState)
    {
        _hasStick = hasStick;
        _sendPadState = sendPadState;
    }

    public void Start()
    {
        _controller = new Controller(UserIndex.One);
        if (!_controller.IsConnected)
            _controller = new Controller(UserIndex.Two);
        if (!_controller.IsConnected)
            _controller = new Controller(UserIndex.Three);
        if (!_controller.IsConnected)
            _controller = new Controller(UserIndex.Four);
        _pollTask = PollLoopAsync();
    }

    private async Task PollLoopAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
            if (_controller?.IsConnected == true && _hasStick())
            {
                _controller.GetState(out State state);
                var msg = ToPadStateMessage(state);
                if (!Equals(_prevState, state))
                {
                    _prevState = state;
                    await _sendPadState(msg);
                }
            }
            }
            catch { /* controller disconnected */ }
            await Task.Delay(PollIntervalMs, _cts.Token);
        }
    }

    private static PadStateMessage ToPadStateMessage(State s)
    {
        var g = s.Gamepad;
        return new PadStateMessage(
            "PAD_STATE",
            (int)g.Buttons,
            g.LeftThumbX,
            g.LeftThumbY,
            g.RightThumbX,
            g.RightThumbY,
            g.LeftTrigger,
            g.RightTrigger);
    }

    private static bool Equals(State a, State b)
    {
        var ga = a.Gamepad;
        var gb = b.Gamepad;
        return ga.Buttons == gb.Buttons
               && ga.LeftThumbX == gb.LeftThumbX && ga.LeftThumbY == gb.LeftThumbY
               && ga.RightThumbX == gb.RightThumbX && ga.RightThumbY == gb.RightThumbY
               && ga.LeftTrigger == gb.LeftTrigger && ga.RightTrigger == gb.RightTrigger;
    }

    public void Dispose()
    {
        _cts.Cancel();
    }
}

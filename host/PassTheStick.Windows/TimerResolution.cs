namespace PassTheStick.Windows;

/// <summary>Asks Windows for a 1 ms timer tick while alive, so a 5 ms input loop really runs every 5 ms instead of every 15.6 ms.</summary>
public sealed class TimerResolution : IDisposable
{
    private bool _active;

    public TimerResolution() => _active = NativeMethods.timeBeginPeriod(1) == 0;

    public void Dispose()
    {
        if (_active)
        {
            _ = NativeMethods.timeEndPeriod(1);
            _active = false;
        }
    }
}

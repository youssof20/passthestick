using System.Runtime.InteropServices;

namespace PassTheStick.Host;

/// <summary>
/// Tracks the pinned game window. Used to scope injection and suppression to the game only.
/// </summary>
public sealed class GameWindowTracker
{
    private nint _gameHwnd;
    private uint _gameProcessId;

    public bool IsPinned => _gameHwnd != nint.Zero && _gameProcessId != 0 && IsWindow(_gameHwnd);

    public uint GameProcessId
    {
        get => _gameProcessId;
        set => _gameProcessId = value;
    }

    public nint GameHwnd => _gameHwnd;

    /// <summary>Pin a specific window as the game target.</summary>
    public void PinWindow(nint hwnd)
    {
        if (hwnd == nint.Zero || !IsWindow(hwnd))
            throw new InvalidOperationException("Selected window is no longer available.");

        GetWindowThreadProcessId(hwnd, out uint pid);
        _gameHwnd = hwnd;
        _gameProcessId = pid;
    }

    /// <summary>Set the current foreground window as the game target.</summary>
    public void PinCurrentForeground()
    {
        var fg = GetForegroundWindow();
        PinWindow(fg);
    }

    /// <summary>True if the pinned game window is currently foreground.</summary>
    public bool IsGameForeground()
    {
        if (!IsPinned) return false;
        var fg = GetForegroundWindow();
        return fg == _gameHwnd;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);
}

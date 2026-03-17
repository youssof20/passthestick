using System.Runtime.InteropServices;

namespace PassTheStick.Host;

/// <summary>
/// Tracks the game window: foreground HWND/PID. Only inject when game is foreground.
/// </summary>
public sealed class GameWindowTracker
{
    private uint _gameProcessId;

    public uint GameProcessId
    {
        get => _gameProcessId;
        set => _gameProcessId = value;
    }

    /// <summary>Set the current foreground window as the game target.</summary>
    public void PinCurrentForeground()
    {
        var fg = GetForegroundWindow();
        GetWindowThreadProcessId(fg, out uint pid);
        _gameProcessId = pid;
    }

    /// <summary>True if the foreground window belongs to the pinned game process.</summary>
    public bool IsGameForeground()
    {
        if (_gameProcessId == 0) return true; // no game pinned, allow inject anyway for testing
        var fg = GetForegroundWindow();
        GetWindowThreadProcessId(fg, out uint fgPid);
        return fgPid == _gameProcessId;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
}

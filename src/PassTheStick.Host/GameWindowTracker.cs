using System.Diagnostics;
using System.Runtime.InteropServices;
using PassTheStick.Shared;

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
        if (pid == 0)
            throw new InvalidOperationException("Selected window is not associated with a process.");

        // Validate process still exists.
        try { _ = Process.GetProcessById((int)pid); }
        catch { throw new InvalidOperationException("Selected game process is no longer running."); }

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
        GetWindowThreadProcessId(fg, out var fgPid);

        var matchPid = fgPid == _gameProcessId;
        if (InputDebugLog.Enabled)
            InputDebugLog.Log($"Foreground PID={fgPid} GamePID={_gameProcessId} match={matchPid}");

        return fg == _gameHwnd;
    }

    public bool IsPinnedProcessElevated()
    {
        if (!IsPinned) return false;
        try
        {
            using var proc = Process.GetProcessById((int)_gameProcessId);
            return IsProcessElevated(proc.Handle);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr TokenHandle,
        int TokenInformationClass,
        out TOKEN_ELEVATION TokenInformation,
        int TokenInformationLength,
        out int ReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevation = 20;

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_ELEVATION
    {
        public int TokenIsElevated;
    }

    private static bool IsProcessElevated(IntPtr processHandle)
    {
        IntPtr tokenHandle = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(processHandle, TOKEN_QUERY, out tokenHandle))
                return false;

            var elevation = new TOKEN_ELEVATION();
            var returnedLength = 0;
            if (!GetTokenInformation(tokenHandle, TokenElevation, out elevation, Marshal.SizeOf<TOKEN_ELEVATION>(), out returnedLength))
                return false;

            return elevation.TokenIsElevated != 0;
        }
        finally
        {
            if (tokenHandle != IntPtr.Zero)
                CloseHandle(tokenHandle);
        }
    }
}

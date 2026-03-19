using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using PassTheStick.Shared;

namespace PassTheStick.Host;

/// <summary>
/// Tracks the pinned game window. Used to scope injection and suppression to the game only.
/// </summary>
public sealed class GameWindowTracker
{
    private nint _gameHwnd;
    private uint _gameProcessId;
    private nint _lastKnownFgHwnd = nint.Zero;
    private uint _lastKnownFgPid;
    private string _lastKnownFgName = "";
    private DateTime _lastKnownFgTimeUtc = DateTime.MinValue;
    private const int FG_CACHE_MS = 500;

    public bool IsPinned => _gameHwnd != nint.Zero && _gameProcessId != 0 && IsWindow(_gameHwnd);

    public uint GameProcessId
    {
        get => _gameProcessId;
        set => _gameProcessId = value;
    }

    public nint GameHwnd => _gameHwnd;

    public void ClearPin()
    {
        _gameHwnd = nint.Zero;
        _gameProcessId = 0;
    }

    public bool IsPinnedHwndValid() => _gameHwnd != nint.Zero && IsWindow(_gameHwnd);

    public bool TryRescanHwndForPid()
    {
        if (_gameProcessId == 0) return false;
        nint found = nint.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (hwnd == nint.Zero) return true;
            if (!IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != _gameProcessId) return true;
            found = hwnd;
            return false;
        }, nint.Zero);

        if (found == nint.Zero) return false;
        _gameHwnd = found;
        return true;
    }

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

        if (fg != nint.Zero)
        {
            GetWindowThreadProcessId(fg, out uint pid);
            var name = GetProcessName(pid);
            _lastKnownFgHwnd = fg;
            _lastKnownFgPid = pid;
            _lastKnownFgName = name;
            _lastKnownFgTimeUtc = DateTime.UtcNow;

            var hwndMatch = fg == _gameHwnd;
            var pidMatch = pid == _gameProcessId;
            var result = hwndMatch || pidMatch;

            InputDebugLog.Log(
                $"[fg] {(result ? "GAME" : "other")} fg=0x{fg:X} ({name} PID={pid}) pinned=0x{_gameHwnd:X} (PID={_gameProcessId}) hwnd={hwndMatch} pid={pidMatch}");

            return result;
        }

        // NULL foreground — use cached state if fresh
        var ms = (DateTime.UtcNow - _lastKnownFgTimeUtc).TotalMilliseconds;
        var cached = _lastKnownFgPid != 0 && ms < FG_CACHE_MS;
        var cachedResult = cached && (_lastKnownFgPid == _gameProcessId || _lastKnownFgHwnd == _gameHwnd);

        InputDebugLog.Log(
            $"[fg] NULL foreground — cache={cached} age={ms:F0}ms last={_lastKnownFgName} PID={_lastKnownFgPid} result={cachedResult}");

        return cachedResult;
    }

    /// <summary>Human-readable reason KEY_EVENT was not injected (debug).</summary>
    public string DescribeWhyNotForegroundForKeyEvent(string? fromId, int vk, bool down)
    {
        var fid = string.IsNullOrEmpty(fromId) ? "?" : fromId;
        if (!IsPinned)
            return $"KEY_EVENT dropped (vk={vk} down={down} fromId={fid}): no game window pinned — pin the game first.";

        var fg = GetForegroundWindow();
        uint fgPid = 0;
        var usedCached = false;
        if (fg != nint.Zero)
        {
            GetWindowThreadProcessId(fg, out fgPid);
            _lastKnownFgHwnd = fg;
            _lastKnownFgPid = fgPid;
            _lastKnownFgName = GetProcessName(fgPid);
            _lastKnownFgTimeUtc = DateTime.UtcNow;
        }
        else
        {
            var ageMs = (DateTime.UtcNow - _lastKnownFgTimeUtc).TotalMilliseconds;
            if (_lastKnownFgPid != 0 && ageMs < FG_CACHE_MS)
            {
                usedCached = true;
                fg = _lastKnownFgHwnd;
                fgPid = _lastKnownFgPid;
            }
        }
        var sameHwnd = fg == _gameHwnd;
        var samePid = fgPid == _gameProcessId;
        var fgName = TryGetProcessName(fgPid);
        var gameName = TryGetProcessName(_gameProcessId);
        return
            $"KEY_EVENT dropped (vk={vk} down={down} fromId={fid}): game not foreground. " +
            $"pinned HWND=0x{_gameHwnd:X} ({gameName} PID={_gameProcessId}); " +
            $"foreground HWND=0x{fg:X} ({fgName} PID={fgPid}){(usedCached ? " (cached)" : "")}; hwndMatch={sameHwnd} pidMatch={samePid}. " +
            "Click the game so it has focus (exclusive fullscreen can hide overlays; Alt+Tab to game).";
    }

    /// <summary>Human-readable reason PAD_STATE was not applied (debug).</summary>
    public string DescribeWhyNotForegroundForPadState(string? fromId)
    {
        var fid = string.IsNullOrEmpty(fromId) ? "?" : fromId;
        if (!IsPinned)
            return $"PAD_STATE dropped (fromId={fid}): no game window pinned.";

        var fg = GetForegroundWindow();
        uint fgPid = 0;
        var usedCached = false;
        if (fg != nint.Zero)
        {
            GetWindowThreadProcessId(fg, out fgPid);
            _lastKnownFgHwnd = fg;
            _lastKnownFgPid = fgPid;
            _lastKnownFgName = GetProcessName(fgPid);
            _lastKnownFgTimeUtc = DateTime.UtcNow;
        }
        else
        {
            var ageMs = (DateTime.UtcNow - _lastKnownFgTimeUtc).TotalMilliseconds;
            if (_lastKnownFgPid != 0 && ageMs < FG_CACHE_MS)
            {
                usedCached = true;
                fg = _lastKnownFgHwnd;
                fgPid = _lastKnownFgPid;
            }
        }
        var sameHwnd = fg == _gameHwnd;
        var fgName = TryGetProcessName(fgPid);
        var gameName = TryGetProcessName(_gameProcessId);
        return
            $"PAD_STATE dropped (fromId={fid}): game not foreground. " +
            $"pinned HWND=0x{_gameHwnd:X} ({gameName}); foreground HWND=0x{fg:X} ({fgName}){(usedCached ? " (cached)" : "")}; hwndMatch={sameHwnd}.";
    }

    public bool TryGetPinnedWindowRect(out Rect rect)
    {
        rect = default;
        if (!IsPinned) return false;
        if (!GetWindowRect(_gameHwnd, out var r)) return false;
        rect = new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        return rect.Width > 0 && rect.Height > 0;
    }

    public bool IsPinnedWindowMinimized()
    {
        if (!IsPinned) return false;
        return IsIconic(_gameHwnd);
    }

    private static string TryGetProcessName(uint pid)
    {
        if (pid == 0) return "(none)";
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName; }
        catch { return "unknown"; }
    }

    private string GetProcessName(uint pid)
    {
        // Instance wrapper to match the v0.1.18 debug prompt shape.
        return TryGetProcessName(pid);
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

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

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

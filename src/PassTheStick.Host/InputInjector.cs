using System.Runtime.InteropServices;
using System.Threading;
using PassTheStick.Shared;

namespace PassTheStick.Host;

/// <summary>
/// Injects keyboard input via SendInput with KEYEVENTF_SCANCODE (required for Raw Input / DirectInput games).
/// Phase 3 will add ViGEm controller injection.
/// </summary>
public static class InputInjector
{
    private const uint InputKeyboard = 1;
    private const uint KeyeventfScancode = 0x0008;
    private const uint KeyeventfKeyup = 0x0002;
    private const uint KeyeventfExtendedkey = 0x0001;
    private const uint MapvkVscToVk = 1;

    private static nint _lastDesktop = nint.Zero;

    private static void SyncThreadDesktop()
    {
        // Sunshine-style pattern: keep thread attached to the current input desktop.
        // This helps after UAC prompts / desktop switches.
        var desktop = OpenInputDesktop(0, false, DesktopSwitchDesktops);
        if (desktop == nint.Zero) return;

        if (desktop != _lastDesktop)
        {
            try
            {
                SetThreadDesktop(desktop);
                InputDebugLog.Log(InputDebugLog.LogLevel.Info, "[desktop] Thread desktop synced");
            }
            catch { }

            // Close previously cached handle.
            if (_lastDesktop != nint.Zero)
            {
                try { CloseDesktop(_lastDesktop); } catch { }
            }
            _lastDesktop = desktop;
        }
        else
        {
            // No change; close the new handle we opened.
            CloseDesktop(desktop);
        }
    }

    /// <summary>True for keys that require KEYEVENTF_EXTENDEDKEY when using scan codes.</summary>
    private static bool IsExtendedKey(ushort scanCode) =>
        scanCode is 0x48 or 0x50 or 0x4B or 0x4D // arrows
            or 0x49 or 0x51 or 0x47 or 0x4F // pgup, pgdn, home, end
            or 0x52 or 0x53 // ins, del
            or 0x1C or 0x35; // numpad enter, numpad div — some layouts / games

    public static bool InjectKeyWithResult(ushort scanCode, bool keyDown, out int win32Error, nint gameHwnd = default)
    {
        var dwFlags = keyDown ? KeyeventfScancode : (KeyeventfScancode | KeyeventfKeyup);
        if (IsExtendedKey(scanCode))
            dwFlags |= KeyeventfExtendedkey;

        InputDebugLog.Log(InputDebugLog.LogLevel.Verbose,
            $"[inject] Attempt sc={scanCode} down={keyDown} flags=0x{dwFlags:X4} gameHwnd=0x{gameHwnd:X}");

        var fgBefore = GetForegroundWindow();
        GetWindowThreadProcessId(fgBefore, out var fgPidBefore);

        var inputs = new INPUT[1];
        inputs[0].type = InputKeyboard;
        inputs[0].ki.wVk = 0;
        inputs[0].ki.wScan = scanCode;
        inputs[0].ki.dwFlags = dwFlags;
        inputs[0].ki.time = 0;
        inputs[0].ki.dwExtraInfo = IntPtr.Zero;

        SyncThreadDesktop();

        uint gameThread = 0;
        var ourThread = GetCurrentThreadId();
        var attached = false;
        if (gameHwnd != nint.Zero && IsWindow(gameHwnd))
        {
            gameThread = GetWindowThreadProcessId(gameHwnd, out _);
            if (gameThread != 0 && gameThread != ourThread)
                attached = AttachThreadInput(ourThread, gameThread, true);
        }

        uint result;
        try
        {
            result = SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
        }
        finally
        {
            if (attached)
                AttachThreadInput(ourThread, gameThread, false);
        }

        var err = Marshal.GetLastWin32Error();

        // Retry once after desktop sync on failure.
        if (result == 0 && err != 0)
        {
            SyncThreadDesktop();
            var retryAttached = false;
            try
            {
                if (gameHwnd != nint.Zero && IsWindow(gameHwnd))
                {
                    gameThread = GetWindowThreadProcessId(gameHwnd, out _);
                    if (gameThread != 0 && gameThread != ourThread)
                        retryAttached = AttachThreadInput(ourThread, gameThread, true);
                }

                result = SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
            }
            finally
            {
                if (retryAttached)
                    AttachThreadInput(ourThread, gameThread, false);
            }
            err = Marshal.GetLastWin32Error();
            InputDebugLog.Log(InputDebugLog.LogLevel.Verbose, $"[inject] Retry result={result} err={err}");
        }

        var fgAfter = GetForegroundWindow();
        GetWindowThreadProcessId(fgAfter, out var fgPidAfter);

        if (fgBefore != fgAfter)
        {
            InputDebugLog.Log(InputDebugLog.LogLevel.Warning,
                $"[inject] WARNING: foreground changed during inject: before=0x{fgBefore:X} pid={fgPidBefore} after=0x{fgAfter:X} pid={fgPidAfter} result={result}");
        }
        else
        {
            InputDebugLog.Log(InputDebugLog.LogLevel.Info,
                $"[inject] fg_at_inject=0x{fgAfter:X} pid={fgPidAfter} result={result}");
        }

        win32Error = err;
        if (result == 0)
        {
            var reason = err switch
            {
                5 => "Access denied — game may be running elevated",
                6 => "Invalid handle",
                87 => "Invalid parameter — INPUT struct malformed",
                1400 => "Invalid window handle",
                _ => $"Win32 error {err}"
            };
            InputDebugLog.Log(InputDebugLog.LogLevel.Warning,
                $"[inject] FAILED sc={scanCode} down={keyDown} err={err} ({reason})");
            return false;
        }

        InputDebugLog.Log(InputDebugLog.LogLevel.Info,
            $"[inject] OK sc={scanCode} down={keyDown}");

        if (keyDown && result > 0)
        {
            Thread.Sleep(2);
            var vk = MapVirtualKey(scanCode, MapvkVscToVk);
            if (vk != 0)
            {
                var st = GetAsyncKeyState((int)vk);
                var registered = (st & 0x8000) != 0;
                InputDebugLog.Log(registered ? InputDebugLog.LogLevel.Verbose : InputDebugLog.LogLevel.Warning,
                    $"[inject] Verification: vk=0x{vk:X} sc={scanCode} key registered in async state = {registered}");
            }
        }

        return true;
    }

    public static void InjectKey(ushort scanCode, bool keyDown, nint gameHwnd = default) =>
        _ = InjectKeyWithResult(scanCode, keyDown, out _, gameHwnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy, mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    // IMPORTANT: INPUT is a C union. Must use Explicit layout and FieldOffset(4).
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(4)] public KEYBDINPUT ki;
        [FieldOffset(4)] public MOUSEINPUT mi;
        [FieldOffset(4)] public HARDWAREINPUT hi;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private const uint DesktopSwitchDesktops = 0x0100;

    [DllImport("user32.dll")]
    private static extern nint OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll")]
    private static extern bool SetThreadDesktop(nint hDesktop);

    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(nint hDesktop);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);
}

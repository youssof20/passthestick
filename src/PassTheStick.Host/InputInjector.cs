using System.Runtime.InteropServices;
using PassTheStick.Shared;

namespace PassTheStick.Host;

/// <summary>
/// Injects keyboard input via SendInput with KEYEVENTF_SCANCODE (required for Raw Input / DirectInput games).
/// Phase 3 will add ViGEm controller injection.
/// </summary>
public static class InputInjector
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_SCANCODE = 0x0008;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

    /// <summary>
    /// Inject a key event by scan code. Use this (not VK) so modern games receive input.
    /// </summary>
    /// <param name="scanCode">Hardware scan code (e.g. 17 for W on QWERTY).</param>
    /// <param name="keyDown">True for key down, false for key up.</param>
    private static nint _lastDesktop = nint.Zero;

    private static void SyncThreadDesktop()
    {
        // Sunshine-style pattern: keep thread attached to the current input desktop.
        // This helps after UAC prompts / desktop switches.
        var desktop = OpenInputDesktop(0, false, DESKTOP_SWITCHDESKTOP);
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

    public static bool InjectKeyWithResult(ushort scanCode, bool keyDown, out int win32Error)
    {
        var dwFlags = keyDown ? KEYEVENTF_SCANCODE : (KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP);

        InputDebugLog.Log(InputDebugLog.LogLevel.Verbose,
            $"[inject] Attempt sc={scanCode} down={keyDown} flags=0x{dwFlags:X4}");

        var inputs = new INPUT[1];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].ki.wVk = 0;
        inputs[0].ki.wScan = scanCode;
        inputs[0].ki.dwFlags = dwFlags;
        inputs[0].ki.time = 0;
        inputs[0].ki.dwExtraInfo = IntPtr.Zero;

        // CRITICAL: must be Marshal.SizeOf(typeof(INPUT)) for correct marshalling.
        SyncThreadDesktop();
        var result = SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
        var err = Marshal.GetLastWin32Error();

        // Retry once after desktop sync on failure.
        if (result == 0 && err != 0)
        {
            SyncThreadDesktop();
            result = SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
            err = Marshal.GetLastWin32Error();
            InputDebugLog.Log(InputDebugLog.LogLevel.Verbose, $"[inject] Retry result={result} err={err}");
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
        return true;
    }

    public static void InjectKey(ushort scanCode, bool keyDown)
    {
        _ = InjectKeyWithResult(scanCode, keyDown, out _);
    }

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

    private const uint DESKTOP_SWITCHDESKTOP = 0x0100;

    [DllImport("user32.dll")]
    private static extern nint OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll")]
    private static extern bool SetThreadDesktop(nint hDesktop);

    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(nint hDesktop);
}

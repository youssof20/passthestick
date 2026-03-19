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
    public static void InjectKey(ushort scanCode, bool keyDown)
    {
        var dwFlags = keyDown ? KEYEVENTF_SCANCODE : (KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP);

        InputDebugLog.Log(
            $"Injecting: sc={scanCode} down={keyDown} flags=0x{dwFlags:X4}");

        var inputs = new INPUT[1];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].ki.wVk = 0;
        inputs[0].ki.wScan = scanCode;
        inputs[0].ki.dwFlags = dwFlags;
        inputs[0].ki.time = 0;
        inputs[0].ki.dwExtraInfo = IntPtr.Zero;

        // CRITICAL: must be Marshal.SizeOf(typeof(INPUT)) for correct marshalling.
        var result = SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
        if (result > 0)
        {
            InputDebugLog.Log($"SendInput result: {result} (success)");
        }
        else
        {
            var err = Marshal.GetLastWin32Error();
            InputDebugLog.Log($"SendInput result: {result} ERROR: {err}");

            if (err == 5)
                InputDebugLog.Log("SendInput FAILED: Access denied/elevation mismatch (error 5). Run PassTheStick as administrator.");
            else if (err == 6)
                InputDebugLog.Log("SendInput FAILED: Invalid handle (error 6). Game window handle may be invalid.");
            else if (err == 87)
                InputDebugLog.Log("SendInput FAILED: Invalid parameter (error 87). This indicates INPUT struct marshalling/layout mismatch.");
        }
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
}

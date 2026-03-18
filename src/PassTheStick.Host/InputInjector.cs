using System.Runtime.InteropServices;
using PassTheStick.Shared;

namespace PassTheStick.Host;

/// <summary>
/// Injects keyboard input via SendInput with KEYEVENTF_SCANCODE (required for Raw Input / DirectInput games).
/// Phase 3 will add ViGEm controller injection.
/// </summary>
public static class InputInjector
{
    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_SCANCODE = 0x0008;
    private const uint KEYEVENTF_KEYUP = 0x0002;

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

        var ki = new KEYBDINPUT
        {
            wScan = scanCode,
            dwFlags = dwFlags
        };
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = ki }
        };
        var inputs = new[] { input };

        var result = SendInput(1, inputs, Marshal.SizeOf<INPUT>());
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
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}

using System.Runtime.InteropServices;

namespace PassTheStick.Guest;

/// <summary>
/// Low-level keyboard hook on guest PC. Sends KEY_EVENT (vk + sc) to the relay when this guest has the stick.
/// </summary>
public sealed class KeyboardCapture : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private readonly Func<bool> _hasStick;
    private readonly Func<int, int, bool, Task> _sendKeyEvent;
    private nint _hookId = nint.Zero;
    private readonly LowLevelKeyboardProc _proc;

    public KeyboardCapture(Func<bool> hasStick, Func<int, int, bool, Task> sendKeyEvent)
    {
        _hasStick = hasStick;
        _sendKeyEvent = sendKeyEvent;
        _proc = Callback;
    }

    public void Install()
    {
        if (_hookId != nint.Zero) return;
        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        nint hMod = curModule != null ? GetModuleHandle(curModule.ModuleName) : nint.Zero;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, hMod, 0);
    }

    public void Uninstall()
    {
        if (_hookId == nint.Zero) return;
        UnhookWindowsHookEx(_hookId);
        _hookId = nint.Zero;
    }

    private nint Callback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && _hasStick())
        {
            var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool down = wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN;
            _ = _sendKeyEvent((int)kbd.vkCode, (int)kbd.scanCode, down);
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    private delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    public void Dispose() => Uninstall();
}

using System.Runtime.InteropServices;

namespace PassTheStick.Host;

/// <summary>
/// Installs and removes WH_KEYBOARD_LL. Suppresses keystrokes when the stick is not with the local host.
/// </summary>
public sealed class HookManager : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;

    private readonly SessionManager _sessionManager;
    private nint _hookId = nint.Zero;
    private readonly LowLevelKeyboardProc _keyboardHookProc; // keep delegate alive for GC

    public HookManager(SessionManager sessionManager)
    {
        _sessionManager = sessionManager;
        _keyboardHookProc = KeyboardHookCallback;
    }

    public void Install()
    {
        if (_hookId != nint.Zero) return;
        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        nint hMod = curModule != null ? GetModuleHandle(curModule.ModuleName) : nint.Zero;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardHookProc, hMod, 0);
        if (_hookId == nint.Zero)
            throw new InvalidOperationException("SetWindowsHookEx(WH_KEYBOARD_LL) failed. Run as administrator for some games.");
    }

    public void Uninstall()
    {
        if (_hookId == nint.Zero) return;
        UnhookWindowsHookEx(_hookId);
        _hookId = nint.Zero;
    }

    private nint KeyboardHookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            bool isHostActive = _sessionManager.IsLocalPlayerActive;
            var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool isInjected = (kbd.flags & LLKHF_INJECTED) != 0;

            if (!isInjected && !isHostActive)
                return (nint)1; // suppress
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

    private const uint LLKHF_INJECTED = 0x00000010;

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

using System.Diagnostics;
using System.Runtime.InteropServices;
using PassTheStick.Shared;

namespace PassTheStick.Host;

/// <summary>
/// Installs and removes WH_KEYBOARD_LL. Suppresses keystrokes when the stick is not with the local host.
/// </summary>
public sealed class HookManager : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;

    private readonly SessionManager _sessionManager;
    private readonly GameWindowTracker _gameWindowTracker;
    private nint _hookId = nint.Zero;
    private readonly LowLevelKeyboardProc _keyboardHookProc; // keep delegate alive for GC

    public HookManager(SessionManager sessionManager, GameWindowTracker gameWindowTracker)
    {
        _sessionManager = sessionManager;
        _gameWindowTracker = gameWindowTracker;
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

            // Never globally suppress the host keyboard: only block while the pinned game is foreground.
            if (!isInjected && !isHostActive)
            {
                var isGameForeground = _gameWindowTracker.IsGameForeground();
                if (isGameForeground)
                {
                    if (InputDebugLog.Enabled)
                    {
                        var active = string.IsNullOrEmpty(_sessionManager.ActivePlayerId)
                            ? "host"
                            : _sessionManager.ActivePlayerId;
                        InputDebugLog.Log(
                            $"SUPPRESSED local key vk={kbd.vkCode} sc={kbd.scanCode} wParam={wParam} (guest has stick; activePlayerId={active})");
                    }
                    return (nint)1; // suppress
                }

                if (InputDebugLog.Enabled)
                {
                    var fgHwnd = GetForegroundWindow();
                    GetWindowThreadProcessId(fgHwnd, out var fgPid);
                    var foregroundName = TryGetProcessName(fgPid);
                    var gameName = TryGetProcessName(_gameWindowTracker.GameProcessId);
                    var active = string.IsNullOrEmpty(_sessionManager.ActivePlayerId)
                        ? "host"
                        : _sessionManager.ActivePlayerId;
                    InputDebugLog.Log(
                        $"SKIPPED local suppress — game not foreground (foreground: {foregroundName}, game: {gameName}; activePlayerId={active}; vk={kbd.vkCode})");
                }
            }
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

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    private static string TryGetProcessName(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            return "unknown";
        }
    }

    public void Dispose() => Uninstall();
}

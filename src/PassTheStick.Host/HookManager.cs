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
    private int _hostMashCount;
    private DateTime _firstHostMashUtc = DateTime.MinValue;

    public event Action? OverrideRequested;

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

    public bool IsInstalled => _hookId != nint.Zero;

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
                    var active = string.IsNullOrEmpty(_sessionManager.ActivePlayerId) ? "host" : _sessionManager.ActivePlayerId;
                    InputDebugLog.Log(InputDebugLog.LogLevel.Verbose,
                        $"[hook] SUPPRESSED {KeyNames.VkToName((int)kbd.vkCode)} vk={kbd.vkCode} sc={kbd.scanCode} injected={isInjected} hostActive={isHostActive} gameFg={isGameForeground} activePlayer={active}");

                    // Parsec-style safety valve: host mashes 3+ keys within 1s -> reclaim stick.
                    var now = DateTime.UtcNow;
                    if ((now - _firstHostMashUtc).TotalSeconds > 1)
                    {
                        _hostMashCount = 0;
                        _firstHostMashUtc = now;
                    }
                    _hostMashCount++;
                    if (_hostMashCount >= 3)
                    {
                        _hostMashCount = 0;
                        OverrideRequested?.Invoke();
                    }
                    return (nint)1; // suppress
                }

                var fgHwnd = GetForegroundWindow();
                GetWindowThreadProcessId(fgHwnd, out var fgPid);
                var foregroundName = TryGetProcessName(fgPid);
                var gameName = TryGetProcessName(_gameWindowTracker.GameProcessId);
                var active2 = string.IsNullOrEmpty(_sessionManager.ActivePlayerId) ? "host" : _sessionManager.ActivePlayerId;
                InputDebugLog.Log(InputDebugLog.LogLevel.Verbose,
                    $"[hook] PASS {KeyNames.VkToName((int)kbd.vkCode)} vk={kbd.vkCode} injected={isInjected} hostActive={isHostActive} gameFg={isGameForeground} fg={foregroundName} game={gameName} activePlayer={active2}");
            }
            else
            {
                // Pass-through (host active or injected key)
                InputDebugLog.Log(InputDebugLog.LogLevel.Verbose,
                    $"[hook] PASS {KeyNames.VkToName((int)kbd.vkCode)} vk={kbd.vkCode} injected={isInjected} hostActive={isHostActive}");
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

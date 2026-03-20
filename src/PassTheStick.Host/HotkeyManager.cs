using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace PassTheStick.Host;

/// <summary>
/// Registers a system hotkey (e.g. Ctrl+Shift+Right). Does NOT change the foreground window when fired.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyIdPass = 1;
    private const int HotkeyIdTakeBack = 2;

    /// <summary>Win32 RegisterHotKey MOD_* flags (not WPF ModifierKeys).</summary>
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;

    private const uint VkRight = 0x27;
    private const uint VkLeft = 0x25;

    private nint _hwnd;
    private bool _registeredPass;
    private bool _registeredTakeBack;

    private nint _cachedRegHwnd;
    private uint _cachedPassMods;
    private uint _cachedPassVk;
    private uint _cachedTakeMods;
    private uint _cachedTakeVk;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    public event Action<string>? HotkeyConflict;

    public void Register(nint windowHandle) =>
        Register(windowHandle, ModControl | ModShift, VkRight, ModControl | ModShift, VkLeft);

    public void Register(nint windowHandle, uint passMods, uint passVk, uint takeMods, uint takeVk)
    {
        if (windowHandle == nint.Zero)
            return;

        // Avoid unregister/re-register churn when the shell calls Loaded repeatedly (sidebar navigation).
        if (_registeredPass && _registeredTakeBack &&
            _hwnd == windowHandle &&
            _cachedRegHwnd == windowHandle &&
            _cachedPassMods == passMods && _cachedPassVk == passVk &&
            _cachedTakeMods == takeMods && _cachedTakeVk == takeVk)
        {
            return;
        }

        Unregister();
        _hwnd = windowHandle;

        if (!RegisterHotKey(_hwnd, HotkeyIdPass, passMods, passVk))
        {
            var err = Marshal.GetLastWin32Error();
            HotkeyConflict?.Invoke(
                $"Hotkey conflict — pass stick combo is in use (err={err}). Change it in Settings or close the other app using the same shortcut.");
        }
        else
        {
            _registeredPass = true;
        }

        if (!RegisterHotKey(_hwnd, HotkeyIdTakeBack, takeMods, takeVk))
        {
            var err = Marshal.GetLastWin32Error();
            HotkeyConflict?.Invoke(
                $"Hotkey conflict — take-back combo is in use (err={err}). Change it in Settings or close the other app using the same shortcut.");
        }
        else
        {
            _registeredTakeBack = true;
        }

        if (_registeredPass && _registeredTakeBack)
        {
            _cachedRegHwnd = windowHandle;
            _cachedPassMods = passMods;
            _cachedPassVk = passVk;
            _cachedTakeMods = takeMods;
            _cachedTakeVk = takeVk;
        }
        else
        {
            _cachedRegHwnd = nint.Zero;
        }
    }

    public void Unregister()
    {
        if (_hwnd == nint.Zero) return;
        if (_registeredPass)
        {
            UnregisterHotKey(_hwnd, HotkeyIdPass);
            _registeredPass = false;
        }

        if (_registeredTakeBack)
        {
            UnregisterHotKey(_hwnd, HotkeyIdTakeBack);
            _registeredTakeBack = false;
        }

        _cachedRegHwnd = nint.Zero;
    }

    public static bool TryGetHotkey(int msg, nint wParam, out HotkeyKind kind)
    {
        kind = HotkeyKind.Pass;
        if (msg != WM_HOTKEY) return false;
        if (wParam == (nint)HotkeyIdPass) { kind = HotkeyKind.Pass; return true; }
        if (wParam == (nint)HotkeyIdTakeBack) { kind = HotkeyKind.TakeBack; return true; }
        return false;
    }

    public enum HotkeyKind
    {
        Pass,
        TakeBack
    }

    public void Dispose() => Unregister();
}

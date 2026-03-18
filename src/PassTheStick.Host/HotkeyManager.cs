using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace PassTheStick.Host;

/// <summary>
/// Registers a system hotkey (e.g. Ctrl+Shift+Right). Does NOT change the foreground window when fired.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int MOD_CONTROL = 0x0002;
    private const int MOD_SHIFT = 0x0001;
    private const int VK_RIGHT = 0x27;
    private const int VK_LEFT = 0x25;
    private const int HotkeyIdPass = 1;
    private const int HotkeyIdTakeBack = 2;

    private nint _hwnd;
    private bool _registeredPass;
    private bool _registeredTakeBack;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    public event Action<string>? HotkeyConflict;

    public void Register(nint windowHandle)
    {
        _hwnd = windowHandle;
        _registeredPass = false;
        _registeredTakeBack = false;

        if (RegisterHotKey(_hwnd, HotkeyIdPass, (uint)(MOD_CONTROL | MOD_SHIFT), VK_RIGHT))
        {
            _registeredPass = true;
        }
        else
        {
            var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            HotkeyConflict?.Invoke($"Hotkey conflict detected — Ctrl+Shift+→ is in use (err={err}).");
        }

        if (RegisterHotKey(_hwnd, HotkeyIdTakeBack, (uint)(MOD_CONTROL | MOD_SHIFT), VK_LEFT))
        {
            _registeredTakeBack = true;
        }
        else
        {
            var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            HotkeyConflict?.Invoke($"Hotkey conflict detected — Ctrl+Shift+← is in use (err={err}).");
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
        if (!_registeredPass && !_registeredTakeBack)
        {
            // nothing
        }
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

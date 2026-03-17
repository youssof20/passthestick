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
    private static readonly int HotkeyId = 1;

    private nint _hwnd;
    private bool _registered;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    public void Register(nint windowHandle)
    {
        _hwnd = windowHandle;
        _registered = RegisterHotKey(_hwnd, HotkeyId, (uint)(MOD_CONTROL | MOD_SHIFT), VK_RIGHT);
    }

    public void Unregister()
    {
        if (_registered && _hwnd != nint.Zero)
        {
            UnregisterHotKey(_hwnd, HotkeyId);
            _registered = false;
        }
    }

    public static bool IsHotkeyMessage(int msg, nint wParam) =>
        msg == WM_HOTKEY && wParam == (nint)HotkeyId;

    public void Dispose() => Unregister();
}

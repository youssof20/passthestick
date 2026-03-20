using System.Runtime.InteropServices;

namespace PassTheStick.Host;

/// <summary>Extended window styles (e.g. WS_EX_NOACTIVATE for overlay-style windows).</summary>
internal static class Win32WindowStyles
{
    public const int GwlExStyle = -20;
    public const int WsExNoActivate = 0x08000000;

    public static void SetNoActivate(nint hwnd, bool enable)
    {
        if (hwnd == nint.Zero) return;
        var ex = GetWindowLongPtr(hwnd, GwlExStyle);
        var newEx = enable ? (ex | WsExNoActivate) : (ex & ~(nint)WsExNoActivate);
        SetWindowLongPtr(hwnd, GwlExStyle, newEx);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);
}

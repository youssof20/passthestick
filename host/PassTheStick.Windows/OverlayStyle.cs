namespace PassTheStick.Windows;

public static class OverlayStyle
{
    private const int GwlExStyle = -20;
    private const nint WsExTransparent = 0x20;
    private const nint WsExToolWindow = 0x80;
    private const nint WsExNoActivate = 0x08000000;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    /// <summary>Moves a window to screen pixel coordinates without resizing or activating it. WPF Left and Top are in device-independent units and drift on scaled displays, so the overlay is placed in pixels.</summary>
    public static void MoveTo(nint window, int x, int y) =>
        NativeMethods.SetWindowPos(window, 0, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);

    /// <summary>Makes a window click-through, hidden from Alt+Tab and unable to take focus, so it can never interrupt the game.</summary>
    public static void MakePassive(nint window)
    {
        var style = NativeMethods.GetWindowLongPtr(window, GwlExStyle);
        _ = NativeMethods.SetWindowLongPtr(window, GwlExStyle, style | WsExTransparent | WsExToolWindow | WsExNoActivate);
    }
}

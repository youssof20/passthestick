namespace PassTheStick.Windows;

public static class TitleBar
{
    private const int UseImmersiveDarkMode = 20;

    /// <summary>Makes the native title bar follow the app theme. Does nothing on Windows builds that lack the attribute.</summary>
    public static void UseDark(nint window, bool dark)
    {
        var value = dark ? 1 : 0;
        _ = NativeMethods.DwmSetWindowAttribute(window, UseImmersiveDarkMode, ref value, sizeof(int));
    }
}

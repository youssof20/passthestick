using System.Runtime.InteropServices;

namespace PassTheStick.Windows;

public enum HotkeyResult
{
    Registered,

    /// <summary>Another program already owns this key combination.</summary>
    InUse,

    Failed,
}

/// <summary>System-wide hotkeys via RegisterHotKey. They work while the game has focus and never steal it.</summary>
public static class Hotkeys
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    public const uint ModNoRepeat = 0x4000;
    public const int WmHotkey = 0x0312;

    private const int ErrorHotkeyAlreadyRegistered = 1409;

    public static HotkeyResult TryRegister(nint window, int id, uint modifiers, uint virtualKey)
    {
        if (NativeMethods.RegisterHotKey(window, id, modifiers | ModNoRepeat, virtualKey))
        {
            return HotkeyResult.Registered;
        }

        return Marshal.GetLastWin32Error() == ErrorHotkeyAlreadyRegistered ? HotkeyResult.InUse : HotkeyResult.Failed;
    }

    public static void Unregister(nint window, int id) => NativeMethods.UnregisterHotKey(window, id);
}

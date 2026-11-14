using System.Runtime.InteropServices;

namespace PassTheStick.Windows;

internal static partial class NativeMethods
{
    public const uint InputKeyboard = 1;
    public const uint KeyeventfExtendedKey = 0x0001;
    public const uint KeyeventfKeyUp = 0x0002;
    public const uint KeyeventfScanCode = 0x0008;
    public const uint GaRootOwner = 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KeybdInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    /// <summary>The Win32 INPUT struct is a tagged union; on x64 the union starts at offset 8 and the struct is 40 bytes.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    public struct Input
    {
        [FieldOffset(0)]
        public uint Type;

        [FieldOffset(8)]
        public KeybdInput Keyboard;

        [FieldOffset(8)]
        public MouseInput Mouse;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint SendInput(uint count, Input[] inputs, int size);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    public static partial nint GetAncestor(nint window, uint flags);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint window, out Rect rect);

    [LibraryImport("winmm.dll")]
    public static partial uint timeBeginPeriod(uint milliseconds);

    [LibraryImport("winmm.dll")]
    public static partial uint timeEndPeriod(uint milliseconds);
}

internal static partial class NativeToken
{
    private const uint TokenQuery = 0x0008;
    private const int TokenElevationClass = 20;

    [System.Runtime.InteropServices.LibraryImport("advapi32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [System.Runtime.InteropServices.LibraryImport("advapi32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(nint token, int infoClass, out int info, int length, out int returned);

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    public static bool IsElevated(nint processHandle)
    {
        if (!OpenProcessToken(processHandle, TokenQuery, out var token))
        {
            return false;
        }

        try
        {
            return GetTokenInformation(token, TokenElevationClass, out var elevated, sizeof(int), out _) && elevated != 0;
        }
        finally
        {
            _ = CloseHandle(token);
        }
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PassTheStick.Windows;

public sealed record WindowInfo(nint Handle, string Title, string ProcessName)
{
    public override string ToString() => $"{Title} ({ProcessName})";
}

/// <summary>Lists the windows a person could mean by "the game": visible, titled, top-level, not tool windows.</summary>
public static class WindowCatalog
{
    private const int GwlExStyle = -20;
    private const nint WsExToolWindow = 0x80;
    private const uint GwOwner = 4;

    public static IReadOnlyList<WindowInfo> List()
    {
        var handles = new List<nint>();
        var gc = GCHandle.Alloc(handles);
        try
        {
            unsafe
            {
                _ = NativeMethods.EnumWindows(&Collect, GCHandle.ToIntPtr(gc));
            }
        }
        finally
        {
            gc.Free();
        }

        var self = Environment.ProcessId;
        var result = new List<WindowInfo>();
        foreach (var handle in handles)
        {
            if (Describe(handle, self) is { } info)
            {
                result.Add(info);
            }
        }

        return [.. result.OrderBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase).ThenBy(w => w.Title, StringComparer.OrdinalIgnoreCase)];
    }

    [UnmanagedCallersOnly]
    private static int Collect(nint window, nint state)
    {
        ((List<nint>)GCHandle.FromIntPtr(state).Target!).Add(window);
        return 1;
    }

    private static WindowInfo? Describe(nint handle, int selfProcessId)
    {
        if (!NativeMethods.IsWindowVisible(handle) || NativeMethods.GetWindow(handle, GwOwner) != 0)
        {
            return null;
        }

        if ((NativeMethods.GetWindowLongPtr(handle, GwlExStyle) & WsExToolWindow) != 0)
        {
            return null;
        }

        var length = NativeMethods.GetWindowTextLength(handle);
        if (length <= 0)
        {
            return null;
        }

        var buffer = new char[length + 1];
        var read = NativeMethods.GetWindowText(handle, buffer, buffer.Length);
        var title = new string(buffer, 0, read).Trim();
        if (title.Length == 0 || title == "Program Manager")
        {
            return null;
        }

        _ = NativeMethods.GetWindowThreadProcessId(handle, out var pid);
        if (pid == selfProcessId)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById((int)pid);
            return new WindowInfo(handle, title, process.ProcessName);
        }
        catch (ArgumentException)
        {
            return null; // the process ended while we were listing
        }
    }
}

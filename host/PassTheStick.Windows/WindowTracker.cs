using System.Diagnostics;
using System.Security.Principal;
using PassTheStick.Platform;

namespace PassTheStick.Windows;

/// <summary>The decision rule, separate from the Win32 calls so it can be tested.</summary>
public static class ForegroundPolicy
{
    /// <summary>
    /// The game is in front when the foreground window is the pinned window or one it owns (a dialog, a launcher
    /// child). A different top-level window from the same process does not count: the old code matched on process
    /// id alone, so Dolphin's log window counted as the game.
    /// </summary>
    public static bool Matches(nint foreground, nint pinned, Func<nint, nint> rootOwnerOf) =>
        foreground != 0 && pinned != 0 && (foreground == pinned || rootOwnerOf(foreground) == pinned);
}

public sealed class WindowTracker : IWindowTracker
{
    private static readonly TimeSpan NullForegroundGrace = TimeSpan.FromMilliseconds(500);

    private readonly TimeProvider _clock;
    private nint _pinned;
    private bool _lastResult;
    private long _lastResultAt;

    public WindowTracker(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    public nint Handle => _pinned;

    public bool IsPinned => _pinned != 0 && NativeMethods.IsWindow(_pinned);

    public bool IsMinimized => IsPinned && NativeMethods.IsIconic(_pinned);

    public uint ProcessId
    {
        get
        {
            if (_pinned == 0)
            {
                return 0;
            }

            _ = NativeMethods.GetWindowThreadProcessId(_pinned, out var pid);
            return pid;
        }
    }

    /// <summary>
    /// Focus changes briefly report no foreground window; during that gap the last answer is kept for 500 ms
    /// instead of releasing every key on a flicker.
    /// </summary>
    public bool IsGameForeground
    {
        get
        {
            if (!IsPinned)
            {
                return false;
            }

            var foreground = NativeMethods.GetForegroundWindow();
            if (foreground == 0)
            {
                return _lastResult && _clock.GetElapsedTime(_lastResultAt) < NullForegroundGrace;
            }

            _lastResult = ForegroundPolicy.Matches(foreground, _pinned, w => NativeMethods.GetAncestor(w, NativeMethods.GaRootOwner));
            _lastResultAt = _clock.GetTimestamp();
            return _lastResult;
        }
    }

    public void Pin(nint window)
    {
        if (window == 0 || !NativeMethods.IsWindow(window))
        {
            throw new InvalidOperationException("That window is gone. Pick the game window again.");
        }

        _pinned = window;
        _lastResult = false;
    }

    public void PinForeground() => Pin(NativeMethods.GetForegroundWindow());

    public void Clear() => _pinned = 0;

    public bool TryGetRect(out (int Left, int Top, int Width, int Height) rect)
    {
        rect = default;
        if (!IsPinned || !NativeMethods.GetWindowRect(_pinned, out var r))
        {
            return false;
        }

        rect = (r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        return rect.Width > 0 && rect.Height > 0;
    }

    /// <summary>True when the game runs as administrator and this app does not. Input would be blocked by UIPI.</summary>
    public bool NeedsElevation() => IsPinned && Elevation.IsProcessElevated(ProcessId) && !Elevation.IsCurrentProcessElevated();
}

public static class Elevation
{
    public static bool IsCurrentProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>False when the process cannot be inspected; that is treated as "not elevated".</summary>
    public static bool IsProcessElevated(uint processId)
    {
        if (processId == 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return NativeToken.IsElevated(process.Handle);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

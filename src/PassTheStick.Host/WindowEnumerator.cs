using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PassTheStick.Host;

public sealed record WindowInfo(nint Hwnd, uint ProcessId, string Title, string ProcessName)
{
    public string Display => $"{Title}  —  {ProcessName}";
}

public static class WindowEnumerator
{
    public static List<WindowInfo> GetCandidateWindows()
    {
        var list = new List<WindowInfo>();
        EnumWindows((hwnd, _) =>
        {
            if (hwnd == nint.Zero) return true;
            if (!IsWindowVisible(hwnd)) return true;
            if (GetWindowTextLength(hwnd) <= 0) return true;

            var title = GetWindowTitle(hwnd);
            if (string.IsNullOrWhiteSpace(title)) return true;

            // Filter out our own windows.
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return true;
            if (pid == (uint)Environment.ProcessId) return true;

            var procName = TryGetProcessName(pid);
            if (string.IsNullOrWhiteSpace(procName)) procName = "unknown";

            // Light filtering of common non-game/system UI.
            var pn = procName.ToLowerInvariant();
            if (pn is "dwm" or "explorer" or "taskmgr" or "sihost" or "applicationframehost")
                return true;

            list.Add(new WindowInfo(hwnd, pid, title.Trim(), procName));
            return true;
        }, nint.Zero);

        // Stable ordering: process name then title.
        return list
            .OrderBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string GetWindowTitle(nint hwnd)
    {
        int len = GetWindowTextLength(hwnd);
        if (len <= 0) return string.Empty;
        var sb = new StringBuilder(len + 1);
        _ = GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string TryGetProcessName(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
}


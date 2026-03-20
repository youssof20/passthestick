using System.Runtime.InteropServices;
using PassTheStick.Shared;

namespace PassTheStick.Host;

/// <summary>Brings the pinned game window to the foreground (AttachThreadInput + SetForegroundWindow).</summary>
public static class GameFocusHelper
{
    private const int SwRestore = 9;

    public static void ForceGameForeground(nint gameHwnd, string? logContext = null)
    {
        if (gameHwnd == nint.Zero || !IsWindow(gameHwnd))
            return;

        try
        {
            ShowWindow(gameHwnd, SwRestore);

            var gameThread = GetWindowThreadProcessId(gameHwnd, out _);
            var ourThread = GetCurrentThreadId();
            var attached = gameThread != 0 &&
                           gameThread != ourThread &&
                           AttachThreadInput(ourThread, gameThread, true);

            try
            {
                SetForegroundWindow(gameHwnd);
                BringWindowToTop(gameHwnd);
                SetFocus(gameHwnd);
            }
            finally
            {
                if (attached)
                    AttachThreadInput(ourThread, gameThread, false);
            }

            var msg = string.IsNullOrEmpty(logContext)
                ? "[focus] Forced game window to foreground"
                : "[focus] Forced game window to foreground — " + logContext;
            InputDebugLog.Log(InputDebugLog.LogLevel.Info, msg);
        }
        catch (Exception ex)
        {
            InputDebugLog.Log(InputDebugLog.LogLevel.Warning, $"[focus] Failed: {ex.Message}");
        }
    }

    [DllImport("user32.dll")] private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll")] private static extern nint SetFocus(nint hWnd);

    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
}

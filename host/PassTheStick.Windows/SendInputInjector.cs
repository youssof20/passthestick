using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PassTheStick.Platform;

namespace PassTheStick.Windows;

/// <summary>
/// Injects keys with SendInput using scan codes, which DirectInput and Raw Input games need. The extended
/// flag comes from the key itself, so main Enter and numpad Enter stay different keys.
/// </summary>
public sealed partial class SendInputInjector : IInputInjector
{
    private readonly ILogger _log;

    public SendInputInjector(ILogger log) => _log = log;

    public static uint Flags(ScanKey key, bool down) =>
        NativeMethods.KeyeventfScanCode
        | (down ? 0u : NativeMethods.KeyeventfKeyUp)
        | (key.Extended ? NativeMethods.KeyeventfExtendedKey : 0u);

    public void KeyDown(ScanKey key) => Send(key, true);

    public void KeyUp(ScanKey key) => Send(key, false);

    private void Send(ScanKey key, bool down)
    {
        var input = new NativeMethods.Input
        {
            Type = NativeMethods.InputKeyboard,
            Keyboard = new NativeMethods.KeybdInput { ScanCode = key.ScanCode, Flags = Flags(key, down) },
        };
        var sent = NativeMethods.SendInput(1, [input], Marshal.SizeOf<NativeMethods.Input>());
        if (sent != 1)
        {
            var error = Marshal.GetLastWin32Error();
            LogSendFailed(_log, key.ScanCode, down, error, error == 5 ? "access denied: the game may be running as administrator" : new Win32Exception(error).Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "SendInput failed for scan code {ScanCode} (down={Down}): error {Error}, {Reason}")]
    private static partial void LogSendFailed(ILogger logger, ushort scanCode, bool down, int error, string reason);
}

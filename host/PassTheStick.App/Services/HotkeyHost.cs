using System.Windows.Interop;
using PassTheStick.Windows;

namespace PassTheStick.App.Services;

/// <summary>
/// Owns a hidden message-only window that receives WM_HOTKEY. Hotkeys fire while the game is focused and
/// never activate anything, so the game keeps the keyboard.
/// </summary>
public sealed class HotkeyHost : IDisposable
{
    private const int PassId = 1;
    private const int TakeBackId = 2;
    private const nint HwndMessage = -3;

    private readonly HwndSource _source;
    private readonly List<int> _registered = [];

    public HotkeyHost()
    {
        var parameters = new HwndSourceParameters("PassTheStickHotkeys") { ParentWindow = HwndMessage, Width = 0, Height = 0 };
        _source = new HwndSource(parameters);
        _source.AddHook(OnMessage);
    }

    public event Action? PassPressed;

    public event Action? TakeBackPressed;

    /// <summary>Registers both hotkeys and returns one plain-language line per one that failed.</summary>
    public IReadOnlyList<string> Register(HotkeyBinding pass, HotkeyBinding takeBack)
    {
        var problems = new List<string>();
        Try(PassId, pass, "pass the stick", problems);
        Try(TakeBackId, takeBack, "take the stick back", problems);
        return problems;
    }

    public void Dispose()
    {
        foreach (var id in _registered)
        {
            Hotkeys.Unregister(_source.Handle, id);
        }

        _registered.Clear();
        _source.RemoveHook(OnMessage);
        _source.Dispose();
    }

    private void Try(int id, HotkeyBinding binding, string what, List<string> problems)
    {
        switch (Hotkeys.TryRegister(_source.Handle, id, binding.Modifiers, binding.VirtualKey))
        {
            case HotkeyResult.Registered:
                _registered.Add(id);
                break;
            case HotkeyResult.InUse:
                problems.Add($"{binding} is already used by another program, so it cannot {what}. Use the buttons or the tray menu instead.");
                break;
            default:
                problems.Add($"Windows would not register {binding} to {what}. Use the buttons or the tray menu instead.");
                break;
        }
    }

    private nint OnMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != Hotkeys.WmHotkey)
        {
            return 0;
        }

        handled = true;
        switch ((int)wParam)
        {
            case PassId:
                PassPressed?.Invoke();
                break;
            case TakeBackId:
                TakeBackPressed?.Invoke();
                break;
            default:
                break;
        }

        return 0;
    }
}

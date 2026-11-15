using System.Windows.Input;
using PassTheStick.Windows;

namespace PassTheStick.App.Services;

/// <summary>A key combination such as Ctrl+Shift+Right. At least one modifier is required so a bare key can never be taken from typing.</summary>
public sealed record HotkeyBinding(uint Modifiers, Key Key)
{
    public static HotkeyBinding DefaultPass { get; } = new(Hotkeys.ModControl | Hotkeys.ModShift, Key.Right);

    public static HotkeyBinding DefaultTakeBack { get; } = new(Hotkeys.ModControl | Hotkeys.ModShift, Key.Left);

    public uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    public static bool TryParse(string? text, out HotkeyBinding binding)
    {
        binding = DefaultPass;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        uint mods = 0;
        Key? key = null;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL":
                    mods |= Hotkeys.ModControl;
                    break;
                case "SHIFT":
                    mods |= Hotkeys.ModShift;
                    break;
                case "ALT":
                    mods |= Hotkeys.ModAlt;
                    break;
                case "WIN":
                    mods |= Hotkeys.ModWin;
                    break;
                default:
                    if (key is not null || !Enum.TryParse<Key>(part, ignoreCase: true, out var parsed) || parsed == Key.None || KeyInterop.VirtualKeyFromKey(parsed) == 0)
                    {
                        return false;
                    }

                    key = parsed;
                    break;
            }
        }

        if (key is null || mods == 0)
        {
            return false;
        }

        binding = new HotkeyBinding(mods, key.Value);
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if ((Modifiers & Hotkeys.ModControl) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((Modifiers & Hotkeys.ModShift) != 0)
        {
            parts.Add("Shift");
        }

        if ((Modifiers & Hotkeys.ModAlt) != 0)
        {
            parts.Add("Alt");
        }

        if ((Modifiers & Hotkeys.ModWin) != 0)
        {
            parts.Add("Win");
        }

        parts.Add(Key.ToString());
        return string.Join('+', parts);
    }
}

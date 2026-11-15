using PassTheStick.Platform;

namespace PassTheStick.Core;

/// <summary>
/// Maps <c>KeyboardEvent.code</c> (physical position) to a set 1 scan code. Layout never matters: KeyW is the
/// key at the W position on a QWERTY board even when the guest's keyboard prints Z there.
/// Windows keys are deliberately absent so a guest cannot open the host's Start menu.
/// </summary>
public static class KeyMap
{
    private static readonly Dictionary<string, ScanKey> Table = Build();

    public static bool TryResolve(string? code, out ScanKey key)
    {
        key = default;
        return code is not null && Table.TryGetValue(code, out key);
    }

    private static Dictionary<string, ScanKey> Build()
    {
        var t = new Dictionary<string, ScanKey>(StringComparer.Ordinal);

        void Add(string code, int sc, bool ext = false) => t[code] = new ScanKey((ushort)sc, ext);

        // Letters: scan codes follow the physical QWERTY rows.
        var rows = new (string Letters, int First)[] { ("QWERTYUIOP", 0x10), ("ASDFGHJKL", 0x1E), ("ZXCVBNM", 0x2C) };
        foreach (var (letters, first) in rows)
        {
            for (var i = 0; i < letters.Length; i++)
            {
                Add("Key" + letters[i], first + i);
            }
        }

        for (var i = 1; i <= 9; i++)
        {
            Add("Digit" + i, 0x01 + i);
        }

        Add("Digit0", 0x0B);

        Add("Escape", 0x01);
        Add("Minus", 0x0C);
        Add("Equal", 0x0D);
        Add("Backspace", 0x0E);
        Add("Tab", 0x0F);
        Add("BracketLeft", 0x1A);
        Add("BracketRight", 0x1B);
        Add("Enter", 0x1C);
        Add("ControlLeft", 0x1D);
        Add("Semicolon", 0x27);
        Add("Quote", 0x28);
        Add("Backquote", 0x29);
        Add("ShiftLeft", 0x2A);
        Add("Backslash", 0x2B);
        Add("Comma", 0x33);
        Add("Period", 0x34);
        Add("Slash", 0x35);
        Add("ShiftRight", 0x36);
        Add("AltLeft", 0x38);
        Add("Space", 0x39);
        Add("CapsLock", 0x3A);
        for (var i = 1; i <= 10; i++)
        {
            Add("F" + i, 0x3A + i);
        }

        Add("F11", 0x57);
        Add("F12", 0x58);

        // Numpad (not extended) and the extended numpad keys.
        string[] numpad = ["7", "8", "9", "Subtract", "4", "5", "6", "Add", "1", "2", "3", "0", "Decimal"];
        for (var i = 0; i < numpad.Length; i++)
        {
            Add("Numpad" + numpad[i], 0x47 + i);
        }

        Add("NumpadMultiply", 0x37);
        Add("NumLock", 0x45);
        Add("NumpadEnter", 0x1C, true);
        Add("NumpadDivide", 0x35, true);

        // Extended keys: the E0 flag is what tells Left arrow from numpad 4.
        Add("ControlRight", 0x1D, true);
        Add("AltRight", 0x38, true);
        Add("ArrowUp", 0x48, true);
        Add("ArrowLeft", 0x4B, true);
        Add("ArrowRight", 0x4D, true);
        Add("ArrowDown", 0x50, true);
        Add("Home", 0x47, true);
        Add("PageUp", 0x49, true);
        Add("End", 0x4F, true);
        Add("PageDown", 0x51, true);
        Add("Insert", 0x52, true);
        Add("Delete", 0x53, true);
        return t;
    }
}

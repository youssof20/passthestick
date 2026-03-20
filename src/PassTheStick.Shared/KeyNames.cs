namespace PassTheStick.Shared;

public static class KeyNames
{
    /// <summary>Human-readable shortcut for Win32 RegisterHotKey fsModifiers + vk.</summary>
    public static string FormatRegisterHotKey(uint fsModifiers, uint vk)
    {
        var parts = new List<string>();
        if ((fsModifiers & 0x0001) != 0) parts.Add("Alt");
        if ((fsModifiers & 0x0002) != 0) parts.Add("Ctrl");
        if ((fsModifiers & 0x0004) != 0) parts.Add("Shift");
        if ((fsModifiers & 0x0008) != 0) parts.Add("Win");
        if (vk != 0) parts.Add(VkToName((int)vk));
        return parts.Count == 0 ? "(none)" : string.Join(" + ", parts);
    }

    public static string VkToName(int vk) => vk switch
    {
        65 => "A", 66 => "B", 67 => "C", 68 => "D", 69 => "E",
        70 => "F", 71 => "G", 72 => "H", 73 => "I", 74 => "J",
        75 => "K", 76 => "L", 77 => "M", 78 => "N", 79 => "O",
        80 => "P", 81 => "Q", 82 => "R", 83 => "S", 84 => "T",
        85 => "U", 86 => "V", 87 => "W", 88 => "X", 89 => "Y",
        90 => "Z", 32 => "Space", 13 => "Enter", 27 => "Esc",
        38 => "Up", 40 => "Down", 37 => "Left", 39 => "Right",
        16 => "Shift", 17 => "Ctrl", 18 => "Alt",
        9 => "Tab", 8 => "Backspace", 20 => "CapsLock",
        _ => $"vk{vk}"
    };
}


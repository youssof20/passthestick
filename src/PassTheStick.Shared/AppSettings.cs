using System.IO;
using System.Text.Json;

namespace PassTheStick.Shared;

public sealed class AppSettings
{
    /// <summary>Bump when new fields need migration (see SettingsStore.Load).</summary>
    public int SettingsSchemaVersion { get; set; } = 2;

    public string? RelayUrlOverride { get; set; }

    // Optional session persistence (best-effort; depends on relay room still existing).
    public string? LastRoomCode { get; set; }
    public string? LastRelayUrl { get; set; }
    public string? LastGameExePath { get; set; }

    /// <summary>First-launch wizard completed.</summary>
    public bool OnboardingCompleted { get; set; }

    /// <summary>Play Windows system sounds on pass/receive (off by default).</summary>
    public bool EnableStickSounds { get; set; }

    /// <summary>Bring pinned game to foreground after passing the stick (default on).</summary>
    public bool AutoFocusGameOnStickReceive { get; set; } = true;

    /// <summary>Release injected held keys when the stick moves to another player (default on).</summary>
    public bool ReleaseHeldKeysOnStickPass { get; set; } = true;

    /// <summary>Show in-game overlay pill during active host sessions (default on).</summary>
    public bool ShowOverlayDuringSessions { get; set; } = true;

    /// <summary>RegisterHotKey fsModifiers + vk for pass stick (default: Ctrl+Shift+Right).</summary>
    public uint PassStickHotkeyModifiers { get; set; } = HotkeySettingsDefaults.PassModifiers;

    public uint PassStickHotkeyVk { get; set; } = HotkeySettingsDefaults.PassVk;

    /// <summary>RegisterHotKey fsModifiers + vk for take stick back (default: Ctrl+Shift+Left).</summary>
    public uint TakeStickBackHotkeyModifiers { get; set; } = HotkeySettingsDefaults.TakeBackModifiers;

    public uint TakeStickBackHotkeyVk { get; set; } = HotkeySettingsDefaults.TakeBackVk;
}

/// <summary>Win32 RegisterHotKey defaults matching in-app docs.</summary>
public static class HotkeySettingsDefaults
{
    // MOD_CONTROL | MOD_SHIFT per Win32 (NOT THE SAME AS WPF ModifierKeys numeric values).
    public const uint PassModifiers = 0x0002 | 0x0004;
    public const uint PassVk = 0x27; // Right
    public const uint TakeBackModifiers = 0x0002 | 0x0004;
    public const uint TakeBackVk = 0x25; // Left
}

public static class SettingsStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string SettingsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PassTheStick");

    private static string SettingsPath => Path.Combine(SettingsDir, "settings.json");

    public static AppSettings Load()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(SettingsPath))
                    return new AppSettings();

                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                return MigrateIfNeeded(settings);
            }
            catch
            {
                return new AppSettings();
            }
        }
    }

    private static AppSettings MigrateIfNeeded(AppSettings s)
    {
        if (s.SettingsSchemaVersion >= 2)
            return s;

        // v1 files (or first run): restore intended defaults for new toggles / hotkeys.
        s.AutoFocusGameOnStickReceive = true;
        s.ReleaseHeldKeysOnStickPass = true;
        s.ShowOverlayDuringSessions = true;
        if (s.PassStickHotkeyVk == 0 || s.PassStickHotkeyModifiers == 0)
        {
            s.PassStickHotkeyModifiers = HotkeySettingsDefaults.PassModifiers;
            s.PassStickHotkeyVk = HotkeySettingsDefaults.PassVk;
        }

        if (s.TakeStickBackHotkeyVk == 0 || s.TakeStickBackHotkeyModifiers == 0)
        {
            s.TakeStickBackHotkeyModifiers = HotkeySettingsDefaults.TakeBackModifiers;
            s.TakeStickBackHotkeyVk = HotkeySettingsDefaults.TakeBackVk;
        }

        s.SettingsSchemaVersion = 2;
        SaveCore(s);
        return s;
    }

    public static void Save(AppSettings settings)
    {
        lock (Gate)
        {
            SaveCore(settings);
        }
    }

    private static void SaveCore(AppSettings settings)
    {
        Directory.CreateDirectory(SettingsDir);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsPath, json);
    }
}


using System.IO;
using System.Text.Json;

namespace PassTheStick.Shared;

public sealed class AppSettings
{
    public string? RelayUrlOverride { get; set; }

    // Optional session persistence (best-effort; depends on relay room still existing).
    public string? LastRoomCode { get; set; }
    public string? LastRelayUrl { get; set; }
    public string? LastGameExePath { get; set; }

    /// <summary>First-launch wizard completed.</summary>
    public bool OnboardingCompleted { get; set; }

    /// <summary>Play Windows system sounds on pass/receive (off by default).</summary>
    public bool EnableStickSounds { get; set; }
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
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
            catch
            {
                return new AppSettings();
            }
        }
    }

    public static void Save(AppSettings settings)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
    }
}


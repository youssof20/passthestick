using System.Text.Json;
using PassTheStick.Windows;

namespace PassTheStick.App.Services;

/// <summary>Persisted settings. Bump <see cref="CurrentVersion"/> and add a step in <see cref="SettingsStore.Migrate"/> when a field changes meaning.</summary>
public sealed record AppSettings
{
    public const int CurrentVersion = 1;

    /// <summary>The release build replaces this with the production Worker address; see HANDOFF.md.</summary>
    public const string DefaultServerUrl = "http://127.0.0.1:8787";

    public int Version { get; init; } = CurrentVersion;

    public string ServerUrl { get; init; } = DefaultServerUrl;

    public IReadOnlyList<string> IceServers { get; init; } = ["stun:stun.cloudflare.com:3478"];

    public string? LastGameProcess { get; init; }

    /// <summary>False until the first-run wizard has been finished or skipped.</summary>
    public bool OnboardingCompleted { get; init; }

    /// <summary>Whether guests may get virtual controllers. Keyboard-only sessions need no driver at all.</summary>
    public bool UseVirtualPads { get; init; } = true;

    public string PassHotkey { get; init; } = "Ctrl+Shift+Right";

    public string TakeBackHotkey { get; init; } = "Ctrl+Shift+Left";
}

public sealed record SettingsLoadResult(AppSettings Settings, string? Notice);

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;

    public SettingsStore(string path) => _path = path;

    public static string DefaultPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PassTheStick", "settings.json");

    public SettingsLoadResult Load()
    {
        if (!File.Exists(_path))
        {
            return new SettingsLoadResult(new AppSettings(), null);
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? throw new JsonException("empty");
            return new SettingsLoadResult(Migrate(loaded), null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            var kept = _path + ".damaged-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
            try
            {
                File.Move(_path, kept);
            }
            catch (IOException)
            {
                kept = _path;
            }

            return new SettingsLoadResult(new AppSettings(), $"Your settings file could not be read, so defaults are in use. The old file was kept as {kept}.");
        }
    }

    public void Save(AppSettings settings) =>
        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(settings with { Version = AppSettings.CurrentVersion }, Json));

    /// <summary>Older files are upgraded here; a file from a newer app is used as is rather than rejected.</summary>
    internal static AppSettings Migrate(AppSettings loaded) => loaded;
}

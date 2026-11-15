using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace PassTheStick.App.Services;

/// <summary>Removes what must not leave the machine from log text: tokens, room codes in links, addresses and the user name.</summary>
public static partial class Redactor
{
    public static string Redact(string text, IEnumerable<string>? knownSecrets = null)
    {
        var result = text;
        foreach (var secret in knownSecrets ?? [])
        {
            if (secret.Length >= 6)
            {
                result = result.Replace(secret, "[secret]", StringComparison.Ordinal);
            }
        }

        result = TokenField().Replace(result, "$1[token]\"");
        result = Join().Replace(result, "$1[code]");
        result = RoomPath().Replace(result, "$1[code]");
        result = Ipv4().Replace(result, "[address]");
        result = Ipv6().Replace(result, "[address]");
        result = UserProfile().Replace(result, @"C:\Users\[user]");
        return result;
    }

    [GeneratedRegex("(\"(?:hostToken|resumeToken|token)\"\\s*:\\s*\")[^\"]*\"", RegexOptions.IgnoreCase)]
    private static partial Regex TokenField();

    [GeneratedRegex("(/join/?(?:\\?[^#\\s]*)?#)[A-Z0-9]{6}", RegexOptions.IgnoreCase)]
    private static partial Regex Join();

    [GeneratedRegex("(/v1/room/)[A-Za-z0-9]{6}")]
    private static partial Regex RoomPath();

    [GeneratedRegex("\\b(?:\\d{1,3}\\.){3}\\d{1,3}\\b")]
    private static partial Regex Ipv4();

    [GeneratedRegex("\\b(?:[0-9a-f]{1,4}:){3,7}[0-9a-f]{1,4}\\b", RegexOptions.IgnoreCase)]
    private static partial Regex Ipv6();

    [GeneratedRegex("C:\\\\Users\\\\[^\\\\\\s\"]+", RegexOptions.IgnoreCase)]
    private static partial Regex UserProfile();
}

/// <summary>Writes a plain log file per day and keeps the last few. Used by the diagnostics export; nothing is sent anywhere.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly Lock _gate = new();
    private readonly int _keep;

    public FileLoggerProvider(string directory, int keepFiles = 5)
    {
        _directory = directory;
        _keep = keepFiles;
        Directory.CreateDirectory(directory);
        Trim();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                File.AppendAllText(Path.Combine(_directory, $"pts-{DateTime.UtcNow:yyyyMMdd}.log"), line + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException)
            {
                // Logging must never break the app.
            }
        }
    }

    private void Trim()
    {
        try
        {
            foreach (var old in Directory.GetFiles(_directory, "pts-*.log").OrderByDescending(f => f, StringComparer.Ordinal).Skip(_keep))
            {
                File.Delete(old);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An old log that cannot be removed is harmless.
        }
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var text = $"{DateTime.UtcNow:O} {logLevel,-11} {category} {formatter(state, exception)}";
            if (exception is not null)
            {
                text += " | " + exception.GetType().Name + ": " + exception.Message;
            }

            owner.Write(text);
        }
    }
}

public interface IDiagnosticsService
{
    /// <summary>Writes a redacted bundle for a bug report and returns its path.</summary>
    string Export();
}

public sealed class DiagnosticsService : IDiagnosticsService
{
    private readonly string _logsDirectory;
    private readonly string _settingsPath;
    private readonly string _outputDirectory;
    private readonly Func<IEnumerable<string>> _secrets;
    private readonly Func<DateTime> _now;

    public DiagnosticsService(string logsDirectory, string settingsPath, string outputDirectory, Func<IEnumerable<string>>? secrets = null, Func<DateTime>? now = null)
    {
        _logsDirectory = logsDirectory;
        _settingsPath = settingsPath;
        _outputDirectory = outputDirectory;
        _secrets = secrets ?? (() => []);
        _now = now ?? (() => DateTime.Now);
    }

    public string Export()
    {
        Directory.CreateDirectory(_outputDirectory);
        var path = Path.Combine(_outputDirectory, $"PassTheStick-diagnostics-{_now():yyyyMMdd-HHmm}.zip");
        var secrets = _secrets().ToList();
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        Add(zip, "system.txt", string.Join(Environment.NewLine,
            $"PassTheStick {typeof(DiagnosticsService).Assembly.GetName().Version}",
            $"Windows {Environment.OSVersion.Version} ({RuntimeInformation.OSArchitecture})",
            $".NET {Environment.Version}",
            $"Elevated {PassTheStick.Windows.Elevation.IsCurrentProcessElevated()}",
            $"Culture {System.Globalization.CultureInfo.CurrentUICulture.Name}",
            $"Exported {_now():O}"));

        if (File.Exists(_settingsPath))
        {
            Add(zip, "settings.json", Redactor.Redact(File.ReadAllText(_settingsPath), secrets));
        }

        if (Directory.Exists(_logsDirectory))
        {
            foreach (var log in Directory.GetFiles(_logsDirectory, "pts-*.log").OrderBy(f => f, StringComparer.Ordinal))
            {
                Add(zip, "logs/" + Path.GetFileName(log), Redactor.Redact(ReadShared(log), secrets));
            }
        }

        Add(zip, "README.txt", "This bundle has tokens, room codes, IP addresses and your Windows user name removed. It contains logs and settings only. Attach it to a GitHub issue.");
        return path;
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}

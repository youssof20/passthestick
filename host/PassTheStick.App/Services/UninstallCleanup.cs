using Microsoft.Extensions.Logging;
using PassTheStick.Windows;

namespace PassTheStick.App.Services;

/// <summary>
/// Runs from the uninstaller. Undoes anything this app changed in HidHide and removes its own files. It never touches
/// the drivers (ViGEmBus, HidHide) themselves, or HidHide entries it did not add.
/// </summary>
public static partial class UninstallCleanup
{
    public static void Run(string dataDirectory, Func<IHidHideService?> openHidHide, ILogger log)
    {
        var journal = Path.Combine(dataDirectory, "hidhide.json");
        if (File.Exists(journal))
        {
            try
            {
                if (openHidHide() is { } service)
                {
                    using var guard = new HidHideGuard(service, journal, log);
                    guard.RecoverLeftovers();
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogHidHideSkipped(log, ex);
            }
        }

        if (!Directory.Exists(dataDirectory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(dataDirectory))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith("settings.json", StringComparison.Ordinal) || name.StartsWith("hidhide.json", StringComparison.Ordinal) || name.EndsWith(".tmp", StringComparison.Ordinal))
            {
                TryDelete(file, log);
            }
        }

        if (!Directory.EnumerateFileSystemEntries(dataDirectory).Any())
        {
            try
            {
                Directory.Delete(dataDirectory);
            }
            catch (IOException ex)
            {
                LogDeleteFailed(log, ex, dataDirectory);
            }
        }
    }

    private static void TryDelete(string path, ILogger log)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogDeleteFailed(log, ex, path);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not undo HidHide changes during uninstall; HidHide may not be installed")]
    private static partial void LogHidHideSkipped(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not remove {Path} during uninstall")]
    private static partial void LogDeleteFailed(ILogger logger, Exception ex, string path);
}

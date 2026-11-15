using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PassTheStick.Windows;

/// <summary>The slice of HidHide that the guard needs. The real one wraps Nefarius.Drivers.HidHide.</summary>
public interface IHidHideService
{
    bool IsActive { get; set; }

    IReadOnlyList<string> BlockedInstanceIds { get; }

    IReadOnlyList<string> ApplicationPaths { get; }

    void AddBlockedInstanceId(string instanceId);

    void RemoveBlockedInstanceId(string instanceId);

    void AddApplicationPath(string path);

    void RemoveApplicationPath(string path);
}

public sealed record HidHideJournal(int Version, IReadOnlyList<string> AddedInstanceIds, string? AddedApplicationPath, bool WasActive);

/// <summary>
/// Hides the host's physical pads while a guest drives, and guarantees the change is undone. Before touching
/// HidHide it writes a journal of exactly what it will add and the previous active state. Restore() undoes
/// only those entries and puts the active flag back, so the user's own HidHide setup is never touched. The
/// journal survives a crash; <see cref="RecoverLeftovers"/> at startup finishes the job.
/// </summary>
public sealed partial class HidHideGuard : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly IHidHideService _service;
    private readonly string _journalPath;
    private readonly ILogger _log;

    public HidHideGuard(IHidHideService service, string journalPath, ILogger log)
    {
        _service = service;
        _journalPath = journalPath;
        _log = log;
    }

    public bool Active => File.Exists(_journalPath);

    /// <summary>Call once at startup, before anything else touches HidHide. Returns true if it had to undo something.</summary>
    public bool RecoverLeftovers() => Restore();

    public void Begin(string applicationPath, IEnumerable<string> instanceIds)
    {
        Restore();

        var blocked = new HashSet<string>(_service.BlockedInstanceIds, StringComparer.OrdinalIgnoreCase);
        var toAdd = instanceIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Where(blocked.Add).ToList();
        var addApp = !_service.ApplicationPaths.Contains(applicationPath, StringComparer.OrdinalIgnoreCase);
        var journal = new HidHideJournal(1, toAdd, addApp ? applicationPath : null, _service.IsActive);

        // The journal goes to disk first, so a crash at any later point can be undone.
        AtomicFile.WriteAllText(_journalPath, JsonSerializer.Serialize(journal, Json));

        if (addApp)
        {
            _service.AddApplicationPath(applicationPath);
        }

        foreach (var id in toAdd)
        {
            _service.AddBlockedInstanceId(id);
        }

        _service.IsActive = true;
    }

    /// <summary>Undoes what the journal says we did. Keeps the journal if any step failed so the next start retries.</summary>
    public bool Restore()
    {
        if (!File.Exists(_journalPath))
        {
            return false;
        }

        HidHideJournal? journal;
        try
        {
            journal = JsonSerializer.Deserialize<HidHideJournal>(File.ReadAllText(_journalPath));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            LogCorruptJournal(_log, ex);
            journal = null;
        }

        if (journal is null)
        {
            File.Delete(_journalPath);
            return false;
        }

        var failed = false;
        foreach (var id in journal.AddedInstanceIds)
        {
            failed |= !Try(() => _service.RemoveBlockedInstanceId(id), id);
        }

        if (journal.AddedApplicationPath is { } path)
        {
            failed |= !Try(() => _service.RemoveApplicationPath(path), path);
        }

        failed |= !Try(() => _service.IsActive = journal.WasActive, "active flag");

        if (!failed)
        {
            File.Delete(_journalPath);
        }

        return true;
    }

    public void Dispose() => Restore();

    private bool Try(Action action, string what)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogRestoreStepFailed(_log, ex, what);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The HidHide journal was unreadable and has been discarded")]
    private static partial void LogCorruptJournal(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not undo HidHide change for {What}; it will be retried next start")]
    private static partial void LogRestoreStepFailed(ILogger logger, Exception ex, string what);
}

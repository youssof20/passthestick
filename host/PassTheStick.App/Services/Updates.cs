using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace PassTheStick.App.Services;

/// <summary>What the app needs from the updater. Faked in tests; the real one wraps Velopack.</summary>
public interface IUpdateClient
{
    /// <summary>False when running from a build folder rather than the installed app, so there is nothing to update.</summary>
    bool IsInstalled { get; }

    /// <summary>Checks the release feed and downloads a newer version if there is one. Returns its version, or null.</summary>
    Task<string?> CheckAndDownloadAsync(CancellationToken cancellationToken);

    void ApplyAndRestart();

    void ApplyOnExit();
}

/// <summary>
/// Decides when updates may be applied. An update is downloaded in the background and then waits: it installs when the
/// app closes, or on request while no session is running. It never restarts the app underneath a live session.
/// </summary>
public sealed partial class UpdateCoordinator
{
    private readonly IUpdateClient _client;
    private readonly Func<bool> _sessionIsLive;
    private readonly bool _enabled;
    private readonly ILogger _log;
    private bool _applying;

    public UpdateCoordinator(IUpdateClient client, Func<bool> sessionIsLive, bool enabled, ILogger log)
    {
        _client = client;
        _sessionIsLive = sessionIsLive;
        _enabled = enabled;
        _log = log;
    }

    public event Action<string>? UpdateReady;

    public string? ReadyVersion { get; private set; }

    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        if (!_enabled || !_client.IsInstalled || ReadyVersion is not null)
        {
            return;
        }

        try
        {
            var version = await _client.CheckAndDownloadAsync(cancellationToken).ConfigureAwait(false);
            if (version is not null)
            {
                ReadyVersion = version;
                UpdateReady?.Invoke(version);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            LogCheckFailed(_log, ex);
        }
    }

    /// <summary>Restarts into the new version. Refused while a session is live.</summary>
    public bool TryInstallNow()
    {
        if (ReadyVersion is null || _sessionIsLive() || _applying)
        {
            return false;
        }

        _applying = true;
        _client.ApplyAndRestart();
        return true;
    }

    /// <summary>Call as the app closes. Schedules the downloaded update to install once the process has exited.</summary>
    public void OnExit()
    {
        if (ReadyVersion is not null && !_applying)
        {
            _applying = true;
            _client.ApplyOnExit();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Update check did not complete; it will be tried again next start")]
    private static partial void LogCheckFailed(ILogger logger, Exception ex);
}

public sealed class VelopackUpdateClient : IUpdateClient
{
    private readonly UpdateManager _manager;
    private UpdateInfo? _pending;

    public VelopackUpdateClient(string repositoryUrl) =>
        _manager = new UpdateManager(new GithubSource(repositoryUrl, accessToken: null, prerelease: false));

    public bool IsInstalled => _manager.IsInstalled;

    public async Task<string?> CheckAndDownloadAsync(CancellationToken cancellationToken)
    {
        var found = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (found is null)
        {
            return null;
        }

        await _manager.DownloadUpdatesAsync(found, cancelToken: cancellationToken).ConfigureAwait(false);
        _pending = found;
        return found.TargetFullRelease.Version.ToString();
    }

    public void ApplyAndRestart()
    {
        if (_pending is not null)
        {
            _manager.ApplyUpdatesAndRestart(_pending);
        }
    }

    public void ApplyOnExit()
    {
        if (_pending is not null)
        {
            _manager.WaitExitThenApplyUpdates(_pending, silent: true, restart: false);
        }
    }
}

using PassTheStick.Core;
using PassTheStick.Protocol;
using PassTheStick.Windows;

namespace PassTheStick.App.Services;

public enum SessionPhase
{
    Idle,
    Starting,
    Live,
    Reconnecting,
    Failed,
}

public sealed record PlayerSnapshot(string Id, string Name, int Slot, bool Away, bool HasStick);

/// <summary>What a guest in test mode is pressing. <see cref="Active"/> false means they just left test mode.</summary>
public sealed record ProbeInfo(string GuestName, bool Active, PadState Pad, IReadOnlyCollection<string> Keys);

public sealed record GameStatus(bool IsPinned, string Title, bool IsForeground, bool NeedsElevation, bool ExclusiveFullscreen = false);

/// <summary>Everything the main view model needs from a running session. Faked in tests.</summary>
public interface IHostSession : IAsyncDisposable
{
    event Action<SessionPhase, string?>? PhaseChanged;

    event Action<string>? RoomCreated;

    event Action? PlayersChanged;

    event Action<ProbeInfo>? ProbeChanged;

    /// <summary>A guest asked for the stick; the argument is their name.</summary>
    event Action<string>? StickRequested;

    /// <summary>Why guests cannot have virtual controllers, or null when they can.</summary>
    string? PadProblem { get; }

    IReadOnlyList<PlayerSnapshot> Players { get; }

    bool HostHoldsStick { get; }

    GameStatus Game { get; }

    Task StartAsync(Uri server, CancellationToken cancellationToken);

    Task StopAsync();

    bool PassTo(string playerId);

    bool TakeBack();

    /// <summary>Approves a waiting request, or passes to the next player in the rotation.</summary>
    PassOutcome PassNext();

    TurnMode Mode { get; }

    void SetMode(TurnMode mode);

    /// <summary>Seconds each guest keeps the stick; 0 means no limit.</summary>
    int TurnSeconds { get; set; }

    /// <summary>Seconds left on the current guest's turn, or null when nothing is timed.</summary>
    int? TurnSecondsLeft { get; }

    /// <summary>Position and size of the pinned game window in screen pixels, when it is visible.</summary>
    bool TryGetGameRect(out (int Left, int Top, int Width, int Height) rect);

    /// <summary>Why virtual controllers cannot be created right now, or null if the driver is ready.</summary>
    string? CheckPadDriver();

    IReadOnlyList<WindowInfo> ListWindows();

    void PinWindow(nint handle);
}

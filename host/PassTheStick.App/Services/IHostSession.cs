using PassTheStick.Core;
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

public sealed record GameStatus(bool IsPinned, string Title, bool IsForeground, bool NeedsElevation);

/// <summary>Everything the main view model needs from a running session. Faked in tests.</summary>
public interface IHostSession : IAsyncDisposable
{
    event Action<SessionPhase, string?>? PhaseChanged;

    event Action<string>? RoomCreated;

    event Action? PlayersChanged;

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

    /// <summary>Position and size of the pinned game window in screen pixels, when it is visible.</summary>
    bool TryGetGameRect(out (int Left, int Top, int Width, int Height) rect);

    IReadOnlyList<WindowInfo> ListWindows();

    void PinWindow(nint handle);
}

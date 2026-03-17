using PassTheStick.Shared;

namespace PassTheStick.Host;

/// <summary>
/// Player list and who has the stick. Reclaims stick to host when active guest disconnects.
/// </summary>
public sealed class SessionManager
{
    private string? _activePlayerId;
    private List<PlayerInfo> _players = new();

    public string? LocalPlayerId { get; set; }
    public string? ActivePlayerId { get => _activePlayerId; set => _activePlayerId = value; }

    public IReadOnlyList<PlayerInfo> Players => _players;

    /// <summary>True when the local host has the stick.</summary>
    public bool IsLocalPlayerActive =>
        string.IsNullOrEmpty(_activePlayerId) || _activePlayerId == LocalPlayerId;

    public void SetActivePlayer(string? playerId)
    {
        _activePlayerId = playerId;
    }

    /// <summary>Update player list from relay. If active player left, reclaim stick to host.</summary>
    public void UpdatePlayers(IEnumerable<PlayerInfo> players)
    {
        _players = players.ToList();
        var ids = _players.Select(p => p.Id).ToHashSet();
        if (!string.IsNullOrEmpty(_activePlayerId) && !ids.Contains(_activePlayerId))
            _activePlayerId = LocalPlayerId; // guest disconnected, reclaim
    }
}

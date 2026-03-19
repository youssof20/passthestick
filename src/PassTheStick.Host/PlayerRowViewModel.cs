using PassTheStick.Shared;

namespace PassTheStick.Host;

/// <summary>Row for host player list (template binding + active highlight).</summary>
public sealed class PlayerRowViewModel
{
    public PlayerRowViewModel(PlayerInfo player, bool isActive)
    {
        Player = player;
        IsActive = isActive;
    }

    public PlayerInfo Player { get; }
    public bool IsActive { get; }

    public string DisplayName => Player.Name;
}

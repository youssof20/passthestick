namespace PassTheStick.Core;

public enum HolderChangeReason
{
    HostPassed,
    HostTookBack,
    HolderLeft,
    GuestRequestApproved,
    Reset,
    ModeChanged,
}

/// <summary>Whether players take turns holding the stick, or everyone plays at once.</summary>
public enum TurnMode
{
    OneAtATime,
    Everyone,
}

public sealed record Player(string Id, string Name, int Slot);

public sealed record HolderChange(string? Previous, string Current, uint Epoch, HolderChangeReason Reason);

/// <summary>
/// The host is the authority on who holds the stick. Every change bumps the epoch so late frames from
/// a previous holder can be recognised and dropped. Pure logic: no I/O, no Windows types.
/// </summary>
public sealed class StickSession
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Player> _players = new(StringComparer.Ordinal);
    private readonly int _maxPlayers;
    private string _holder;
    private uint _epoch;
    private TurnMode _mode;

    public StickSession(string hostId, int maxPlayers = 8)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPlayers, 1);
        HostId = hostId;
        _maxPlayers = maxPlayers;
        _holder = hostId;
    }

    public event Action<HolderChange>? HolderChanged;

    public event Action<Player>? StickRequested;

    /// <summary>A guest left the room (raised after any holder change they caused).</summary>
    public event Action<string>? PlayerRemoved;

    public string HostId { get; }

    public TurnMode Mode
    {
        get
        {
            lock (_gate)
            {
                return _mode;
            }
        }
    }

    /// <summary>
    /// Whether this guest's input may be applied right now: the holder in one-at-a-time mode, any guest in
    /// everyone-at-once mode. The host's own id never qualifies, since the host plays on the real keyboard.
    /// </summary>
    public bool MaySend(string id)
    {
        lock (_gate)
        {
            return id != HostId && (_mode == TurnMode.Everyone ? _players.ContainsKey(id) : _holder == id);
        }
    }

    /// <summary>Switches mode. The epoch changes and everything held is released, so nothing carries over.</summary>
    public bool SetMode(TurnMode mode)
    {
        HolderChange change;
        lock (_gate)
        {
            if (_mode == mode)
            {
                return false;
            }

            _mode = mode;
            change = SetHolderLocked(HostId, HolderChangeReason.ModeChanged);
        }

        Raise(change);
        return true;
    }

    public string Holder
    {
        get
        {
            lock (_gate)
            {
                return _holder;
            }
        }
    }

    public uint Epoch
    {
        get
        {
            lock (_gate)
            {
                return _epoch;
            }
        }
    }

    public bool HostHoldsStick => Holder == HostId;

    public IReadOnlyList<Player> Players
    {
        get
        {
            lock (_gate)
            {
                return [.. _players.Values.OrderBy(p => p.Slot)];
            }
        }
    }

    /// <summary>Adds a guest in the lowest free slot. Returns null when the room is full or the id is taken.</summary>
    public Player? AddPlayer(string id, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_gate)
        {
            if (id == HostId || _players.ContainsKey(id) || _players.Count >= _maxPlayers)
            {
                return null;
            }

            var slot = 0;
            while (_players.Values.Any(p => p.Slot == slot))
            {
                slot++;
            }

            var player = new Player(id, name, slot);
            _players[id] = player;
            return player;
        }
    }

    /// <summary>Removes a guest. If they held the stick it goes back to the host.</summary>
    public bool RemovePlayer(string id)
    {
        HolderChange? change = null;
        bool removed;
        lock (_gate)
        {
            removed = _players.Remove(id);
            if (removed && _holder == id)
            {
                change = SetHolderLocked(HostId, HolderChangeReason.HolderLeft);
            }
        }

        Raise(change);
        if (removed)
        {
            PlayerRemoved?.Invoke(id);
        }

        return removed;
    }

    /// <summary>Host hands the stick to a guest. False when the target is not in the room or already holds it.</summary>
    public bool PassTo(string id)
    {
        HolderChange? change;
        lock (_gate)
        {
            if (!_players.ContainsKey(id) || _holder == id)
            {
                return false;
            }

            change = SetHolderLocked(id, HolderChangeReason.HostPassed);
        }

        Raise(change);
        return true;
    }

    public bool TakeBack()
    {
        HolderChange? change;
        lock (_gate)
        {
            if (_holder == HostId)
            {
                return false;
            }

            change = SetHolderLocked(HostId, HolderChangeReason.HostTookBack);
        }

        Raise(change);
        return true;
    }

    /// <summary>A guest asks for the stick. Nothing changes until the host approves.</summary>
    public bool RequestStick(string id)
    {
        Player? player;
        lock (_gate)
        {
            if (!_players.TryGetValue(id, out player) || _holder == id)
            {
                return false;
            }
        }

        StickRequested?.Invoke(player);
        return true;
    }

    /// <summary>
    /// For a host that has just taken over an existing room: moves the epoch past whatever the room last saw and gives the
    /// stick to the host, so the room and its guests stop acting on a holder this session never knew about.
    /// </summary>
    public void ResumeAt(uint roomEpoch)
    {
        HolderChange change;
        lock (_gate)
        {
            if (roomEpoch > _epoch)
            {
                _epoch = roomEpoch;
            }

            change = SetHolderLocked(HostId, HolderChangeReason.Reset);
        }

        Raise(change);
    }

    /// <summary>Forces the stick back to the host and bumps the epoch, even if the host already holds it.</summary>
    public void Reset()
    {
        HolderChange change;
        lock (_gate)
        {
            change = SetHolderLocked(HostId, HolderChangeReason.Reset);
        }

        Raise(change);
    }

    public bool IsHolder(string id, uint epoch)
    {
        lock (_gate)
        {
            return MaySendLocked(id) && _epoch == epoch;
        }
    }

    private bool MaySendLocked(string id) => id != HostId && (_mode == TurnMode.Everyone ? _players.ContainsKey(id) : _holder == id);

    private HolderChange SetHolderLocked(string next, HolderChangeReason reason)
    {
        var previous = _holder;
        _holder = next;
        _epoch++;
        return new HolderChange(previous, next, _epoch, reason);
    }

    private void Raise(HolderChange? change)
    {
        if (change is not null)
        {
            HolderChanged?.Invoke(change);
        }
    }
}

namespace PassTheStick.Core;

/// <summary>
/// An optional limit on how long a guest keeps the stick. When it runs out the stick moves to the next player in the
/// rotation. The host's own turns are never timed, and nothing is timed in everyone-at-once mode.
/// </summary>
public sealed class TurnTimer : IDisposable
{
    private readonly StickSession _session;
    private readonly PassCoordinator _pass;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private long _turnStartedAt;
    private TimeSpan _limit;

    public TurnTimer(StickSession session, PassCoordinator pass, TimeProvider? clock = null)
    {
        _session = session;
        _pass = pass;
        _clock = clock ?? TimeProvider.System;
        _turnStartedAt = _clock.GetTimestamp();
        _session.HolderChanged += OnHolderChanged;
    }

    /// <summary>Zero means no limit.</summary>
    public TimeSpan Limit
    {
        get
        {
            lock (_gate)
            {
                return _limit;
            }
        }

        set
        {
            lock (_gate)
            {
                _limit = value < TimeSpan.Zero ? TimeSpan.Zero : value;
                _turnStartedAt = _clock.GetTimestamp(); // changing the limit gives the current player a fresh turn
            }
        }
    }

    /// <summary>Time left on the current guest's turn, or null when nothing is being timed.</summary>
    public TimeSpan? Remaining
    {
        get
        {
            lock (_gate)
            {
                return IsTimedLocked() ? _limit - _clock.GetElapsedTime(_turnStartedAt) : null;
            }
        }
    }

    /// <summary>Call about once a second. Passes the stick when the turn has run out.</summary>
    public void Tick()
    {
        bool expired;
        lock (_gate)
        {
            expired = IsTimedLocked() && _clock.GetElapsedTime(_turnStartedAt) >= _limit;
        }

        if (expired)
        {
            _pass.Pass(); // the resulting holder change restarts the clock
        }
    }

    public void Dispose() => _session.HolderChanged -= OnHolderChanged;

    private bool IsTimedLocked() => _limit > TimeSpan.Zero && _session.Mode == TurnMode.OneAtATime && !_session.HostHoldsStick;

    private void OnHolderChanged(HolderChange change)
    {
        lock (_gate)
        {
            _turnStartedAt = _clock.GetTimestamp();
        }
    }
}

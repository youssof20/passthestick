namespace PassTheStick.Core;

public enum PassOutcome
{
    /// <summary>The stick went to a guest who had asked for it.</summary>
    ApprovedRequest,

    /// <summary>The stick went to the next player in the rotation.</summary>
    PassedToNext,

    /// <summary>There is nobody to pass to.</summary>
    NoOneToPass,
}

/// <summary>
/// One hotkey, two jobs. If a guest has asked for the stick, the pass key approves that request; otherwise it
/// hands the stick to the next player in the rotation (host, then guests in slot order, then the host again).
/// That covers "pass on death" and "request the stick, approve with one key" without a second binding.
/// </summary>
public sealed class PassCoordinator : IDisposable
{
    private readonly StickSession _session;
    private readonly Func<string, bool> _isAway;
    private readonly Lock _gate = new();
    private string? _requester;

    public PassCoordinator(StickSession session, Func<string, bool>? isAway = null)
    {
        _session = session;
        _isAway = isAway ?? (_ => false);
        _session.StickRequested += OnRequested;
        _session.HolderChanged += OnHolderChanged;
    }

    /// <summary>The guest waiting for approval, if any.</summary>
    public Player? PendingRequest
    {
        get
        {
            lock (_gate)
            {
                return _requester is null ? null : _session.Players.FirstOrDefault(p => p.Id == _requester);
            }
        }
    }

    public PassOutcome Pass()
    {
        if (PendingRequest is { } asking && !_isAway(asking.Id) && _session.PassTo(asking.Id))
        {
            return PassOutcome.ApprovedRequest;
        }

        return PassToNext() ? PassOutcome.PassedToNext : PassOutcome.NoOneToPass;
    }

    public void Dispose()
    {
        _session.StickRequested -= OnRequested;
        _session.HolderChanged -= OnHolderChanged;
    }

    /// <summary>Rotation order is the host, then guests by slot. Away guests are skipped.</summary>
    private bool PassToNext()
    {
        var order = new List<string> { _session.HostId };
        order.AddRange(_session.Players.Where(p => !_isAway(p.Id)).Select(p => p.Id));
        if (order.Count < 2)
        {
            return false;
        }

        var at = order.IndexOf(_session.Holder);
        var next = order[(at + 1) % order.Count];
        return next == _session.HostId ? _session.TakeBack() : _session.PassTo(next);
    }

    private void OnRequested(Player player)
    {
        lock (_gate)
        {
            _requester = player.Id;
        }
    }

    private void OnHolderChanged(HolderChange change)
    {
        lock (_gate)
        {
            if (_requester == change.Current || _session.Players.All(p => p.Id != _requester))
            {
                _requester = null;
            }
        }
    }
}

using PassTheStick.Platform;
using PassTheStick.Protocol;

namespace PassTheStick.Core;

public enum InputResult
{
    Accepted,
    NotHolder,
    StaleEpoch,
    UnknownKey,
    StaleSequence,
    Paused,
}

/// <summary>
/// Validates guest input against the stick state and drives the injector and pad backend.
/// Invariants: only the current holder's input under the current epoch is injected, and every held key
/// and button is released on any holder change, pause, timeout or shutdown.
/// All members are safe to call from any thread; the injector and pad are only touched under one lock.
/// </summary>
public sealed class InputPipeline : IDisposable
{
    private readonly object _gate = new();
    private readonly StickSession _session;
    private readonly IInputInjector _injector;
    private readonly IVirtualPadBackend _pad;
    private readonly TimeProvider _clock;
    private readonly HashSet<ScanKey> _heldKeys = [];
    private readonly HashSet<int> _padSlots = [];
    private bool _paused;
    private ushort _lastPadSeq;
    private bool _haveSeq;
    private long _lastFrameTicks;
    private bool _disposed;

    public InputPipeline(StickSession session, IInputInjector injector, IVirtualPadBackend pad, TimeProvider? clock = null)
    {
        _session = session;
        _injector = injector;
        _pad = pad;
        _clock = clock ?? TimeProvider.System;
        _session.HolderChanged += OnHolderChanged;
    }

    /// <summary>True while the pinned game is not the foreground window. Input is dropped and everything is released.</summary>
    public bool Paused
    {
        get
        {
            lock (_gate)
            {
                return _paused;
            }
        }
    }

    public void SetPaused(bool paused)
    {
        lock (_gate)
        {
            if (_paused == paused)
            {
                return;
            }

            _paused = paused;
            if (paused)
            {
                ReleaseAllLocked();
            }
        }
    }

    public InputResult HandleKey(string fromId, uint epoch, string? code, bool down)
    {
        lock (_gate)
        {
            var gate = CheckLocked(fromId, epoch);
            if (gate != InputResult.Accepted)
            {
                return gate;
            }

            if (!KeyMap.TryResolve(code, out var key))
            {
                return InputResult.UnknownKey;
            }

            Touch();
            if (down)
            {
                if (_heldKeys.Add(key))
                {
                    _injector.KeyDown(key);
                }
            }
            else if (_heldKeys.Remove(key))
            {
                _injector.KeyUp(key);
            }

            return InputResult.Accepted;
        }
    }

    public InputResult HandlePad(string fromId, uint epoch, ushort seq, in PadState state)
    {
        lock (_gate)
        {
            var gate = CheckLocked(fromId, epoch);
            if (gate != InputResult.Accepted)
            {
                return gate;
            }

            if (_haveSeq && !SerialNumber.IsNewer(seq, _lastPadSeq))
            {
                return InputResult.StaleSequence;
            }

            _haveSeq = true;
            _lastPadSeq = seq;
            Touch();
            var slot = SlotOf(fromId);
            _pad.Submit(slot, state);
            if (state == PadState.Neutral)
            {
                _padSlots.Remove(slot);
            }
            else
            {
                _padSlots.Add(slot);
            }

            return InputResult.Accepted;
        }
    }

    /// <summary>Keepalives and other state frames from the holder prove the link is alive.</summary>
    public InputResult HandleKeepalive(string fromId, uint epoch)
    {
        lock (_gate)
        {
            var gate = CheckLocked(fromId, epoch);
            if (gate == InputResult.Accepted)
            {
                Touch();
            }

            return gate;
        }
    }

    /// <summary>Call every few milliseconds. Releases everything when the holder has gone quiet for too long.</summary>
    public void Tick()
    {
        lock (_gate)
        {
            if (_disposed || (_heldKeys.Count == 0 && _padSlots.Count == 0))
            {
                return;
            }

            var elapsed = _clock.GetElapsedTime(_lastFrameTicks);
            if (elapsed > ProtocolInfo.StateTimeout)
            {
                ReleaseAllLocked();
            }
        }
    }

    public void ReleaseAll()
    {
        lock (_gate)
        {
            ReleaseAllLocked();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _session.HolderChanged -= OnHolderChanged;
            ReleaseAllLocked();
        }
    }

    private InputResult CheckLocked(string fromId, uint epoch)
    {
        if (_disposed || _paused)
        {
            return InputResult.Paused;
        }

        if (fromId == _session.HostId || _session.Holder != fromId)
        {
            return InputResult.NotHolder;
        }

        return _session.Epoch == epoch ? InputResult.Accepted : InputResult.StaleEpoch;
    }

    private int SlotOf(string id) => _session.Players.FirstOrDefault(p => p.Id == id)?.Slot ?? 0;

    private void Touch() => _lastFrameTicks = _clock.GetTimestamp();

    private void OnHolderChanged(HolderChange change)
    {
        lock (_gate)
        {
            ReleaseAllLocked();
            _haveSeq = false;
        }
    }

    private void ReleaseAllLocked()
    {
        foreach (var key in _heldKeys)
        {
            _injector.KeyUp(key);
        }

        _heldKeys.Clear();
        foreach (var slot in _padSlots)
        {
            _pad.Neutralize(slot);
        }

        _padSlots.Clear();
    }
}

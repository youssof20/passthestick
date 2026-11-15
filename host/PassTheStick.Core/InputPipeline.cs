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

    /// <summary>Everyone-at-once mode: the keyboard belongs to the first player only.</summary>
    NotKeyboardOwner,

    /// <summary>Everyone-at-once mode: Windows games see four pads at most.</summary>
    NoPadSlot,
}

/// <summary>
/// Validates guest input against the session and drives the injector and pad backend.
/// Invariants: input is applied only for players the session allows to send (the holder in one-at-a-time mode, every
/// player in everyone-at-once mode) under the current epoch, and every held key and button is released on any holder
/// or mode change, when the player leaves, on pause, when that player goes quiet, and on shutdown.
/// All members are safe to call from any thread; the injector and pad are only touched under one lock.
/// </summary>
public sealed class InputPipeline : IDisposable
{
    public const int MaxPads = 4;

    private readonly object _gate = new();
    private readonly StickSession _session;
    private readonly IInputInjector _injector;
    private readonly IVirtualPadBackend _pad;
    private readonly TimeProvider _clock;
    private readonly Func<bool> _isGameForeground;
    private readonly HashSet<ScanKey> _heldKeys = [];
    private readonly Dictionary<int, string> _padOwner = [];
    private readonly Dictionary<string, (ushort Seq, bool Have)> _padSeq = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastSeen = new(StringComparer.Ordinal);
    private readonly List<string> _expired = [];
    private string? _keyOwner;
    private bool _paused;
    private bool _disposed;

    public InputPipeline(StickSession session, IInputInjector injector, IVirtualPadBackend pad, TimeProvider? clock = null, Func<bool>? isGameForeground = null)
    {
        _isGameForeground = isGameForeground ?? (() => true);
        _session = session;
        _injector = injector;
        _pad = pad;
        _clock = clock ?? TimeProvider.System;
        _session.HolderChanged += OnHolderChanged;
        _session.PlayerRemoved += OnPlayerRemoved;
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

            if (_session.Mode == TurnMode.Everyone)
            {
                var players = _session.Players;
                if (players.Count == 0 || players[0].Id != fromId)
                {
                    return InputResult.NotKeyboardOwner;
                }
            }

            if (!KeyMap.TryResolve(code, out var key))
            {
                return InputResult.UnknownKey;
            }

            if (down && !_isGameForeground())
            {
                // The pump only checks focus every few milliseconds; a burst of keys must not outrun it.
                _paused = true;
                ReleaseAllLocked();
                return InputResult.Paused;
            }

            if (down && LeavesGame(key))
            {
                return InputResult.UnknownKey;
            }

            Touch(fromId);
            if (down)
            {
                _keyOwner = fromId;
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

    private static bool IsCtrl(ScanKey k) => k.ScanCode == 0x1D;

    private static bool IsAlt(ScanKey k) => k.ScanCode == 0x38;

    /// <summary>Escape, Tab, Space, F4 and Delete: the second key of the chords that open Start, switch windows or close them.</summary>
    private static bool IsChordTarget(ScanKey k) => k.ScanCode is 0x01 or 0x0F or 0x39 or 0x3E or 0x53;

    /// <summary>
    /// A guest may act inside the game window only. Ctrl+Esc, Ctrl+Shift+Esc, Alt+Tab, Alt+Esc, Alt+Space, Alt+F4 and
    /// Ctrl+Alt+Delete reach Start, Task Manager, the window switcher or the system menu, whichever order the keys arrive in.
    /// </summary>
    private bool LeavesGame(ScanKey key)
    {
        var ctrl = IsCtrl(key) || _heldKeys.Any(IsCtrl);
        var alt = IsAlt(key) || _heldKeys.Any(IsAlt);
        if (!ctrl && !alt)
        {
            return false;
        }

        if (IsCtrl(key) || IsAlt(key))
        {
            return _heldKeys.Any(IsChordTarget) || (IsAlt(key) && _heldKeys.Any(IsCtrl)) || (IsCtrl(key) && _heldKeys.Any(IsAlt));
        }

        if (!IsChordTarget(key))
        {
            return false;
        }

        return key.ScanCode == 0x53 ? ctrl && alt : alt || key.ScanCode == 0x01;
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

            var slot = PadSlotFor(fromId);
            if (slot is null)
            {
                return InputResult.NoPadSlot;
            }

            if (_padSeq.TryGetValue(fromId, out var last) && last.Have && !SerialNumber.IsNewer(seq, last.Seq))
            {
                return InputResult.StaleSequence;
            }

            _padSeq[fromId] = (seq, true);
            Touch(fromId);
            if (state == PadState.Neutral && !_padOwner.ContainsKey(slot.Value))
            {
                // A resent neutral snapshot from someone with no pad pressed: nothing to apply, and it must not
                // create a virtual controller for a keyboard-only player.
                return InputResult.Accepted;
            }

            _pad.Submit(slot.Value, state);
            if (state == PadState.Neutral)
            {
                _padOwner.Remove(slot.Value);
            }
            else
            {
                _padOwner[slot.Value] = fromId;
            }

            return InputResult.Accepted;
        }
    }

    /// <summary>Keepalives and other state frames from a sender prove its link is alive.</summary>
    public InputResult HandleKeepalive(string fromId, uint epoch)
    {
        lock (_gate)
        {
            var gate = CheckLocked(fromId, epoch);
            if (gate == InputResult.Accepted)
            {
                Touch(fromId);
            }

            return gate;
        }
    }

    /// <summary>Call every few milliseconds. Releases what a player was holding once that player has gone quiet for too long.</summary>
    public void Tick()
    {
        lock (_gate)
        {
            if (_disposed || (_heldKeys.Count == 0 && _padOwner.Count == 0))
            {
                return;
            }

            var now = _clock.GetTimestamp();
            _expired.Clear();
            foreach (var (id, seen) in _lastSeen)
            {
                if (_clock.GetElapsedTime(seen, now) > ProtocolInfo.StateTimeout)
                {
                    _expired.Add(id);
                }
            }

            foreach (var id in _expired)
            {
                ReleasePlayerLocked(id);
                _lastSeen.Remove(id);
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
            _session.PlayerRemoved -= OnPlayerRemoved;
            ReleaseAllLocked();
        }
    }

    private InputResult CheckLocked(string fromId, uint epoch)
    {
        if (_disposed || _paused)
        {
            return InputResult.Paused;
        }

        if (!_session.MaySend(fromId))
        {
            return InputResult.NotHolder;
        }

        return _session.Epoch == epoch ? InputResult.Accepted : InputResult.StaleEpoch;
    }

    /// <summary>
    /// One at a time: whoever holds the stick is player 1, so the game always sees pad 1.
    /// Everyone at once: each player's own slot, and none beyond the four pads Windows games can read.
    /// </summary>
    private int? PadSlotFor(string id)
    {
        if (_session.Mode == TurnMode.OneAtATime)
        {
            return 0;
        }

        var slot = _session.Players.FirstOrDefault(p => p.Id == id)?.Slot ?? int.MaxValue;
        return slot < MaxPads ? slot : null;
    }

    private void Touch(string id) => _lastSeen[id] = _clock.GetTimestamp();

    private void OnHolderChanged(HolderChange change)
    {
        lock (_gate)
        {
            ReleaseAllLocked();
            _padSeq.Clear();
            _lastSeen.Clear();
        }
    }

    private void OnPlayerRemoved(string id)
    {
        lock (_gate)
        {
            ReleasePlayerLocked(id);
            _padSeq.Remove(id);
            _lastSeen.Remove(id);
        }
    }

    private void ReleasePlayerLocked(string id)
    {
        if (_keyOwner == id)
        {
            foreach (var key in _heldKeys)
            {
                _injector.KeyUp(key);
            }

            _heldKeys.Clear();
            _keyOwner = null;
        }

        foreach (var slot in _padOwner.Where(kv => kv.Value == id).Select(kv => kv.Key).ToList())
        {
            _pad.Neutralize(slot);
            _padOwner.Remove(slot);
        }
    }

    private void ReleaseAllLocked()
    {
        foreach (var key in _heldKeys)
        {
            _injector.KeyUp(key);
        }

        _heldKeys.Clear();
        _keyOwner = null;
        foreach (var slot in _padOwner.Keys)
        {
            _pad.Neutralize(slot);
        }

        _padOwner.Clear();
    }
}

using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PassTheStick.Core;
using PassTheStick.Protocol;

namespace PassTheStick.Net;

/// <summary>
/// Glue between the room, the WebRTC links and the stick session. The host is the authority: this class
/// mirrors the room's player list into <see cref="StickSession"/>, pushes every holder change to the room
/// and to guests, and feeds guest frames to <see cref="InputPipeline"/>, which does the holder and epoch checks.
/// </summary>
/// <summary>What a guest in test mode is sending right now. Never injected; only shown to the host and echoed back to the guest.</summary>
public sealed record ProbeSnapshot(string GuestId, PadState Pad, IReadOnlyCollection<string> Keys);

public sealed partial class HostSessionController : IDisposable
{
    private static readonly TimeSpan EchoInterval = TimeSpan.FromMilliseconds(100);

    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _probeGate = new();
    private readonly Dictionary<string, ProbeState> _testing = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    private readonly StickSession _session;
    private readonly InputPipeline _pipeline;
    private readonly IRoomLink _room;
    private readonly IPeerLink _peers;
    private readonly ILogger _log;

    public HostSessionController(StickSession session, InputPipeline pipeline, IRoomLink room, IPeerLink peers, ILogger log, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _session = session;
        _pipeline = pipeline;
        _room = room;
        _peers = peers;
        _log = log;
        _room.Received += OnRoomEvent;
        _peers.StateReceived += OnState;
        _peers.ControlReceived += OnControl;
        _session.HolderChanged += OnHolderChanged;
        _peers.StatusChanged += OnPeerStatus;
    }

    /// <summary>Motion from the current holder, for the DSU server. Never raised for anyone else.</summary>
    public event Action<string, MotionState>? MotionReceived;

    /// <summary>A guest in test mode changed what they are pressing. Raised for holders and non-holders alike.</summary>
    public event Action<ProbeSnapshot>? ProbeChanged;

    /// <summary>A guest turned test mode on or off (argument: guest id, then on).</summary>
    public event Action<string, bool>? TestModeChanged;

    /// <summary>Mouse state from the current holder.</summary>
    public event Action<string, MouseState>? MouseReceived;

    public void Dispose()
    {
        _shutdown.Cancel();
        _room.Received -= OnRoomEvent;
        _peers.StateReceived -= OnState;
        _peers.ControlReceived -= OnControl;
        _session.HolderChanged -= OnHolderChanged;
        _peers.StatusChanged -= OnPeerStatus;
        _shutdown.Dispose();
    }

    private void OnRoomEvent(RoomEvent e)
    {
        switch (e)
        {
            case PresenceEvent presence:
                if (_syncPending)
                {
                    _syncPending = false;
                    SyncWithRoom(presence);
                }

                Reconcile(presence);
                break;
            case SignalEvent signal:
                _peers.HandleSignal(signal.From, signal.Payload);
                break;
            case RequestEvent request:
                _session.RequestStick(request.From);
                break;
            case RelayFrameEvent relay:
                HandleFrame(relay.From, relay.Payload);
                break;
            case ConnectionEvent { State: RoomConnectionState.Reconnecting }:
                _syncPending = true;
                _outageStartedAt ??= _clock.GetTimestamp();
                break;
            case ConnectionEvent { State: RoomConnectionState.Open }:
                OnRoomOpen();
                break;
            case EndedEvent:
                _pipeline.ReleaseAll();
                break;
            default:
                break;
        }
    }

    private void Reconcile(PresenceEvent presence)
    {
        var inRoom = presence.Players.ToDictionary(p => p.Id, StringComparer.Ordinal);

        foreach (var known in _session.Players)
        {
            if (!inRoom.ContainsKey(known.Id))
            {
                _session.RemovePlayer(known.Id);
                _peers.RemovePeer(known.Id);
                lock (_health)
                {
                    _health.Remove(known.Id);
                }

                SetTesting(known.Id, false);
            }
        }

        var existing = _session.Players.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var player in presence.Players)
        {
            if (!existing.Contains(player.Id))
            {
                if (_session.AddPlayer(player.Id, player.Name) is null)
                {
                    FireAndLog(_room.KickAsync(player.Id, _shutdown.Token));
                    continue;
                }
            }

            if (!player.Away)
            {
                lock (_health)
                {
                    _health.TryAdd(player.Id, new PeerHealth { Since = _clock.GetTimestamp() });
                }

                _peers.EnsurePeer(player.Id);
            }
        }
    }

    // ---- keeping the WebRTC links alive ----

    private static readonly TimeSpan DisconnectedGrace = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ConnectingPatience = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan OutageThatKillsLinks = TimeSpan.FromSeconds(5);
    private const int MaxReoffers = 6;

    private readonly Dictionary<string, PeerHealth> _health = new(StringComparer.Ordinal);
    private long? _outageStartedAt;
    private bool _reoffering;
    private bool _syncPending = true;

    private sealed class PeerHealth
    {
        public PeerStatus Status { get; set; } = PeerStatus.Connecting;

        public long Since { get; set; }

        public int Attempts { get; set; }
    }

    private void OnPeerStatus(string guestId, PeerStatus status)
    {
        if (_reoffering)
        {
            return; // our own teardown, not news
        }

        lock (_health)
        {
            if (!_health.TryGetValue(guestId, out var h))
            {
                _health[guestId] = h = new PeerHealth();
            }

            if (h.Status != status)
            {
                h.Status = status;
                h.Since = _clock.GetTimestamp();
            }

            if (status == PeerStatus.Open)
            {
                h.Attempts = 0;
            }
        }
    }

    /// <summary>
    /// Starts a fresh offer to any guest whose link failed, stayed disconnected, or never connected, with growing delays and
    /// a cap. Call this regularly from the input loop.
    /// </summary>
    public void Maintain()
    {
        List<string> stale = [];
        var now = _clock.GetTimestamp();
        lock (_health)
        {
            foreach (var (id, h) in _health)
            {
                var waited = _clock.GetElapsedTime(h.Since, now);
                var due = h.Status switch
                {
                    PeerStatus.Failed or PeerStatus.Closed => waited >= TimeSpan.FromSeconds(Math.Min(8, 1 << Math.Min(h.Attempts, 3))),
                    PeerStatus.Disconnected => waited >= DisconnectedGrace,
                    PeerStatus.Connecting => waited >= ConnectingPatience,
                    _ => false,
                };
                if (due && h.Attempts < MaxReoffers && _session.Players.Any(p => p.Id == id))
                {
                    h.Attempts++;
                    h.Status = PeerStatus.Connecting;
                    h.Since = now;
                    stale.Add(id);
                }
            }
        }

        foreach (var id in stale)
        {
            Reoffer(id);
        }
    }

    private void Reoffer(string guestId)
    {
        _reoffering = true;
        try
        {
            _peers.RemovePeer(guestId);
            _peers.EnsurePeer(guestId);
        }
        finally
        {
            _reoffering = false;
        }
    }

    /// <summary>After a long enough outage the old links are dead, so every guest gets a new offer.</summary>
    private void OnRoomOpen()
    {
        var started = _outageStartedAt;
        _outageStartedAt = null;
        if (started is null || _clock.GetElapsedTime(started.Value) < OutageThatKillsLinks)
        {
            return;
        }

        foreach (var player in _session.Players)
        {
            lock (_health)
            {
                _health[player.Id] = new PeerHealth { Status = PeerStatus.Connecting, Since = _clock.GetTimestamp() };
            }

            Reoffer(player.Id);
        }
    }

    private void OnHolderChanged(HolderChange change) => Publish(change.Current, change.Epoch);

    private void Publish(string holder, uint epoch)
    {
        var wire = holder == _session.HostId ? null : holder;
        var everyone = _session.Mode == TurnMode.Everyone;
        FireAndLog(_room.SendHolderAsync(wire, epoch, everyone, _shutdown.Token));
        _peers.BroadcastControl(new JsonObject { ["t"] = "holder", ["id"] = wire ?? _session.HostId, ["epoch"] = epoch, ["mode"] = everyone ? "everyone" : "one" });
    }

    /// <summary>
    /// The first thing the room tells a newly attached host is its own idea of the stick. A fresh session knows nothing of
    /// an old holder and epoch (the app restarted, or a mode was chosen before the connection came up), so reconcile:
    /// move past the room's epoch if it is ahead, otherwise just tell the room what this session believes.
    /// </summary>
    private void SyncWithRoom(PresenceEvent room)
    {
        var holderHere = _session.HostHoldsStick ? "host" : _session.Holder;
        if (room.Epoch > _session.Epoch || (room.Holder != "host" && room.Holder != holderHere))
        {
            _session.ResumeAt(room.Epoch);
        }
        else if (room.Everyone != (_session.Mode == TurnMode.Everyone) || room.Holder != holderHere || room.Epoch != _session.Epoch)
        {
            Publish(_session.Holder, _session.Epoch);
        }
    }

    private void OnState(string from, byte[] data) => HandleFrame(from, data);

    private void HandleFrame(string from, byte[] data)
    {
        if (data.Length > 0 && data[0] == RelayKeyKind)
        {
            HandleRelayKey(from, data);
            return;
        }

        if (!StateFrameCodec.TryDecode(data, out var frame))
        {
            return;
        }

        if (frame.Kind == StateKind.Pad)
        {
            UpdateProbe(from, probe => probe.Pad = frame.Pad);
        }

        // State frames carry no epoch (see docs/protocol.md): the pipeline checks the sender is the holder.
        var epoch = _session.Epoch;
        switch (frame.Kind)
        {
            case StateKind.Pad:
                _pipeline.HandlePad(from, epoch, frame.Seq, frame.Pad);
                break;
            case StateKind.Keepalive:
                _pipeline.HandleKeepalive(from, epoch);
                break;
            case StateKind.Motion:
                if (_pipeline.HandleKeepalive(from, epoch) == InputResult.Accepted)
                {
                    MotionReceived?.Invoke(from, frame.Motion);
                }

                break;
            case StateKind.Mouse:
                if (_pipeline.HandleKeepalive(from, epoch) == InputResult.Accepted)
                {
                    MouseReceived?.Invoke(from, frame.Mouse);
                }

                break;
            default:
                break;
        }
    }

    private const byte RelayKeyKind = 5;

    /// <summary>Relay-only key frame: [5][seq u16][down u8][len u8][ASCII code]. Relay frames only arrive from the holder.</summary>
    private void HandleRelayKey(string from, byte[] data)
    {
        if (data.Length < 5 || data.Length != 5 + data[4])
        {
            return;
        }

        var code = Encoding.ASCII.GetString(data, 5, data[4]);
        _pipeline.HandleKey(from, _session.Epoch, code, data[3] != 0);
    }

    private void OnControl(string from, JsonObject message)
    {
        switch ((string?)message["t"])
        {
            case "key":
                if ((string?)message["code"] is { } code && message["down"] is JsonValue down && down.TryGetValue(out bool isDown)
                    && message["epoch"] is JsonValue epochValue && epochValue.TryGetValue(out double epoch) && epoch is >= 0 and <= uint.MaxValue)
                {
                    if (code.Length <= 32)
                    {
                        UpdateProbe(from, probe =>
                        {
                            if (isDown)
                            {
                                probe.Keys.Add(code);
                            }
                            else
                            {
                                probe.Keys.Remove(code);
                            }
                        });
                    }

                    _pipeline.HandleKey(from, (uint)epoch, code, isDown);
                }

                break;
            case "test":
                SetTesting(from, message["on"] is JsonValue on && on.TryGetValue(out bool enabled) && enabled);
                break;
            case "request":
                _session.RequestStick(from);
                break;
            default:
                break;
        }
    }

    private void SetTesting(string guestId, bool on)
    {
        lock (_probeGate)
        {
            if (on)
            {
                if (_session.Players.All(p => p.Id != guestId) || _testing.ContainsKey(guestId))
                {
                    return;
                }

                _testing[guestId] = new ProbeState();
            }
            else if (!_testing.Remove(guestId))
            {
                return;
            }
        }

        TestModeChanged?.Invoke(guestId, on);
    }

    private void UpdateProbe(string guestId, Action<ProbeState> change)
    {
        ProbeSnapshot snapshot;
        lock (_probeGate)
        {
            if (!_testing.TryGetValue(guestId, out var state))
            {
                return;
            }

            change(state);
            snapshot = new ProbeSnapshot(guestId, state.Pad, [.. state.Keys]);

            // Echo what arrived back to the guest, at most ten times a second, but always when something was
            // released so a button never stays lit on their screen. Anything held back is sent by FlushEchoes.
            var now = _clock.GetTimestamp();
            var released = (state.LastEchoed.Buttons & ~state.Pad.Buttons) != 0 || state.LastKeys.Except(state.Keys).Any();
            if (state.Echoed && !released && _clock.GetElapsedTime(state.LastEchoAt, now) < EchoInterval)
            {
                state.Dirty = true;
            }
            else
            {
                Echo(guestId, state, now);
            }
        }

        ProbeChanged?.Invoke(snapshot);
    }

    /// <summary>Sends any echo that was held back by the rate limit. Call this regularly from the input loop.</summary>
    public void FlushEchoes()
    {
        lock (_probeGate)
        {
            if (_testing.Count == 0)
            {
                return;
            }

            var now = _clock.GetTimestamp();
            foreach (var (guestId, state) in _testing)
            {
                if (state.Dirty && _clock.GetElapsedTime(state.LastEchoAt, now) >= EchoInterval)
                {
                    Echo(guestId, state, now);
                }
            }
        }
    }

    private void Echo(string guestId, ProbeState state, long now)
    {
        state.Dirty = false;
        state.Echoed = true;
        state.LastEchoAt = now;
        state.LastEchoed = state.Pad;
        state.LastKeys = [.. state.Keys];
        _peers.SendControl(guestId, new JsonObject
        {
            ["t"] = "probe",
            ["buttons"] = (int)state.Pad.Buttons,
            ["lx"] = state.Pad.Lx,
            ["ly"] = state.Pad.Ly,
            ["rx"] = state.Pad.Rx,
            ["ry"] = state.Pad.Ry,
            ["lt"] = state.Pad.Lt,
            ["rt"] = state.Pad.Rt,
            ["keys"] = new JsonArray([.. state.Keys.Select(k => (JsonNode?)JsonValue.Create(k))]),
        });
    }

    private sealed class ProbeState
    {
        public PadState Pad { get; set; }

        public HashSet<string> Keys { get; } = [];

        public PadState LastEchoed { get; set; }

        public HashSet<string> LastKeys { get; set; } = [];

        public long LastEchoAt { get; set; }

        public bool Echoed { get; set; }

        public bool Dirty { get; set; }
    }

    private void FireAndLog(Task task)
    {
        _ = task.ContinueWith(
            t => LogSendFailed(_log, t.Exception),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not send to the room")]
    private static partial void LogSendFailed(ILogger logger, Exception? ex);
}

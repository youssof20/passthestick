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
        _room.Received -= OnRoomEvent;
        _peers.StateReceived -= OnState;
        _peers.ControlReceived -= OnControl;
        _session.HolderChanged -= OnHolderChanged;
    }

    private void OnRoomEvent(RoomEvent e)
    {
        switch (e)
        {
            case PresenceEvent presence:
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
                    FireAndLog(_room.KickAsync(player.Id, CancellationToken.None));
                    continue;
                }
            }

            if (!player.Away)
            {
                _peers.EnsurePeer(player.Id);
            }
        }
    }

    private void OnHolderChanged(HolderChange change)
    {
        var wire = change.Current == _session.HostId ? null : change.Current;
        FireAndLog(_room.SendHolderAsync(wire, change.Epoch, CancellationToken.None));
        _peers.BroadcastControl(new JsonObject { ["t"] = "holder", ["id"] = wire ?? _session.HostId, ["epoch"] = change.Epoch });
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

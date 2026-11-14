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
public sealed partial class HostSessionController : IDisposable
{
    private readonly StickSession _session;
    private readonly InputPipeline _pipeline;
    private readonly IRoomLink _room;
    private readonly IPeerLink _peers;
    private readonly ILogger _log;

    public HostSessionController(StickSession session, InputPipeline pipeline, IRoomLink room, IPeerLink peers, ILogger log)
    {
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
                    _pipeline.HandleKey(from, (uint)epoch, code, isDown);
                }

                break;
            case "request":
                _session.RequestStick(from);
                break;
            default:
                break;
        }
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

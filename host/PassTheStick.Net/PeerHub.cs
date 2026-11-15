using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using DataChannelDotnet;
using DataChannelDotnet.Bindings;
using DataChannelDotnet.Data;
using DataChannelDotnet.Impl;
using Microsoft.Extensions.Logging;

namespace PassTheStick.Net;

public enum PeerStatus
{
    Connecting,
    Open,

    /// <summary>The path went quiet. It can recover by itself, so the host waits before starting over.</summary>
    Disconnected,

    Failed,
    Closed,
}

/// <summary>Per-guest WebRTC links. Faked in tests.</summary>
public interface IPeerLink
{
    /// <summary>Raised with the guest id and one whole frame from that guest's unreliable `state` channel.</summary>
    event Action<string, byte[]>? StateReceived;

    /// <summary>Raised with the guest id and one JSON message from that guest's reliable `control` channel.</summary>
    event Action<string, JsonObject>? ControlReceived;

    event Action<string, PeerStatus>? StatusChanged;

    /// <summary>Starts connecting to a guest if no link exists. The host always makes the offer.</summary>
    void EnsurePeer(string guestId);

    void RemovePeer(string guestId);

    void HandleSignal(string guestId, JsonObject payload);

    bool SendControl(string guestId, JsonObject message);

    void BroadcastControl(JsonObject message);
}

public sealed partial class PeerHub : IPeerLink, IDisposable
{
    private const int MaxControlBytes = 4096;

    private readonly IRoomLink _room;
    private readonly ICollection<string> _iceServers;
    private readonly ILogger _log;
    private readonly string? _bindAddress;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, Peer> _peers = new(StringComparer.Ordinal);

    /// <param name="bindAddress">Restricts the local interface used for media, for example 127.0.0.1 in tests. Null uses all interfaces.</param>
    public PeerHub(IRoomLink room, IEnumerable<string> iceServers, ILogger log, string? bindAddress = null)
    {
        _bindAddress = bindAddress;
        _room = room;
        _iceServers = [.. iceServers];
        _log = log;
    }

    public event Action<string, byte[]>? StateReceived;

    public event Action<string, JsonObject>? ControlReceived;

    public event Action<string, PeerStatus>? StatusChanged;

    public void EnsurePeer(string guestId)
    {
        _peers.GetOrAdd(guestId, Create);
    }

    public void RemovePeer(string guestId)
    {
        if (_peers.TryRemove(guestId, out var peer))
        {
            peer.Dispose();
            StatusChanged?.Invoke(guestId, PeerStatus.Closed);
        }
    }

    public void HandleSignal(string guestId, JsonObject payload)
    {
        if (!_peers.TryGetValue(guestId, out var peer))
        {
            return;
        }

        try
        {
            if (payload["sdp"] is JsonObject sdp && (string?)sdp["type"] == "answer" && (string?)sdp["sdp"] is { } text)
            {
                peer.Connection.SetRemoteDescription(new RtcDescription { Sdp = text, Type = RtcDescriptionType.Answer });
            }
            else if (payload["candidate"] is JsonObject c && (string?)c["candidate"] is { Length: > 0 } line)
            {
                peer.Connection.AddRemoteCandidate(new RtcCandidate { Content = line, Mid = (string?)c["sdpMid"] ?? "0" });
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogBadSignal(_log, ex, guestId);
        }
    }

    public bool SendControl(string guestId, JsonObject message)
    {
        if (_peers.TryGetValue(guestId, out var peer) && peer.Control is { IsOpen: true } channel)
        {
            channel.Send(message.ToJsonString());
            return true;
        }

        return false;
    }

    public void BroadcastControl(JsonObject message)
    {
        foreach (var id in _peers.Keys)
        {
            SendControl(id, message);
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        foreach (var id in _peers.Keys.ToArray())
        {
            if (_peers.TryRemove(id, out var peer))
            {
                peer.Dispose();
            }
        }
    }

    private Peer Create(string guestId)
    {
        var connection = new RtcPeerConnection(new RtcPeerConfiguration { IceServers = _iceServers, BindAddress = _bindAddress });
        var peer = new Peer(connection);

        connection.OnLocalDescriptionSafe += (_, d) =>
        {
            var sdp = new JsonObject { ["sdp"] = new JsonObject { ["type"] = d.Type.ToString().ToLowerInvariant(), ["sdp"] = d.Sdp } };
            FireAndLog(_room.SendSignalAsync(guestId, sdp, _shutdown.Token), "offer");
        };
        connection.OnCandidateSafe += (_, c) =>
        {
            var body = new JsonObject { ["candidate"] = new JsonObject { ["candidate"] = c.Content, ["sdpMid"] = c.Mid, ["sdpMLineIndex"] = 0 } };
            FireAndLog(_room.SendSignalAsync(guestId, body, _shutdown.Token), "candidate");
        };
        connection.OnConnectionStateChange += (_, state) =>
        {
            var status = state switch
            {
                rtcState.RTC_CONNECTED => PeerStatus.Open,
                rtcState.RTC_FAILED => PeerStatus.Failed,
                rtcState.RTC_DISCONNECTED => PeerStatus.Disconnected,
                rtcState.RTC_CLOSED => PeerStatus.Closed,
                _ => PeerStatus.Connecting,
            };
            StatusChanged?.Invoke(guestId, status);
        };

        peer.State = connection.CreateDataChannel(new RtcCreateDataChannelArgs { Label = "state", Unordered = true, Unreliable = true, MaxRetransmits = 0 });
        peer.State.OnBinaryReceivedSafe += (_, e) => StateReceived?.Invoke(guestId, e.Data.ToArray());

        peer.Control = connection.CreateDataChannel(new RtcCreateDataChannelArgs { Label = "control" });
        peer.Control.OnTextReceivedSafe += (_, e) =>
        {
            var text = e.Text;
            if (Encoding.UTF8.GetByteCount(text) > MaxControlBytes)
            {
                return;
            }

            try
            {
                if (JsonNode.Parse(text) is JsonObject json)
                {
                    ControlReceived?.Invoke(guestId, json);
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // A malformed control message from a guest is dropped.
            }
        };
        return peer;
    }

    private void FireAndLog(Task task, string what)
    {
        _ = task.ContinueWith(
            t => LogSendFailed(_log, t.Exception, what),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignoring a bad signal from guest {GuestId}")]
    private static partial void LogBadSignal(ILogger logger, Exception ex, string guestId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not send {What} to the room")]
    private static partial void LogSendFailed(ILogger logger, Exception? ex, string what);

    private sealed class Peer(IRtcPeerConnection connection) : IDisposable
    {
        public IRtcPeerConnection Connection { get; } = connection;

        public IRtcDataChannel? State { get; set; }

        public IRtcDataChannel? Control { get; set; }

        public void Dispose() => (Connection as IDisposable)?.Dispose();
    }
}

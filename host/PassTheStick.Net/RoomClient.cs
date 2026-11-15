using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using PassTheStick.Protocol;

namespace PassTheStick.Net;

public sealed record RoomPlayer(string Id, string Name, int Slot, bool Away);

public abstract record RoomEvent;

public sealed record PresenceEvent(IReadOnlyList<RoomPlayer> Players, string Holder, uint Epoch, bool HostAway, bool Everyone = false) : RoomEvent;

public sealed record SignalEvent(string From, JsonObject Payload) : RoomEvent;

public sealed record RequestEvent(string From) : RoomEvent;

public sealed record ChatEvent(string From, string Text) : RoomEvent;

/// <summary>A binary frame the room relayed because peer-to-peer failed. Only the current holder's frames arrive.</summary>
public sealed record RelayFrameEvent(string From, byte[] Payload) : RoomEvent;

public sealed record EndedEvent(string Reason) : RoomEvent;

public sealed record RoomErrorEvent(string Code, string Message, bool Fatal) : RoomEvent;

public enum RoomConnectionState
{
    Connecting,
    Open,
    Reconnecting,
    Closed,
}

public sealed record ConnectionEvent(RoomConnectionState State) : RoomEvent;

/// <summary>What the rest of the host needs from the signaling room. Faked in tests.</summary>
public interface IRoomLink
{
    event Action<RoomEvent>? Received;

    Task SendHolderAsync(string? guestId, uint epoch, bool everyone, CancellationToken cancellationToken);

    Task SendSignalAsync(string guestId, JsonObject payload, CancellationToken cancellationToken);

    Task KickAsync(string guestId, CancellationToken cancellationToken);
}

public sealed record CreatedRoom(string Code, string HostToken);

public sealed class RoomClient : IRoomLink
{
    private static readonly HashSet<string> FatalCodes = ["bad_version", "room_not_found", "bad_token", "too_large", "bad_message"];

    private readonly IMessageSocketFactory _sockets;
    private readonly Uri _wsBase;
    private readonly string _code;
    private readonly string _hostToken;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Lock _gate = new();
    private IMessageSocket? _current;

    public RoomClient(
        IMessageSocketFactory sockets,
        Uri wsBase,
        string code,
        string hostToken,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _sockets = sockets;
        _wsBase = wsBase;
        _code = code;
        _hostToken = hostToken;
        _delay = delay ?? Task.Delay;
    }

    public event Action<RoomEvent>? Received;

    public static async Task<CreatedRoom> CreateRoomAsync(HttpClient http, Uri httpBase, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsync(new Uri(httpBase, "/v1/rooms"), null, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreatedRoom>(cancellationToken).ConfigureAwait(false);
        return created is { Code.Length: > 0, HostToken.Length: > 0 } ? created : throw new InvalidOperationException("The server did not return a room.");
    }

    /// <summary>
    /// Checks that a saved room still exists and the token is still good, without taking it over: connects, says hello as
    /// the host, and reads the first answer. True only on a welcome. The socket is closed again either way.
    /// </summary>
    public static async Task<bool> ProbeAsync(IMessageSocketFactory sockets, Uri wsBase, string code, string hostToken, TimeSpan timeout, CancellationToken cancellationToken)
    {
        await using var socket = sockets.Create();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await socket.ConnectAsync(new Uri(wsBase, $"/v1/room/{code}"), limit.Token).ConfigureAwait(false);
            var hello = new JsonObject { ["t"] = "hello", ["v"] = ProtocolInfo.Version, ["role"] = "host", ["hostToken"] = hostToken };
            await socket.SendTextAsync(hello.ToJsonString(), limit.Token).ConfigureAwait(false);
            while (await socket.ReceiveAsync(limit.Token).ConfigureAwait(false) is { IsText: true } message)
            {
                if (JsonNode.Parse(message.Data) is JsonObject json)
                {
                    return (string?)json["t"] == "welcome";
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.Net.WebSockets.WebSocketException or HttpRequestException or IOException or JsonException)
        {
            return false;
        }
    }

    /// <summary>Delay before reconnect attempt n (0-based): 0.5 s doubling to 8 s.</summary>
    public static TimeSpan Backoff(int attempt) => TimeSpan.FromMilliseconds(Math.Min(8000, 500 * Math.Pow(2, Math.Min(attempt, 10))));

    /// <summary>Connects, says hello with the host token, and keeps the room attached until cancelled or a fatal error.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            Raise(new ConnectionEvent(attempt == 0 ? RoomConnectionState.Connecting : RoomConnectionState.Reconnecting));
            var socket = _sockets.Create();
            try
            {
                await socket.ConnectAsync(new Uri(_wsBase, $"/v1/room/{_code}"), cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    _current = socket;
                }

                var hello = new JsonObject { ["t"] = "hello", ["v"] = ProtocolInfo.Version, ["role"] = "host", ["hostToken"] = _hostToken };
                await socket.SendTextAsync(hello.ToJsonString(), cancellationToken).ConfigureAwait(false);

                while (await socket.ReceiveAsync(cancellationToken).ConfigureAwait(false) is { } message)
                {
                    var outcome = Dispatch(message);
                    if (outcome == Dispatched.Welcome)
                    {
                        attempt = 0;
                        Raise(new ConnectionEvent(RoomConnectionState.Open));
                    }
                    else if (outcome is Dispatched.Fatal or Dispatched.Ended)
                    {
                        Raise(new ConnectionEvent(RoomConnectionState.Closed));
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or HttpRequestException or IOException)
            {
                // Network trouble: fall through to the backoff below.
            }
            finally
            {
                lock (_gate)
                {
                    _current = null;
                }

                await socket.DisposeAsync().ConfigureAwait(false);
            }

            Raise(new ConnectionEvent(RoomConnectionState.Reconnecting));
            try
            {
                await _delay(Backoff(attempt++), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Raise(new ConnectionEvent(RoomConnectionState.Closed));
    }

    public Task SendHolderAsync(string? guestId, uint epoch, bool everyone, CancellationToken cancellationToken) =>
        SendJsonAsync(new JsonObject { ["t"] = "holder", ["id"] = guestId, ["epoch"] = epoch, ["mode"] = everyone ? "everyone" : "one" }, cancellationToken);

    public Task SendSignalAsync(string guestId, JsonObject payload, CancellationToken cancellationToken)
    {
        var body = (JsonObject)payload.DeepClone();
        body["t"] = "signal";
        body["to"] = guestId;
        return SendJsonAsync(body, cancellationToken);
    }

    public Task KickAsync(string guestId, CancellationToken cancellationToken) =>
        SendJsonAsync(new JsonObject { ["t"] = "kick", ["id"] = guestId }, cancellationToken);

    private enum Dispatched
    {
        Nothing,
        Welcome,
        Fatal,
        Ended,
    }

    private async Task SendJsonAsync(JsonObject body, CancellationToken cancellationToken)
    {
        IMessageSocket? socket;
        lock (_gate)
        {
            socket = _current;
        }

        if (socket is not null)
        {
            await socket.SendTextAsync(body.ToJsonString(), cancellationToken).ConfigureAwait(false);
        }
    }

    private Dispatched Dispatch(SocketMessage message)
    {
        if (!message.IsText)
        {
            if (message.Data.Length > 8)
            {
                Raise(new RelayFrameEvent(Convert.ToHexStringLower(message.Data.AsSpan(0, 8)), message.Data[8..]));
            }

            return Dispatched.Nothing;
        }

        JsonObject? json;
        try
        {
            json = JsonNode.Parse(message.Data) as JsonObject;
        }
        catch (JsonException)
        {
            return Dispatched.Nothing;
        }

        switch ((string?)json?["t"])
        {
            case "welcome":
                Raise(ParsePresence(json!));
                return Dispatched.Welcome;
            case "presence":
                Raise(ParsePresence(json!));
                return Dispatched.Nothing;
            case "signal":
                if ((string?)json!["from"] is { } from)
                {
                    var payload = (JsonObject)json.DeepClone();
                    payload.Remove("t");
                    payload.Remove("from");
                    Raise(new SignalEvent(from, payload));
                }

                return Dispatched.Nothing;
            case "request":
                Raise(new RequestEvent((string?)json!["from"] ?? string.Empty));
                return Dispatched.Nothing;
            case "chat":
                Raise(new ChatEvent((string?)json!["from"] ?? string.Empty, (string?)json["text"] ?? string.Empty));
                return Dispatched.Nothing;
            case "ended":
                Raise(new EndedEvent((string?)json!["reason"] ?? string.Empty));
                return Dispatched.Ended;
            case "error":
                var code = (string?)json!["code"] ?? "unknown";
                var fatal = FatalCodes.Contains(code);
                Raise(new RoomErrorEvent(code, (string?)json["message"] ?? code, fatal));
                return fatal ? Dispatched.Fatal : Dispatched.Nothing;
            default:
                return Dispatched.Nothing;
        }
    }

    private static PresenceEvent ParsePresence(JsonObject json)
    {
        var players = new List<RoomPlayer>();
        if (json["players"] is JsonArray array)
        {
            foreach (var node in array)
            {
                if (node is JsonObject p && (string?)p["id"] is { } id)
                {
                    players.Add(new RoomPlayer(id, (string?)p["name"] ?? "Guest", (int?)p["slot"] ?? 0, (bool?)p["away"] ?? false));
                }
            }
        }

        var epoch = json["epoch"] is { } e ? Convert.ToUInt32(e.GetValue<double>(), CultureInfo.InvariantCulture) : 0u;
        return new PresenceEvent(players, (string?)json["holder"] ?? "host", epoch, (bool?)json["hostAway"] ?? false, (string?)json["mode"] == "everyone");
    }

    private void Raise(RoomEvent e) => Received?.Invoke(e);
}

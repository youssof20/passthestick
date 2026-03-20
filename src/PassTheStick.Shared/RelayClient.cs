using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace PassTheStick.Shared;

public sealed class RelayClient : IDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiveTask;
    private CancellationTokenSource? _heartbeatCts;
    private Task? _heartbeatTask;
    private bool _disconnectNotified;
    private string? _forcedDisconnectReason;
    private long _lastPongTs;
    private long _lastPingTs;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public bool IsHost { get; private set; }
    public string? RoomCode { get; private set; }
    public string? MyId { get; private set; }
    public bool IsConnected => _ws.State == WebSocketState.Open;

    /// <summary>Last measured round-trip latency from PONG handling (ms), or 0 if unknown.</summary>
    public int LastLatencyMs { get; private set; }

    public event Action? Connected;
    public event Action<string>? Disconnected;
    public event Action<string>? Error;
    public event Action<string>? RoomCreated;
    public event Action<List<PlayerInfo>>? PlayerListReceived;
    public event Action? YouHaveItReceived;
    public event Action<string>? PassStickReceived;
    public event Action<KeyEventMessage>? KeyEventReceived;
    public event Action<PadStateMessage>? PadStateReceived;
    public event Action<string>? SessionEnded;
    public event Action<int>? LatencyUpdatedMs;
    public event Action<string>? HostRejoined;

    public async Task ConnectAsync()
    {
        var uri = new Uri(Constants.RelayWebSocketUrl);
        InputDebugLog.Log(InputDebugLog.LogLevel.Info, $"[relay] Connecting to {uri}...");
        await WakeRelayAsync(Constants.RelayWebSocketUrl).ConfigureAwait(false);
        // Bypass system proxy settings; localhost relay should connect directly.
        _ws.Options.Proxy = null;
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        _disconnectNotified = false;
        _forcedDisconnectReason = null;
        _lastPingTs = 0;
        _lastPongTs = 0;
        LastLatencyMs = 0;
        await _ws.ConnectAsync(uri, _cts.Token).ConfigureAwait(false);
        MyId = null;
        InputDebugLog.Log(InputDebugLog.LogLevel.Info, $"[relay] Connected to {uri}");
        Connected?.Invoke();
        _receiveTask = ReceiveLoopAsync();
        StartHeartbeatLoop();
    }

    /// <summary>
    /// Best-effort HTTP GET to the relay's origin so cold instances (e.g. Render) wake before WebSocket connect.
    /// Non-fatal on failure (localhost, firewalls, etc.).
    /// </summary>
    private static async Task WakeRelayAsync(string wsUrl)
    {
        try
        {
            if (wsUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
                wsUrl.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase))
                return;

            var httpUrl = wsUrl
                .Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase)
                .Replace("ws://", "http://", StringComparison.OrdinalIgnoreCase);
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(30);
            await http.GetAsync(new Uri(httpUrl)).ConfigureAwait(false);
            await Task.Delay(2000).ConfigureAwait(false);
        }
        catch
        {
            // non-fatal
        }
    }

    public async Task<string> CreateRoomAsync()
    {
        var tcs = new TaskCompletionSource<string>();
        void OnRoomCreated(string code)
        {
            RoomCreated -= OnRoomCreated;
            tcs.TrySetResult(code);
        }
        RoomCreated += OnRoomCreated;
        await SendAsync(new CreateMessage());
        RoomCode = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        IsHost = true;
        return RoomCode;
    }

    public async Task<string> RejoinHostAsync(string roomCode)
    {
        RoomCode = roomCode;
        IsHost = true;

        var tcs = new TaskCompletionSource<string>();

        void OnRejoined(string rc)
        {
            tcs.TrySetResult(rc);
        }

        void OnErr(string err)
        {
            tcs.TrySetException(new InvalidOperationException(err));
        }

        HostRejoined += OnRejoined;
        Error += OnErr;

        try
        {
            await SendAsync(new HostRejoinMessage("HOST_REJOIN", roomCode));
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            HostRejoined -= OnRejoined;
            Error -= OnErr;
        }
    }

    public async Task JoinRoomAsync(string roomCode, string name)
    {
        RoomCode = roomCode;
        IsHost = false;
        var tcs = new TaskCompletionSource<bool>(); // true = success, false = error
        void OnList(List<PlayerInfo> _) { PlayerListReceived -= OnList; Error -= OnErr; tcs.TrySetResult(true); }
        void OnErr(string _) { Error -= OnErr; PlayerListReceived -= OnList; tcs.TrySetResult(false); }
        PlayerListReceived += OnList;
        Error += OnErr;
        await SendAsync(new JoinMessage("JOIN", roomCode, name));
        var ok = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (!ok)
            throw new InvalidOperationException("Room not found or join failed.");
    }

    public async Task SendPassStickAsync(string toId) =>
        await SendAsync(new PassStickMessage("PASS_STICK", toId));

    public async Task CloseRoomAsync(string reason = "Host ended the session") =>
        await SendAsync(new CloseRoomMessage("CLOSE_ROOM", reason));

    public async Task SendKeyEventAsync(int vk, int sc, bool down) =>
        await SendAsync(new KeyEventMessage("KEY_EVENT", vk, sc, down));

    public async Task SendPadStateAsync(PadStateMessage msg) =>
        await SendAsync(msg);

    public async Task SendAsync(object message)
    {
        var json = JsonSerializer.Serialize(message, message.GetType(), JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token);
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[4096];
        var segment = new ArraySegment<byte>(buffer);
        try
        {
            while (_ws.State == WebSocketState.Open)
            {
                var result = await _ws.ReceiveAsync(segment, _cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                if (result.MessageType != WebSocketMessageType.Text)
                    continue;
                var json = Encoding.UTF8.GetString(buffer.AsSpan(0, result.Count));
                DispatchMessage(json);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Error?.Invoke(ex.Message);
        }
        finally
        {
            if (!_disconnectNotified)
            {
                _disconnectNotified = true;
                InputDebugLog.Log(InputDebugLog.LogLevel.Warning, $"[relay] Disconnected: {_forcedDisconnectReason ?? _ws.CloseStatusDescription ?? "Connection closed"}");
                Disconnected?.Invoke(_forcedDisconnectReason ?? _ws.CloseStatusDescription ?? "Connection closed");
            }
        }
    }

    private void DispatchMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl))
                return;
            var type = typeEl.GetString();
            if (!string.IsNullOrWhiteSpace(type))
                InputDebugLog.Log(InputDebugLog.LogLevel.Verbose, $"[relay] ← {type}");
            switch (type)
            {
                case "CREATED":
                    if (root.TryGetProperty("id", out var createdId))
                        MyId = createdId.GetString();
                    if (root.TryGetProperty("roomCode", out var rc))
                        RoomCreated?.Invoke(rc.GetString() ?? "");
                    break;
                case "JOINED":
                    if (root.TryGetProperty("id", out var joinedId))
                        MyId = joinedId.GetString();
                    break;
                case "PLAYER_LIST":
                    if (root.TryGetProperty("players", out var arr))
                    {
                        var list = JsonSerializer.Deserialize<List<PlayerInfo>>(arr.GetRawText(), JsonOptions) ?? new List<PlayerInfo>();
                        PlayerListReceived?.Invoke(list);
                    }
                    break;
                case "YOU_HAVE_IT":
                    YouHaveItReceived?.Invoke();
                    break;
                case "PASS_STICK":
                    if (root.TryGetProperty("toId", out var toId))
                        PassStickReceived?.Invoke(toId.GetString() ?? "");
                    break;
                case "KEY_EVENT":
                    var ke = JsonSerializer.Deserialize<KeyEventMessage>(json, JsonOptions);
                    if (ke != null)
                    {
                        InputDebugLog.Log($"KEY_EVENT received fromId={ke.FromId}: vk={ke.Vk} sc={ke.Sc} down={ke.Down}");
                        KeyEventReceived?.Invoke(ke);
                    }
                    break;
                case "PAD_STATE":
                    var ps = JsonSerializer.Deserialize<PadStateMessage>(json, JsonOptions);
                    if (ps != null)
                    {
                        InputDebugLog.Log(
                            $"PAD_STATE received fromId={ps.FromId}: btns=0x{ps.Btns:X} lx={ps.Lx} ly={ps.Ly} rx={ps.Rx} ry={ps.Ry} lt={ps.Lt} rt={ps.Rt}");
                        PadStateReceived?.Invoke(ps);
                    }
                    break;
                case "PONG":
                    var pong = JsonSerializer.Deserialize<PongMessage>(json, JsonOptions);
                    if (pong != null)
                    {
                        _lastPongTs = pong.Ts;
                        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        var latency = (int)Math.Max(0, now - pong.Ts);
                        LastLatencyMs = latency;
                        LatencyUpdatedMs?.Invoke(latency);
                    }
                    break;
                case "SESSION_ENDED":
                    var ended = JsonSerializer.Deserialize<SessionEndedMessage>(json, JsonOptions);
                    if (ended != null)
                        SessionEnded?.Invoke(ended.Reason);
                    break;
                case "REJOINED":
                    if (root.TryGetProperty("roomCode", out var reRoomCodeEl))
                        RoomCode = reRoomCodeEl.GetString();
                    if (root.TryGetProperty("id", out var idEl))
                        MyId = idEl.GetString();
                    IsHost = true;
                    HostRejoined?.Invoke(RoomCode ?? "");
                    break;
                case "ERROR":
                    if (root.TryGetProperty("msg", out var msg))
                        Error?.Invoke(msg.GetString() ?? "");
                    break;
            }
        }
        catch
        {
            // ignore parse errors
        }
    }

    private void StartHeartbeatLoop()
    {
        _heartbeatCts?.Cancel();
        _heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);

        _heartbeatTask = Task.Run(async () =>
        {
            try
            {
                while (!_heartbeatCts!.IsCancellationRequested && _ws.State == WebSocketState.Open)
                {
                    _lastPingTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    await SendAsync(new PingMessage("PING", _lastPingTs));

                    var deadline = DateTime.UtcNow.AddSeconds(5);
                    while (DateTime.UtcNow < deadline && !_heartbeatCts.IsCancellationRequested)
                    {
                        if (_lastPongTs == _lastPingTs && _lastPongTs != 0)
                            break;
                        await Task.Delay(200, _heartbeatCts.Token);
                    }

                    if (_lastPongTs != _lastPingTs || _lastPongTs == 0)
                    {
                        _forcedDisconnectReason = "[heartbeat] Connection lost — reconnecting...";
                        _disconnectNotified = true; // prevent ReceiveLoop double invoke
                        try { _ws.Abort(); } catch { }
                        Disconnected?.Invoke(_forcedDisconnectReason);
                        return;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(20), _heartbeatCts.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _forcedDisconnectReason = ex.Message;
                if (!_disconnectNotified)
                {
                    _disconnectNotified = true;
                    Disconnected?.Invoke(_forcedDisconnectReason);
                }
            }
        }, _heartbeatCts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _heartbeatCts?.Cancel();
        _ws.Dispose();
    }
}

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace PassTheStick.Shared;

public sealed class RelayClient : IDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiveTask;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public bool IsHost { get; private set; }
    public string? RoomCode { get; private set; }
    public string? MyId { get; private set; }
    public bool IsConnected => _ws.State == WebSocketState.Open;

    public event Action? Connected;
    public event Action<string>? Disconnected;
    public event Action<string>? Error;
    public event Action<string>? RoomCreated;
    public event Action<List<PlayerInfo>>? PlayerListReceived;
    public event Action? YouHaveItReceived;
    public event Action<string>? PassStickReceived;
    public event Action<KeyEventMessage>? KeyEventReceived;
    public event Action<PadStateMessage>? PadStateReceived;

    public async Task ConnectAsync()
    {
        var uri = new Uri(Constants.RelayWebSocketUrl);
        // Bypass system proxy settings; localhost relay should connect directly.
        _ws.Options.Proxy = null;
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await _ws.ConnectAsync(uri, _cts.Token);
        MyId = null;
        Connected?.Invoke();
        _receiveTask = ReceiveLoopAsync();
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
            Disconnected?.Invoke(_ws.CloseStatusDescription ?? "Connection closed");
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
                        InputDebugLog.Log($"KEY_EVENT received: vk={ke.Vk} sc={ke.Sc} down={ke.Down}");
                        KeyEventReceived?.Invoke(ke);
                    }
                    break;
                case "PAD_STATE":
                    var ps = JsonSerializer.Deserialize<PadStateMessage>(json, JsonOptions);
                    if (ps != null) PadStateReceived?.Invoke(ps);
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

    public void Dispose()
    {
        _cts.Cancel();
        _ws.Dispose();
    }
}

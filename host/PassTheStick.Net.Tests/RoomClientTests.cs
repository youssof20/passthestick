using System.Text.Json.Nodes;
using System.Threading.Channels;
using PassTheStick.Net;

namespace PassTheStick.Net.Tests;

internal sealed class FakeSocket : IMessageSocket
{
    private readonly Channel<SocketMessage?> _incoming = Channel.CreateUnbounded<SocketMessage?>();

    public bool FailConnect { get; set; }

    public List<string> Sent { get; } = [];

    public Uri? ConnectedTo { get; private set; }

    public bool Disposed { get; private set; }

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        ConnectedTo = uri;
        return FailConnect ? throw new System.Net.WebSockets.WebSocketException("no route") : Task.CompletedTask;
    }

    public Task SendTextAsync(string text, CancellationToken cancellationToken)
    {
        lock (Sent)
        {
            Sent.Add(text);
        }

        return Task.CompletedTask;
    }

    public Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<SocketMessage?> ReceiveAsync(CancellationToken cancellationToken) =>
        await _incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    public void Push(string json) => _incoming.Writer.TryWrite(new SocketMessage(true, System.Text.Encoding.UTF8.GetBytes(json)));

    public void PushBinary(byte[] data) => _incoming.Writer.TryWrite(new SocketMessage(false, data));

    public void Close() => _incoming.Writer.TryWrite(null);
}

internal sealed class FakeFactory(params FakeSocket[] sockets) : IMessageSocketFactory
{
    private int _next;

    public int Created => _next;

    public IMessageSocket Create() => sockets[_next++];
}

public class RoomClientTests
{
    private static readonly Uri Base = new("wss://example.test");
    private const string Welcome = "{\"t\":\"welcome\",\"v\":1,\"you\":\"host\",\"players\":[],\"holder\":\"host\",\"epoch\":0,\"hostAway\":false}";

    private static async Task<T> Next<T>(List<RoomEvent> seen, int from = 0)
        where T : RoomEvent
    {
        for (var i = 0; i < 200; i++)
        {
            lock (seen)
            {
                if (seen.Skip(from).OfType<T>().FirstOrDefault() is { } hit)
                {
                    return hit;
                }
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"No {typeof(T).Name} arrived.");
    }

    [Fact]
    public void Backoff_doubles_from_half_a_second_to_eight_seconds()
    {
        Assert.Equal([500, 1000, 2000, 4000, 8000, 8000, 8000], new[] { 0, 1, 2, 3, 4, 5, 99 }.Select(n => (int)RoomClient.Backoff(n).TotalMilliseconds));
    }

    [Fact]
    public async Task Says_hello_with_version_role_and_token_then_reports_players_and_signals()
    {
        var socket = new FakeSocket();
        var client = new RoomClient(new FakeFactory(socket), Base, "ABCDEF", "tok");
        var seen = new List<RoomEvent>();
        client.Received += e => { lock (seen) { seen.Add(e); } };
        using var cts = new CancellationTokenSource();
        var run = client.RunAsync(cts.Token);

        socket.Push(Welcome);
        await Next<PresenceEvent>(seen);
        Assert.Equal("wss://example.test/v1/room/ABCDEF", socket.ConnectedTo?.ToString());
        var hello = JsonNode.Parse(socket.Sent[0])!;
        Assert.Equal("hello", (string?)hello["t"]);
        Assert.Equal(1, (int?)hello["v"]);
        Assert.Equal("host", (string?)hello["role"]);
        Assert.Equal("tok", (string?)hello["hostToken"]);

        socket.Push("{\"t\":\"presence\",\"players\":[{\"id\":\"g1\",\"name\":\"Ann\",\"slot\":0},{\"id\":\"g2\",\"name\":\"Bo\",\"slot\":1,\"away\":true}],\"holder\":\"g1\",\"epoch\":3,\"hostAway\":false}");
        socket.Push("{\"t\":\"signal\",\"from\":\"g1\",\"sdp\":{\"type\":\"answer\",\"sdp\":\"x\"}}");
        socket.Push("{\"t\":\"request\",\"from\":\"g2\"}");
        var signal = await Next<SignalEvent>(seen);
        var presence = seen.OfType<PresenceEvent>().Last();
        Assert.Equal(3u, presence.Epoch);
        Assert.Equal("g1", presence.Holder);
        Assert.Equal([false, true], presence.Players.Select(p => p.Away));
        Assert.Equal("g1", signal.From);
        Assert.Null(signal.Payload["from"]);
        Assert.Equal("answer", (string?)signal.Payload["sdp"]!["type"]);
        Assert.Equal("g2", (await Next<RequestEvent>(seen)).From);

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Relay_frames_are_split_into_sender_id_and_payload()
    {
        var socket = new FakeSocket();
        var client = new RoomClient(new FakeFactory(socket), Base, "ABCDEF", "tok");
        var seen = new List<RoomEvent>();
        client.Received += e => { lock (seen) { seen.Add(e); } };
        using var cts = new CancellationTokenSource();
        var run = client.RunAsync(cts.Token);
        socket.Push(Welcome);
        socket.PushBinary([0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f, 0x10, 0x11, 9, 8, 7]);
        var frame = await Next<RelayFrameEvent>(seen);
        Assert.Equal("0a0b0c0d0e0f1011", frame.From);
        Assert.Equal([9, 8, 7], frame.Payload);
        await cts.CancelAsync();
        await run;
    }

    [Theory]
    [InlineData("bad_token")]
    [InlineData("bad_version")]
    [InlineData("room_not_found")]
    public async Task Fatal_errors_stop_the_client_without_reconnecting(string code)
    {
        var socket = new FakeSocket();
        var factory = new FakeFactory(socket, new FakeSocket());
        var client = new RoomClient(factory, Base, "ABCDEF", "tok", (_, _) => Task.CompletedTask);
        var seen = new List<RoomEvent>();
        client.Received += e => { lock (seen) { seen.Add(e); } };
        var run = client.RunAsync(CancellationToken.None);
        socket.Push($"{{\"t\":\"error\",\"code\":\"{code}\",\"message\":\"m\"}}");
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, factory.Created);
        Assert.True((await Next<RoomErrorEvent>(seen)).Fatal);
        Assert.Equal(RoomConnectionState.Closed, seen.OfType<ConnectionEvent>().Last().State);
    }

    [Fact]
    public async Task A_dropped_socket_reconnects_with_backoff_and_says_hello_again()
    {
        var first = new FakeSocket();
        var second = new FakeSocket();
        var delays = new List<TimeSpan>();
        var client = new RoomClient(new FakeFactory(first, second), Base, "ABCDEF", "tok", (d, _) => { delays.Add(d); return Task.CompletedTask; });
        var seen = new List<RoomEvent>();
        client.Received += e => { lock (seen) { seen.Add(e); } };
        using var cts = new CancellationTokenSource();
        var run = client.RunAsync(cts.Token);

        first.Push(Welcome);
        await Next<PresenceEvent>(seen);
        first.Close();
        for (var i = 0; i < 200 && second.Sent.Count == 0; i++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(["hello"], second.Sent.Select(s => (string?)JsonNode.Parse(s)!["t"]));
        Assert.Equal([TimeSpan.FromMilliseconds(500)], delays);
        Assert.True(first.Disposed);
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Connection_failures_retry_with_growing_delays_and_reset_after_a_welcome()
    {
        var bad1 = new FakeSocket { FailConnect = true };
        var bad2 = new FakeSocket { FailConnect = true };
        var good = new FakeSocket();
        var delays = new List<TimeSpan>();
        var client = new RoomClient(new FakeFactory(bad1, bad2, good), Base, "ABCDEF", "tok", (d, _) => { delays.Add(d); return Task.CompletedTask; });
        var seen = new List<RoomEvent>();
        client.Received += e => { lock (seen) { seen.Add(e); } };
        using var cts = new CancellationTokenSource();
        var run = client.RunAsync(cts.Token);
        good.Push(Welcome);
        await Next<PresenceEvent>(seen);
        Assert.Equal([500, 1000], delays.Select(d => (int)d.TotalMilliseconds));
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Outgoing_messages_have_the_documented_shape()
    {
        var socket = new FakeSocket();
        var client = new RoomClient(new FakeFactory(socket), Base, "ABCDEF", "tok");
        var seen = new List<RoomEvent>();
        client.Received += e => { lock (seen) { seen.Add(e); } };
        using var cts = new CancellationTokenSource();
        var run = client.RunAsync(cts.Token);
        socket.Push(Welcome);
        await Next<PresenceEvent>(seen);

        await client.SendHolderAsync("g1", 4, false, CancellationToken.None);
        await client.SendHolderAsync(null, 5, true, CancellationToken.None);
        await client.SendSignalAsync("g1", new JsonObject { ["sdp"] = new JsonObject { ["type"] = "offer", ["sdp"] = "s" } }, CancellationToken.None);
        await client.KickAsync("g2", CancellationToken.None);

        var sent = socket.Sent.Skip(1).Select(s => (JsonObject)JsonNode.Parse(s)!).ToList();
        Assert.Equal("holder", (string?)sent[0]["t"]);
        Assert.Equal("g1", (string?)sent[0]["id"]);
        Assert.Equal(4, (int?)sent[0]["epoch"]);
        Assert.Null(sent[1]["id"]);
        Assert.Equal("one", (string?)sent[0]["mode"]);
        Assert.Equal("everyone", (string?)sent[1]["mode"]);
        Assert.Equal("signal", (string?)sent[2]["t"]);
        Assert.Equal("g1", (string?)sent[2]["to"]);
        Assert.Equal("offer", (string?)sent[2]["sdp"]!["type"]);
        Assert.Equal("kick", (string?)sent[3]["t"]);
        await cts.CancelAsync();
        await run;
    }
}

public class RoomProbeTests
{
    private static readonly Uri Base = new("wss://example.test");

    private static Task<bool> Probe(FakeSocket socket, int timeoutMs = 2000) =>
        RoomClient.ProbeAsync(new FakeFactory(socket), Base, "ABCDEF", "tok", TimeSpan.FromMilliseconds(timeoutMs), CancellationToken.None);

    [Fact]
    public async Task A_welcome_means_the_room_and_token_are_still_good_and_the_probe_says_hello_as_the_host()
    {
        var socket = new FakeSocket();
        socket.Push("{\"t\":\"welcome\",\"v\":1,\"you\":\"host\",\"players\":[],\"holder\":\"host\",\"epoch\":0,\"hostAway\":true}");
        Assert.True(await Probe(socket));
        Assert.Contains("\"role\":\"host\"", socket.Sent[0], StringComparison.Ordinal);
        Assert.Contains("\"hostToken\":\"tok\"", socket.Sent[0], StringComparison.Ordinal);
        Assert.True(socket.Disposed);
    }

    [Theory]
    [InlineData("room_not_found")]
    [InlineData("bad_token")]
    [InlineData("host_exists")]
    public async Task An_error_means_do_not_resume(string code)
    {
        var socket = new FakeSocket();
        socket.Push($"{{\"t\":\"error\",\"code\":\"{code}\",\"message\":\"m\"}}");
        Assert.False(await Probe(socket));
    }

    [Fact]
    public async Task A_dead_server_a_silent_server_and_a_closed_socket_all_mean_do_not_resume()
    {
        Assert.False(await Probe(new FakeSocket { FailConnect = true }));
        Assert.False(await Probe(new FakeSocket(), 150));
        var closed = new FakeSocket();
        closed.Close();
        Assert.False(await Probe(closed));
    }
}

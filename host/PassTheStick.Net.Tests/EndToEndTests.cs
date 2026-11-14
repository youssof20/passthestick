using System.Text.Json.Nodes;
using DataChannelDotnet;
using DataChannelDotnet.Data;
using DataChannelDotnet.Impl;
using Microsoft.Extensions.Logging.Abstractions;
using PassTheStick.Core;
using PassTheStick.Net;
using PassTheStick.Platform;
using PassTheStick.Protocol;

namespace PassTheStick.Net.Tests;

/// <summary>
/// Runs the real Worker, the real room client, the real libdatachannel peers and the real session logic,
/// with a libdatachannel "browser" standing in for the web guest. Set PTS_WORKER_URL (for example
/// http://127.0.0.1:8787 with `npx wrangler dev` running in server/worker) to run it; otherwise it
/// returns immediately, so a green run without the variable proves nothing about this path.
/// </summary>
public sealed class EndToEndTests
{
    private static readonly string? WorkerUrl = Environment.GetEnvironmentVariable("PTS_WORKER_URL");

    private static async Task Eventually(Func<bool> condition, string what, int seconds = 15)
    {
        for (var i = 0; i < seconds * 50; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("Timed out waiting for: " + what);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task A_guest_joins_gets_the_stick_and_its_keys_and_pad_reach_the_host_then_everything_is_released()
    {
        if (WorkerUrl is null)
        {
            return;
        }

        var httpBase = new Uri(WorkerUrl);
        var wsBase = new Uri(WorkerUrl.Replace("http", "ws", StringComparison.Ordinal));
        using var http = new HttpClient();
        var room = await RoomClient.CreateRoomAsync(http, httpBase, CancellationToken.None);

        var session = new StickSession("host");
        var injector = new FakeInjector();
        var pad = new FakePad();
        using var pipeline = new InputPipeline(session, injector, pad);
        var client = new RoomClient(new WebSocketFactory(), wsBase, room.Code, room.HostToken);
        using var hub = new PeerHub(client, [], NullLogger.Instance);
        using var controller = new HostSessionController(session, pipeline, client, hub, NullLogger.Instance);
        using var stop = new CancellationTokenSource();
        var hostRun = client.RunAsync(stop.Token);

        // The "browser": joins as a guest over the same Worker and answers the host's offer.
        await using var guestSocket = new WebSocketMessageSocket();
        await guestSocket.ConnectAsync(new Uri(wsBase, $"/v1/room/{room.Code}"), CancellationToken.None);
        await guestSocket.SendTextAsync("{\"t\":\"hello\",\"v\":1,\"role\":\"guest\",\"name\":\"Browser\"}", CancellationToken.None);

        using var pc = new RtcPeerConnection(new RtcPeerConfiguration { IceServers = [] });
        IRtcDataChannel? state = null;
        IRtcDataChannel? control = null;
        var holderSeen = new TaskCompletionSource<(string Id, uint Epoch)>();
        pc.OnLocalDescriptionSafe += (conn, d) => _ = guestSocket.SendTextAsync(
            new JsonObject { ["t"] = "signal", ["sdp"] = new JsonObject { ["type"] = "answer", ["sdp"] = d.Sdp } }.ToJsonString(), CancellationToken.None);
        pc.OnCandidateSafe += (conn, c) => _ = guestSocket.SendTextAsync(
            new JsonObject { ["t"] = "signal", ["candidate"] = new JsonObject { ["candidate"] = c.Content, ["sdpMid"] = c.Mid } }.ToJsonString(), CancellationToken.None);
        pc.OnDataChannel += (_, ch) =>
        {
            if (ch.Label == "state")
            {
                state = ch;
            }
            else
            {
                control = ch;
                ch.OnTextReceivedSafe += (_, e) =>
                {
                    var m = JsonNode.Parse(e.Text)!;
                    if ((string?)m["t"] == "holder")
                    {
                        holderSeen.TrySetResult(((string)m["id"]!, (uint)(double)m["epoch"]!));
                    }
                };
            }
        };

        string? myId = null;
        var pump = Task.Run(async () =>
        {
            while (await guestSocket.ReceiveAsync(stop.Token) is { IsText: true } msg)
            {
                var j = JsonNode.Parse(msg.Data)!;
                switch ((string?)j["t"])
                {
                    case "welcome":
                        myId = (string?)j["you"];
                        break;
                    case "signal" when j["sdp"] is JsonObject sdp:
                        pc.SetRemoteDescription(new RtcDescription { Sdp = (string)sdp["sdp"]!, Type = RtcDescriptionType.Offer });
                        break;
                    case "signal" when j["candidate"] is JsonObject c:
                        pc.AddRemoteCandidate(new RtcCandidate { Content = (string)c["candidate"]!, Mid = (string?)c["sdpMid"] ?? "0" });
                        break;
                    default:
                        break;
                }
            }
        });

        await Eventually(() => myId is not null && session.Players.Count == 1, "host sees the guest");
        await Eventually(() => state is { IsOpen: true } && control is { IsOpen: true }, "both data channels open");

        // Not the holder yet: input is dropped.
        control!.Send($"{{\"t\":\"key\",\"code\":\"KeyW\",\"down\":true,\"epoch\":{session.Epoch}}}");
        state!.Send(StateFrameCodec.EncodePad(1, new PadState(PadButtons.A, 0, 0, 0, 0, 0, 0)));
        await Task.Delay(300);
        Assert.Empty(injector.Down);
        Assert.Empty(pad.State);

        // The host passes the stick; the guest hears about it on the control channel and the room.
        Assert.True(session.PassTo(myId!));
        var (holderId, epoch) = await holderSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(myId, holderId);
        Assert.Equal(session.Epoch, epoch);

        control.Send($"{{\"t\":\"key\",\"code\":\"KeyW\",\"down\":true,\"epoch\":{epoch}}}");
        state.Send(StateFrameCodec.EncodePad(2, new PadState(PadButtons.A | PadButtons.B, 1000, 0, 0, 0, 255, 0)));
        await Eventually(() => injector.Down.Count == 1 && pad.State.TryGetValue(0, out var p) && p.Buttons == (PadButtons.A | PadButtons.B), "holder input applied");

        // The host takes the stick back while keys and buttons are held: everything must be released.
        Assert.True(session.TakeBack());
        Assert.Empty(injector.Down);
        Assert.Equal(PadState.Neutral, pad.State[0]);

        await stop.CancelAsync();
        await hostRun;
        await pump.WaitAsync(TimeSpan.FromSeconds(3)).ContinueWith(_ => { }, TaskScheduler.Default);
    }
}

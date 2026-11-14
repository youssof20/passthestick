using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using PassTheStick.Core;
using PassTheStick.Net;
using PassTheStick.Platform;
using PassTheStick.Protocol;

namespace PassTheStick.Net.Tests;

internal sealed class FakeInjector : IInputInjector
{
    public HashSet<ScanKey> Down { get; } = [];

    public void KeyDown(ScanKey key) => Down.Add(key);

    public void KeyUp(ScanKey key) => Down.Remove(key);
}

internal sealed class FakePad : IVirtualPadBackend
{
    public Dictionary<int, PadState> State { get; } = [];

    public void Submit(int slot, in PadState state) => State[slot] = state;

    public void Neutralize(int slot) => State[slot] = PadState.Neutral;
}

internal sealed class FakeRoom : IRoomLink
{
    public event Action<RoomEvent>? Received;

    public List<(string? Id, uint Epoch)> Holders { get; } = [];

    public List<string> Kicked { get; } = [];

    public Task SendHolderAsync(string? guestId, uint epoch, CancellationToken cancellationToken)
    {
        Holders.Add((guestId, epoch));
        return Task.CompletedTask;
    }

    public Task SendSignalAsync(string guestId, JsonObject payload, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task KickAsync(string guestId, CancellationToken cancellationToken)
    {
        Kicked.Add(guestId);
        return Task.CompletedTask;
    }

    public void Raise(RoomEvent e) => Received?.Invoke(e);
}

internal sealed class FakePeers : IPeerLink
{
    public event Action<string, byte[]>? StateReceived;

    public event Action<string, JsonObject>? ControlReceived;

    public event Action<string, PeerStatus>? StatusChanged;

    public HashSet<string> Peers { get; } = [];

    public List<JsonObject> Broadcasts { get; } = [];

    public List<(string Id, JsonObject Payload)> Signals { get; } = [];

    public void EnsurePeer(string guestId) => Peers.Add(guestId);

    public void RemovePeer(string guestId)
    {
        Peers.Remove(guestId);
        StatusChanged?.Invoke(guestId, PeerStatus.Closed);
    }

    public void HandleSignal(string guestId, JsonObject payload) => Signals.Add((guestId, payload));

    public bool SendControl(string guestId, JsonObject message) => false;

    public void BroadcastControl(JsonObject message) => Broadcasts.Add(message);

    public void RaiseState(string from, byte[] data) => StateReceived?.Invoke(from, data);

    public void RaiseControl(string from, JsonObject message) => ControlReceived?.Invoke(from, message);
}

public sealed class HostSessionControllerTests : IDisposable
{
    private readonly StickSession _session = new("host");
    private readonly FakeInjector _inj = new();
    private readonly FakePad _pad = new();
    private readonly FakeRoom _room = new();
    private readonly FakePeers _peers = new();
    private readonly InputPipeline _pipe;
    private readonly HostSessionController _ctl;

    public HostSessionControllerTests()
    {
        _pipe = new InputPipeline(_session, _inj, _pad);
        _ctl = new HostSessionController(_session, _pipe, _room, _peers, NullLogger.Instance);
    }

    public void Dispose()
    {
        _ctl.Dispose();
        _pipe.Dispose();
    }

    private static PresenceEvent Presence(params (string Id, string Name, bool Away)[] players) =>
        new([.. players.Select((p, i) => new RoomPlayer(p.Id, p.Name, i, p.Away))], "host", 0, false);

    private void Join(string id, string name = "Guest") => _room.Raise(Presence((id, name, false)));

    // Parsed from text like real guest traffic, so values are JSON-backed rather than CLR-typed.
    private static JsonObject Key(string code, bool down, uint epoch) =>
        (JsonObject)JsonNode.Parse(
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{{\"t\":\"key\",\"code\":\"{code}\",\"down\":{(down ? "true" : "false")},\"epoch\":{epoch}}}"))!;

    [Fact]
    public void Presence_adds_players_and_starts_a_peer_for_each_connected_guest()
    {
        _room.Raise(Presence(("a", "Ann", false), ("b", "Bo", true)));
        Assert.Equal(["a", "b"], _session.Players.Select(p => p.Id));
        Assert.Contains("a", _peers.Peers);
        Assert.DoesNotContain("b", _peers.Peers); // away guests keep their place but get no new link
    }

    [Fact]
    public void A_guest_missing_from_presence_is_removed_and_the_stick_returns_to_the_host()
    {
        Join("a");
        Assert.True(_session.PassTo("a"));
        _room.Raise(Presence());
        Assert.Empty(_session.Players);
        Assert.True(_session.HostHoldsStick);
        Assert.DoesNotContain("a", _peers.Peers);
    }

    [Fact]
    public void A_full_room_kicks_the_extra_guest()
    {
        var small = new StickSession("host", 1);
        using var pipe = new InputPipeline(small, _inj, _pad);
        using var ctl = new HostSessionController(small, pipe, _room, _peers, NullLogger.Instance);
        _room.Raise(Presence(("a", "A", false), ("b", "B", false)));
        Assert.Equal(["b"], _room.Kicked);
    }

    [Fact]
    public void Every_holder_change_is_sent_to_the_room_and_to_guests_with_the_epoch()
    {
        Join("a");
        _session.PassTo("a");
        _session.TakeBack();
        Assert.Equal([("a", 1u), (null, 2u)], _room.Holders);
        Assert.Equal("a", (string?)_peers.Broadcasts[0]["id"]);
        Assert.Equal("host", (string?)_peers.Broadcasts[1]["id"]);
        Assert.Equal(2u, (uint)_peers.Broadcasts[1]["epoch"]!);
    }

    [Fact]
    public void Signals_are_handed_to_the_peer_layer()
    {
        _room.Raise(new SignalEvent("a", new JsonObject { ["sdp"] = "x" }));
        Assert.Equal("a", _peers.Signals.Single().Id);
    }

    [Fact]
    public void Pad_frames_from_the_holder_reach_the_virtual_pad_and_others_do_not()
    {
        Join("a");
        _room.Raise(Presence(("a", "A", false), ("b", "B", false)));
        _session.PassTo("a");
        var frame = StateFrameCodec.EncodePad(1, new PadState(PadButtons.A, 0, 0, 0, 0, 0, 0));
        _peers.RaiseState("b", frame);
        Assert.Empty(_pad.State);
        _peers.RaiseState("a", frame);
        Assert.Equal(PadButtons.A, _pad.State[0].Buttons);
    }

    [Fact]
    public void Key_messages_inject_only_with_the_current_epoch()
    {
        Join("a");
        _session.PassTo("a");
        _peers.RaiseControl("a", Key("KeyW", true, 0));
        Assert.Empty(_inj.Down);
        _peers.RaiseControl("a", Key("KeyW", true, _session.Epoch));
        Assert.Single(_inj.Down);
    }

    [Fact]
    public void Malformed_key_messages_are_ignored()
    {
        Join("a");
        _session.PassTo("a");
        foreach (var bad in new[]
        {
            "{\"t\":\"key\"}",
            "{\"t\":\"key\",\"code\":\"KeyW\",\"down\":\"yes\",\"epoch\":1}",
            "{\"t\":\"key\",\"code\":\"KeyW\",\"down\":true,\"epoch\":-1}",
            "{\"t\":\"key\",\"code\":\"KeyW\",\"down\":true,\"epoch\":1e20}",
            "{\"t\":\"nonsense\"}",
        })
        {
            _peers.RaiseControl("a", (JsonObject)JsonNode.Parse(bad)!);
        }

        Assert.Empty(_inj.Down);
    }

    [Fact]
    public void Garbage_frames_never_throw_or_inject()
    {
        Join("a");
        _session.PassTo("a");
        var rng = new Random(7);
        for (var i = 0; i < 2000; i++)
        {
            var buf = new byte[rng.Next(0, 40)];
            rng.NextBytes(buf);
            _peers.RaiseState("a", buf);
        }

        _session.TakeBack();
        Assert.Empty(_inj.Down);
    }

    [Fact]
    public void Relay_key_frames_work_for_the_holder_and_are_dropped_for_others()
    {
        _room.Raise(Presence(("a", "A", false), ("b", "B", false)));
        _session.PassTo("a");
        byte[] Frame(string code, bool down) => [.. new byte[] { 5, 0, 0, (byte)(down ? 1 : 0), (byte)code.Length }, .. System.Text.Encoding.ASCII.GetBytes(code)];
        _room.Raise(new RelayFrameEvent("b", Frame("KeyW", true)));
        Assert.Empty(_inj.Down);
        _room.Raise(new RelayFrameEvent("a", Frame("KeyW", true)));
        Assert.Single(_inj.Down);
        _room.Raise(new RelayFrameEvent("a", [5, 0, 0, 1, 99, 65])); // length byte lies
        Assert.Single(_inj.Down);
    }

    [Fact]
    public void Motion_and_mouse_reach_listeners_only_from_the_holder()
    {
        _room.Raise(Presence(("a", "A", false), ("b", "B", false)));
        _session.PassTo("a");
        var motion = new List<string>();
        _ctl.MotionReceived += (id, _) => motion.Add(id);
        var frame = StateFrameCodec.EncodeMotion(1, new MotionState(0, 1, 0, 5, 0, 0, 1));
        _peers.RaiseState("b", frame);
        _peers.RaiseState("a", frame);
        Assert.Equal(["a"], motion);
    }

    [Fact]
    public void A_guest_request_is_raised_on_the_session_and_changes_nothing()
    {
        Join("a");
        Player? asked = null;
        _session.StickRequested += p => asked = p;
        _peers.RaiseControl("a", new JsonObject { ["t"] = "request" });
        Assert.Equal("a", asked?.Id);
        Assert.True(_session.HostHoldsStick);
    }

    [Fact]
    public void Room_end_releases_everything()
    {
        Join("a");
        _session.PassTo("a");
        _peers.RaiseControl("a", Key("KeyW", true, _session.Epoch));
        _room.Raise(new EndedEvent("host_left"));
        Assert.Empty(_inj.Down);
    }
}

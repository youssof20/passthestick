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

internal sealed class StepClock : TimeProvider
{
    private long _ms;

    public override long TimestampFrequency => 1000;

    public override long GetTimestamp() => _ms;

    public void Advance(int ms) => _ms += ms;
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

    public List<(string Id, JsonObject Message)> Sent { get; } = [];

    public bool SendControl(string guestId, JsonObject message)
    {
        Sent.Add((guestId, message));
        return true;
    }

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
    private readonly StepClock _clock = new();
    private readonly HostSessionController _ctl;

    public HostSessionControllerTests()
    {
        _pipe = new InputPipeline(_session, _inj, _pad);
        _ctl = new HostSessionController(_session, _pipe, _room, _peers, NullLogger.Instance, _clock);
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

    private static JsonObject Test(bool on) => (JsonObject)JsonNode.Parse(on ? "{\"t\":\"test\",\"on\":true}" : "{\"t\":\"test\",\"on\":false}")!;

    private static byte[] Pad(PadButtons b, short lx = 0) => StateFrameCodec.EncodePad(1, new PadState(b, lx, 0, 0, 0, 0, 0));

    [Fact]
    public void A_guest_in_test_mode_is_shown_to_the_host_even_though_they_do_not_hold_the_stick()
    {
        Join("a");
        var probes = new List<ProbeSnapshot>();
        _ctl.ProbeChanged += probes.Add;
        _peers.RaiseControl("a", Test(true));
        _peers.RaiseState("a", Pad(PadButtons.A | PadButtons.DpadUp, 1000));
        Assert.Equal("a", probes.Single().GuestId);
        Assert.Equal(PadButtons.A | PadButtons.DpadUp, probes.Single().Pad.Buttons);
        Assert.Empty(_pad.State); // shown, never injected
        Assert.Empty(_inj.Down);
    }

    [Fact]
    public void Without_test_mode_a_non_holder_is_ignored_entirely()
    {
        Join("a");
        var probes = new List<ProbeSnapshot>();
        _ctl.ProbeChanged += probes.Add;
        _peers.RaiseState("a", Pad(PadButtons.A));
        _peers.RaiseControl("a", Key("KeyW", true, 0));
        Assert.Empty(probes);
    }

    [Fact]
    public void Test_keys_are_tracked_and_shown_but_not_pressed_on_the_host()
    {
        Join("a");
        ProbeSnapshot? last = null;
        _ctl.ProbeChanged += p => last = p;
        _peers.RaiseControl("a", Test(true));
        _peers.RaiseControl("a", Key("KeyW", true, 0));
        _peers.RaiseControl("a", Key("Space", true, 0));
        Assert.Equal(["KeyW", "Space"], last!.Keys.Order());
        _peers.RaiseControl("a", Key("KeyW", false, 0));
        Assert.Equal(["Space"], last.Keys);
        Assert.Empty(_inj.Down);
    }

    [Fact]
    public void What_arrived_is_echoed_to_the_guest_at_most_ten_times_a_second()
    {
        Join("a");
        _peers.RaiseControl("a", Test(true));
        _peers.RaiseState("a", Pad(PadButtons.A));
        _peers.RaiseState("a", Pad(PadButtons.A | PadButtons.B));
        Assert.Single(_peers.Sent);
        Assert.Equal("probe", (string?)_peers.Sent[0].Message["t"]);
        Assert.Equal((int)PadButtons.A, (int)_peers.Sent[0].Message["buttons"]!);

        _clock.Advance(100);
        _peers.RaiseState("a", Pad(PadButtons.A | PadButtons.B));
        Assert.Equal(2, _peers.Sent.Count);
        Assert.Equal((int)(PadButtons.A | PadButtons.B), (int)_peers.Sent[1].Message["buttons"]!);
    }

    [Fact]
    public void A_release_is_always_echoed_so_nothing_stays_lit_on_the_guests_screen()
    {
        Join("a");
        _peers.RaiseControl("a", Test(true));
        _peers.RaiseState("a", Pad(PadButtons.A));
        _peers.RaiseState("a", Pad(PadButtons.None));
        Assert.Equal(2, _peers.Sent.Count);
        Assert.Equal(0, (int)_peers.Sent[1].Message["buttons"]!);
    }

    [Fact]
    public void Turning_test_mode_off_or_leaving_the_room_stops_probing()
    {
        Join("a");
        var probes = new List<ProbeSnapshot>();
        var toggles = new List<(string, bool)>();
        _ctl.ProbeChanged += probes.Add;
        _ctl.TestModeChanged += (id, on) => toggles.Add((id, on));
        _peers.RaiseControl("a", Test(true));
        _peers.RaiseControl("a", Test(false));
        _peers.RaiseState("a", Pad(PadButtons.A));
        Assert.Empty(probes);

        _peers.RaiseControl("a", Test(true));
        _room.Raise(Presence());
        _peers.RaiseState("a", Pad(PadButtons.A));
        Assert.Empty(probes);
        Assert.Equal([("a", true), ("a", false), ("a", true), ("a", false)], toggles);
    }

    [Fact]
    public void A_stranger_cannot_start_test_mode_and_test_mode_does_not_widen_what_the_holder_may_do()
    {
        var toggles = new List<(string, bool)>();
        _ctl.TestModeChanged += (id, on) => toggles.Add((id, on));
        _peers.RaiseControl("nobody", Test(true));
        Assert.Empty(toggles);

        Join("a");
        _room.Raise(Presence(("a", "A", false), ("b", "B", false)));
        _peers.RaiseControl("b", Test(true));
        _session.PassTo("a");
        _peers.RaiseControl("b", Key("KeyW", true, _session.Epoch));
        Assert.Empty(_inj.Down); // b is testing, not holding: still not injected
    }

    [Fact]
    public void A_change_held_back_by_the_rate_limit_is_sent_later_so_the_guest_sees_the_final_state()
    {
        Join("a");
        _peers.RaiseControl("a", Test(true));
        _peers.RaiseControl("a", Key("KeyW", true, 0));
        _peers.RaiseControl("a", Key("Space", true, 0));
        Assert.Single(_peers.Sent);

        _ctl.FlushEchoes();
        Assert.Single(_peers.Sent);

        _clock.Advance(100);
        _ctl.FlushEchoes();
        Assert.Equal(2, _peers.Sent.Count);
        Assert.Equal(["KeyW", "Space"], _peers.Sent[1].Message["keys"]!.AsArray().Select(k => (string)k!).Order());

        _clock.Advance(500);
        _ctl.FlushEchoes();
        Assert.Equal(2, _peers.Sent.Count);
    }

    [Fact]
    public async Task Test_mode_survives_frames_and_toggles_arriving_from_several_threads()
    {
        Join("a");
        _peers.RaiseControl("a", Test(true));
        var frame = Pad(PadButtons.A);
        var work = Enumerable.Range(0, 4).Select(i => Task.Run(() =>
        {
            for (var n = 0; n < 500; n++)
            {
                _peers.RaiseState("a", frame);
                _peers.RaiseControl("a", Key("KeyW", n % 2 == 0, 0));
                _ctl.FlushEchoes();
                if (i == 0 && n % 50 == 0)
                {
                    _peers.RaiseControl("a", Test(n % 100 == 0));
                }
            }
        }));
        await Task.WhenAll(work);
    }
}

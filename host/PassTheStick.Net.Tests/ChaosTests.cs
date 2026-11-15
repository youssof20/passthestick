using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using PassTheStick.Core;
using PassTheStick.Net;
using PassTheStick.Platform;
using PassTheStick.Protocol;

namespace PassTheStick.Net.Tests;

/// <summary>
/// Throws a long, seeded storm of hostile and unlucky traffic at the whole host stack: frames that are late, repeated,
/// reordered or from the wrong player, key messages with old epochs, players joining and leaving, the game losing
/// focus, and the holder changing at awkward moments. The invariants are the two that matter: nothing from anyone but
/// the current holder ever reaches the game, and nothing stays held after the stick moves.
/// </summary>
public sealed class ChaosTests
{
    private static readonly string[] Codes = ["KeyW", "KeyA", "KeyS", "KeyD", "Space", "ArrowLeft", "Enter", "NumpadEnter", "ShiftLeft", "Bogus", "MetaLeft"];
    private static readonly string[] Ids = ["a", "b", "c", "ghost"];

    private static JsonObject Key(string code, bool down, uint epoch) =>
        (JsonObject)JsonNode.Parse(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{{\"t\":\"key\",\"code\":\"{code}\",\"down\":{(down ? "true" : "false")},\"epoch\":{epoch}}}"))!;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Nothing_unauthorised_gets_through_and_nothing_stays_held_after_the_stick_moves(int seed)
    {
        var rng = new Random(seed);
        var session = new StickSession("host");
        var inj = new FakeInjector();
        var pad = new FakePad();
        var clock = new StepClock();
        var room = new FakeRoom();
        var peers = new FakePeers();
        using var pipeline = new InputPipeline(session, inj, pad, clock);
        using var controller = new HostSessionController(session, pipeline, room, peers, NullLogger.Instance, clock);
        using var coordinator = new PassCoordinator(session);
        var tracker = new FakeTrackerForChaos();
        var pump = new InputPump(tracker, pipeline);

        var inRoom = new HashSet<string>();
        ushort seq = 0;

        string Snapshot() => string.Join(",", inj.Down.OrderBy(k => k.ScanCode).ThenBy(k => k.Extended).Select(k => $"{k.ScanCode}{(k.Extended ? "e" : string.Empty)}"))
            + "|" + string.Join(",", pad.State.OrderBy(p => p.Key).Select(p => $"{p.Key}:{p.Value}"));

        void AssertQuietUnlessHolderMoved(uint epochBefore, string holderBefore)
        {
            if (session.Epoch != epochBefore || session.Holder != holderBefore)
            {
                Assert.Empty(inj.Down);
                Assert.All(pad.State.Values, s => Assert.Equal(PadState.Neutral, s));
            }
        }

        for (var step = 0; step < 6000; step++)
        {
            var epoch = session.Epoch;
            var holder = session.Holder;
            var who = Ids[rng.Next(Ids.Length)];
            var isHolder = session.MaySend(who);
            var before = Snapshot();

            var action = rng.Next(15);
            switch (action)
            {
                case 0:
                    var present = new[] { "a", "b", "c" }.Where(_ => rng.Next(3) != 0).ToArray();
                    room.Raise(new PresenceEvent([.. present.Select((id, i) => new RoomPlayer(id, id.ToUpperInvariant(), i, rng.Next(8) == 0))], "host", 0, false));
                    inRoom = [.. present];
                    break;
                case 1:
                    _ = who != "ghost" ? session.PassTo(who) : false;
                    break;
                case 2:
                    session.TakeBack();
                    break;
                case 3:
                    coordinator.Pass();
                    break;
                case 4:
                    peers.RaiseControl(who, Key(Codes[rng.Next(Codes.Length)], rng.Next(2) == 0, (uint)Math.Max(0, (int)epoch + rng.Next(-2, 2))));
                    break;
                case 5:
                    peers.RaiseState(who, StateFrameCodec.EncodePad(seq += (ushort)rng.Next(-3, 5), new PadState((PadButtons)rng.Next(0x10000), (short)rng.Next(-32768, 32768), 0, 0, 0, (byte)rng.Next(256), 0)));
                    break;
                case 6:
                    peers.RaiseState(who, StateFrameCodec.EncodeKeepalive(seq++));
                    break;
                case 7:
                    var junk = new byte[rng.Next(0, 24)];
                    rng.NextBytes(junk);
                    peers.RaiseState(who, junk);
                    break;
                case 8:
                    tracker.Foreground = rng.Next(4) != 0;
                    pump.Step();
                    break;
                case 9:
                    clock.Advance(rng.Next(0, 900));
                    pump.Step();
                    break;
                case 10:
                    session.RemovePlayer(who);
                    break;
                case 11:
                    peers.RaiseControl(who, (JsonObject)JsonNode.Parse("{\"t\":\"test\",\"on\":" + (rng.Next(2) == 0 ? "true" : "false") + "}")!);
                    break;
                case 12:
                    session.RequestStick(who);
                    break;
                case 13:
                    session.SetMode(rng.Next(2) == 0 ? TurnMode.Everyone : TurnMode.OneAtATime);
                    break;
                default:
                    controller.FlushEchoes();
                    controller.Maintain();
                    break;
            }

            AssertQuietUnlessHolderMoved(epoch, holder);

            // A frame from someone who is not the holder must leave the game exactly as it was.
            // Only input frames are judged here; the pump, timeouts and holder changes legitimately release things.
            if (action is >= 4 and <= 7 && !isHolder && before != Snapshot())
            {
                Assert.Fail($"step {step}: state changed without the holder or epoch changing for non-holder {who}");
            }

            if (session.Mode == TurnMode.OneAtATime && session.HostHoldsStick)
            {
                Assert.Empty(inj.Down);
                Assert.All(pad.State.Values, s => Assert.Equal(PadState.Neutral, s));
            }
        }

        session.Reset();
        Assert.Empty(inj.Down);
        Assert.All(pad.State.Values, s => Assert.Equal(PadState.Neutral, s));
        _ = inRoom;
    }

    private sealed class FakeTrackerForChaos : IWindowTracker
    {
        public bool IsPinned => true;

        public bool Foreground { get; set; } = true;

        public bool IsGameForeground => Foreground;
    }
}

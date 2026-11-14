using PassTheStick.Platform;
using PassTheStick.Protocol;

namespace PassTheStick.Core.Tests;

internal sealed class FakeInjector : IInputInjector
{
    public HashSet<ScanKey> Down { get; } = [];

    public List<string> Log { get; } = [];

    public void KeyDown(ScanKey key)
    {
        Down.Add(key);
        Log.Add($"down {key.ScanCode:X2}{(key.Extended ? "e" : string.Empty)}");
    }

    public void KeyUp(ScanKey key)
    {
        Down.Remove(key);
        Log.Add($"up {key.ScanCode:X2}{(key.Extended ? "e" : string.Empty)}");
    }
}

internal sealed class FakePad : IVirtualPadBackend
{
    public Dictionary<int, PadState> State { get; } = [];

    public void Submit(int slot, in PadState state) => State[slot] = state;

    public void Neutralize(int slot) => State[slot] = PadState.Neutral;
}

internal sealed class ManualClock : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => 1000;

    public override long GetTimestamp() => _ticks;

    public void Advance(TimeSpan by) => _ticks += (long)by.TotalMilliseconds;
}

public class StickSessionTests
{
    private static StickSession NewSession(out string a, out string b)
    {
        var s = new StickSession("host");
        a = "alice";
        b = "bob";
        s.AddPlayer(a, "Alice");
        s.AddPlayer(b, "Bob");
        return s;
    }

    [Fact]
    public void Host_holds_the_stick_at_the_start()
    {
        var s = new StickSession("host");
        Assert.True(s.HostHoldsStick);
        Assert.Equal(0u, s.Epoch);
    }

    [Fact]
    public void Pass_changes_holder_and_bumps_epoch_each_time()
    {
        var s = NewSession(out var a, out var b);
        Assert.True(s.PassTo(a));
        Assert.Equal((a, 1u), (s.Holder, s.Epoch));
        Assert.True(s.PassTo(b));
        Assert.Equal((b, 2u), (s.Holder, s.Epoch));
        Assert.True(s.TakeBack());
        Assert.Equal(("host", 3u), (s.Holder, s.Epoch));
    }

    [Fact]
    public void Pass_to_unknown_or_current_holder_does_nothing()
    {
        var s = NewSession(out var a, out _);
        Assert.False(s.PassTo("nobody"));
        s.PassTo(a);
        Assert.False(s.PassTo(a));
        Assert.Equal(1u, s.Epoch);
    }

    [Fact]
    public void Take_back_when_host_already_holds_does_nothing()
    {
        var s = NewSession(out _, out _);
        Assert.False(s.TakeBack());
        Assert.Equal(0u, s.Epoch);
    }

    [Fact]
    public void Holder_leaving_returns_the_stick_to_the_host()
    {
        var s = NewSession(out var a, out _);
        s.PassTo(a);
        HolderChange? seen = null;
        s.HolderChanged += c => seen = c;
        Assert.True(s.RemovePlayer(a));
        Assert.Equal("host", s.Holder);
        Assert.Equal(HolderChangeReason.HolderLeft, seen?.Reason);
    }

    [Fact]
    public void Non_holder_leaving_does_not_change_holder()
    {
        var s = NewSession(out var a, out var b);
        s.PassTo(a);
        s.RemovePlayer(b);
        Assert.Equal((a, 1u), (s.Holder, s.Epoch));
    }

    [Fact]
    public void Room_cap_duplicate_ids_and_host_id_are_refused()
    {
        var s = new StickSession("host", 2);
        Assert.NotNull(s.AddPlayer("a", "A"));
        Assert.Null(s.AddPlayer("a", "again"));
        Assert.Null(s.AddPlayer("host", "imposter"));
        Assert.NotNull(s.AddPlayer("b", "B"));
        Assert.Null(s.AddPlayer("c", "C"));
    }

    [Fact]
    public void Slots_are_lowest_free_and_reused()
    {
        var s = new StickSession("host");
        Assert.Equal(0, s.AddPlayer("a", "A")?.Slot);
        Assert.Equal(1, s.AddPlayer("b", "B")?.Slot);
        s.RemovePlayer("a");
        Assert.Equal(0, s.AddPlayer("c", "C")?.Slot);
    }

    [Fact]
    public void Request_raises_an_event_but_changes_nothing()
    {
        var s = NewSession(out var a, out _);
        Player? asked = null;
        s.StickRequested += p => asked = p;
        Assert.True(s.RequestStick(a));
        Assert.Equal(a, asked?.Id);
        Assert.True(s.HostHoldsStick);
        Assert.False(s.RequestStick("stranger"));
    }

    [Fact]
    public void IsHolder_requires_matching_epoch()
    {
        var s = NewSession(out var a, out _);
        s.PassTo(a);
        Assert.True(s.IsHolder(a, 1));
        Assert.False(s.IsHolder(a, 0));
        Assert.False(s.IsHolder("bob", 1));
    }

    [Fact]
    public void Reset_bumps_epoch_even_if_host_already_holds()
    {
        var s = NewSession(out _, out _);
        s.Reset();
        Assert.Equal(1u, s.Epoch);
    }
}

public class KeyMapTests
{
    [Theory]
    [InlineData("KeyW", 0x11, false)]
    [InlineData("KeyA", 0x1E, false)]
    [InlineData("KeyS", 0x1F, false)]
    [InlineData("KeyD", 0x20, false)]
    [InlineData("KeyQ", 0x10, false)]
    [InlineData("KeyZ", 0x2C, false)]
    [InlineData("Space", 0x39, false)]
    [InlineData("Enter", 0x1C, false)]
    [InlineData("NumpadEnter", 0x1C, true)]
    [InlineData("Slash", 0x35, false)]
    [InlineData("NumpadDivide", 0x35, true)]
    [InlineData("ArrowLeft", 0x4B, true)]
    [InlineData("Numpad4", 0x4B, false)]
    [InlineData("ControlRight", 0x1D, true)]
    [InlineData("F11", 0x57, false)]
    [InlineData("Digit0", 0x0B, false)]
    public void Known_keys_map_to_the_right_scan_code_and_extended_flag(string code, int scan, bool extended)
    {
        Assert.True(KeyMap.TryResolve(code, out var key));
        Assert.Equal(new ScanKey((ushort)scan, extended), key);
    }

    [Theory]
    [InlineData("MetaLeft")]
    [InlineData("MetaRight")]
    [InlineData("ContextMenu")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Bogus")]
    public void Windows_keys_and_unknown_codes_are_refused(string? code) =>
        Assert.False(KeyMap.TryResolve(code, out _));

    [Fact]
    public void No_two_codes_share_a_scan_key()
    {
        var seen = new Dictionary<ScanKey, string>();
        foreach (var code in AllCodes())
        {
            Assert.True(KeyMap.TryResolve(code, out var key), code);
            Assert.False(seen.TryGetValue(key, out var other), $"{code} collides with {other}");
            seen[key] = code;
        }
    }

    private static IEnumerable<string> AllCodes()
    {
        foreach (var c in "QWERTYUIOPASDFGHJKLZXCVBNM")
        {
            yield return "Key" + c;
        }

        for (var i = 0; i <= 9; i++)
        {
            yield return "Digit" + i;
        }

        for (var i = 1; i <= 12; i++)
        {
            yield return "F" + i;
        }

        foreach (var c in new[] { "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight", "Home", "End", "PageUp", "PageDown", "Insert", "Delete", "Space", "Enter", "NumpadEnter", "Tab", "Escape", "Backspace" })
        {
            yield return c;
        }
    }
}

public sealed class InputPipelineTests : IDisposable
{
    private const string Alice = "alice";
    private const string Bob = "bob";

    private readonly StickSession _session = new("host");
    private readonly FakeInjector _inj = new();
    private readonly FakePad _pad = new();
    private readonly ManualClock _clock = new();
    private readonly InputPipeline _pipe;

    public InputPipelineTests()
    {
        _session.AddPlayer(Alice, "Alice");
        _session.AddPlayer(Bob, "Bob");
        _pipe = new InputPipeline(_session, _inj, _pad, _clock);
    }

    public void Dispose() => _pipe.Dispose();

    private void GiveStickTo(string id) => Assert.True(_session.PassTo(id));

    [Fact]
    public void Holder_input_is_injected()
    {
        GiveStickTo(Alice);
        Assert.Equal(InputResult.Accepted, _pipe.HandleKey(Alice, _session.Epoch, "KeyW", true));
        Assert.Contains(new ScanKey(0x11, false), _inj.Down);
        Assert.Equal(InputResult.Accepted, _pipe.HandleKey(Alice, _session.Epoch, "KeyW", false));
        Assert.Empty(_inj.Down);
    }

    [Fact]
    public void Non_holder_input_is_dropped()
    {
        GiveStickTo(Alice);
        Assert.Equal(InputResult.NotHolder, _pipe.HandleKey(Bob, _session.Epoch, "KeyW", true));
        Assert.Equal(InputResult.NotHolder, _pipe.HandlePad(Bob, _session.Epoch, 1, new PadState(PadButtons.A, 0, 0, 0, 0, 0, 0)));
        Assert.Empty(_inj.Log);
        Assert.Empty(_pad.State);
    }

    [Fact]
    public void Spoofing_the_host_id_is_dropped()
    {
        GiveStickTo(Alice);
        Assert.Equal(InputResult.NotHolder, _pipe.HandleKey("host", _session.Epoch, "KeyW", true));
    }

    [Fact]
    public void Input_is_dropped_while_the_host_holds_the_stick()
    {
        Assert.Equal(InputResult.NotHolder, _pipe.HandleKey(Alice, _session.Epoch, "KeyW", true));
    }

    [Fact]
    public void Stale_epoch_is_dropped()
    {
        GiveStickTo(Alice);
        var old = _session.Epoch;
        GiveStickTo(Bob);
        _session.PassTo(Alice);
        Assert.Equal(InputResult.StaleEpoch, _pipe.HandleKey(Alice, old, "KeyW", true));
    }

    [Fact]
    public void Unknown_and_windows_keys_are_not_injected()
    {
        GiveStickTo(Alice);
        Assert.Equal(InputResult.UnknownKey, _pipe.HandleKey(Alice, _session.Epoch, "MetaLeft", true));
        Assert.Equal(InputResult.UnknownKey, _pipe.HandleKey(Alice, _session.Epoch, "Nonsense", true));
        Assert.Empty(_inj.Log);
    }

    [Fact]
    public void Repeated_key_down_injects_once_and_stray_key_up_injects_nothing()
    {
        GiveStickTo(Alice);
        _pipe.HandleKey(Alice, _session.Epoch, "KeyW", true);
        _pipe.HandleKey(Alice, _session.Epoch, "KeyW", true);
        _pipe.HandleKey(Alice, _session.Epoch, "KeyD", false);
        Assert.Equal(["down 11"], _inj.Log);
    }

    private void HoldEverything()
    {
        _pipe.HandleKey(Alice, _session.Epoch, "KeyW", true);
        _pipe.HandleKey(Alice, _session.Epoch, "ArrowLeft", true);
        _pipe.HandlePad(Alice, _session.Epoch, 1, new PadState(PadButtons.A | PadButtons.B, 30000, -30000, 0, 0, 255, 0));
        Assert.Equal(2, _inj.Down.Count);
        Assert.NotEqual(PadState.Neutral, _pad.State[0]);
    }

    private void AssertAllReleased()
    {
        Assert.Empty(_inj.Down);
        Assert.Equal(PadState.Neutral, _pad.State[0]);
    }

    [Fact]
    public void Stick_passed_while_keys_and_buttons_are_held_releases_everything()
    {
        GiveStickTo(Alice);
        HoldEverything();
        GiveStickTo(Bob);
        AssertAllReleased();
    }

    [Fact]
    public void Host_taking_the_stick_back_releases_everything()
    {
        GiveStickTo(Alice);
        HoldEverything();
        _session.TakeBack();
        AssertAllReleased();
    }

    [Fact]
    public void Holder_disconnecting_mid_press_releases_everything()
    {
        GiveStickTo(Alice);
        HoldEverything();
        _session.RemovePlayer(Alice);
        AssertAllReleased();
    }

    [Fact]
    public void Losing_game_focus_releases_everything_and_drops_input_until_focus_returns()
    {
        GiveStickTo(Alice);
        HoldEverything();
        _pipe.SetPaused(true);
        AssertAllReleased();
        Assert.Equal(InputResult.Paused, _pipe.HandleKey(Alice, _session.Epoch, "KeyW", true));
        _pipe.SetPaused(false);
        Assert.Equal(InputResult.Accepted, _pipe.HandleKey(Alice, _session.Epoch, "KeyW", true));
    }

    [Fact]
    public void Silence_longer_than_the_timeout_releases_everything()
    {
        GiveStickTo(Alice);
        HoldEverything();
        _clock.Advance(ProtocolInfo.StateTimeout - TimeSpan.FromMilliseconds(10));
        _pipe.Tick();
        Assert.Equal(2, _inj.Down.Count);
        _clock.Advance(TimeSpan.FromMilliseconds(20));
        _pipe.Tick();
        AssertAllReleased();
    }

    [Fact]
    public void Keepalives_hold_off_the_timeout()
    {
        GiveStickTo(Alice);
        HoldEverything();
        for (var i = 0; i < 10; i++)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(400));
            Assert.Equal(InputResult.Accepted, _pipe.HandleKeepalive(Alice, _session.Epoch));
            _pipe.Tick();
        }

        Assert.Equal(2, _inj.Down.Count);
    }

    [Fact]
    public void Dispose_releases_everything_and_stops_listening()
    {
        GiveStickTo(Alice);
        HoldEverything();
        _pipe.Dispose();
        AssertAllReleased();
        Assert.Equal(InputResult.Paused, _pipe.HandleKey(Alice, _session.Epoch, "KeyW", true));
    }

    [Fact]
    public void Release_all_covers_every_co_op_pad_slot()
    {
        GiveStickTo(Alice);
        _pipe.HandlePad(Alice, _session.Epoch, 1, new PadState(PadButtons.A, 0, 0, 0, 0, 0, 0));
        _session.Reset();
        Assert.Equal(PadState.Neutral, _pad.State[0]);
    }

    [Fact]
    public void Old_pad_sequence_numbers_are_dropped_and_wrapping_is_handled()
    {
        GiveStickTo(Alice);
        var a = new PadState(PadButtons.A, 0, 0, 0, 0, 0, 0);
        Assert.Equal(InputResult.Accepted, _pipe.HandlePad(Alice, _session.Epoch, 65534, a));
        Assert.Equal(InputResult.Accepted, _pipe.HandlePad(Alice, _session.Epoch, 1, a));
        Assert.Equal(InputResult.StaleSequence, _pipe.HandlePad(Alice, _session.Epoch, 65535, a));
        Assert.Equal(InputResult.StaleSequence, _pipe.HandlePad(Alice, _session.Epoch, 1, a));
    }

    [Fact]
    public void Sequence_numbers_restart_after_a_holder_change()
    {
        GiveStickTo(Alice);
        var a = new PadState(PadButtons.A, 0, 0, 0, 0, 0, 0);
        _pipe.HandlePad(Alice, _session.Epoch, 500, a);
        GiveStickTo(Bob);
        Assert.Equal(InputResult.Accepted, _pipe.HandlePad(Bob, _session.Epoch, 3, a));
    }

    [Fact]
    public async Task Concurrent_input_and_stick_passes_never_leave_anything_held()
    {
        GiveStickTo(Alice);
        var stop = false;
        var feeder = Task.Run(() =>
        {
            var rng = new Random(1);
            while (!Volatile.Read(ref stop))
            {
                var who = rng.Next(2) == 0 ? Alice : Bob;
                _pipe.HandleKey(who, _session.Epoch, "KeyW", rng.Next(2) == 0);
                _pipe.HandlePad(who, _session.Epoch, (ushort)rng.Next(), new PadState(PadButtons.A, 1, 1, 1, 1, 1, 1));
            }
        });

        for (var i = 0; i < 2000; i++)
        {
            _session.PassTo(i % 2 == 0 ? Bob : Alice);
        }

        Volatile.Write(ref stop, true);
        await feeder;
        _session.Reset();
        Assert.Empty(_inj.Down);
        Assert.All(_pad.State.Values, s => Assert.Equal(PadState.Neutral, s));
    }
}

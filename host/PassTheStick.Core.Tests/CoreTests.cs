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

internal sealed class FakeTracker : IWindowTracker
{
    public bool IsPinned { get; set; } = true;

    public bool IsGameForeground { get; set; } = true;
}

public sealed class InputPumpTests
{
    [Fact]
    public void Unpinned_or_background_game_pauses_and_releases_and_foreground_resumes()
    {
        var session = new StickSession("host");
        session.AddPlayer("a", "A");
        var inj = new FakeInjector();
        var pad = new FakePad();
        var tracker = new FakeTracker();
        using var pipe = new InputPipeline(session, inj, pad);
        var pump = new InputPump(tracker, pipe);
        session.PassTo("a");

        pump.Step();
        Assert.Equal(InputResult.Accepted, pipe.HandleKey("a", session.Epoch, "KeyW", true));
        Assert.Single(inj.Down);

        tracker.IsGameForeground = false;
        pump.Step();
        Assert.Empty(inj.Down);
        Assert.Equal(InputResult.Paused, pipe.HandleKey("a", session.Epoch, "KeyW", true));

        tracker.IsGameForeground = true;
        tracker.IsPinned = false;
        pump.Step();
        Assert.Equal(InputResult.Paused, pipe.HandleKey("a", session.Epoch, "KeyW", true));

        tracker.IsPinned = true;
        pump.Step();
        Assert.Equal(InputResult.Accepted, pipe.HandleKey("a", session.Epoch, "KeyW", true));
    }
}

public sealed class PassCoordinatorTests : IDisposable
{
    private readonly StickSession _session = new("host");
    private readonly HashSet<string> _away = [];
    private readonly PassCoordinator _pass;

    public PassCoordinatorTests()
    {
        _session.AddPlayer("a", "Ann");
        _session.AddPlayer("b", "Bo");
        _pass = new PassCoordinator(_session, _away.Contains);
    }

    public void Dispose() => _pass.Dispose();

    [Fact]
    public void Rotation_goes_host_then_guests_in_slot_order_then_back_to_the_host()
    {
        var seen = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(PassOutcome.PassedToNext, _pass.Pass());
            seen.Add(_session.Holder);
        }

        Assert.Equal(["a", "b", "host", "a", "b", "host"], seen);
    }

    [Fact]
    public void Away_guests_are_skipped()
    {
        _away.Add("a");
        _pass.Pass();
        Assert.Equal("b", _session.Holder);
    }

    [Fact]
    public void With_nobody_else_there_nothing_happens()
    {
        var alone = new StickSession("host");
        using var coordinator = new PassCoordinator(alone);
        Assert.Equal(PassOutcome.NoOneToPass, coordinator.Pass());
        Assert.Equal(0u, alone.Epoch);
    }

    [Fact]
    public void A_request_is_approved_by_the_next_pass_and_jumps_the_queue()
    {
        _session.RequestStick("b");
        Assert.Equal("b", _pass.PendingRequest?.Id);
        Assert.Equal(PassOutcome.ApprovedRequest, _pass.Pass());
        Assert.Equal("b", _session.Holder);
        Assert.Null(_pass.PendingRequest);
    }

    [Fact]
    public void A_request_is_dropped_once_that_guest_gets_the_stick_another_way_or_leaves()
    {
        _session.RequestStick("a");
        _session.PassTo("a");
        Assert.Null(_pass.PendingRequest);

        _session.TakeBack();
        _session.RequestStick("b");
        _session.RemovePlayer("b");
        Assert.Null(_pass.PendingRequest);
        Assert.Equal(PassOutcome.PassedToNext, _pass.Pass());
        Assert.Equal("a", _session.Holder);
    }

    [Fact]
    public void A_request_from_a_guest_who_went_away_is_skipped_not_approved()
    {
        _session.RequestStick("a");
        _away.Add("a");
        Assert.Equal(PassOutcome.PassedToNext, _pass.Pass());
        Assert.Equal("b", _session.Holder);
    }
}

public sealed class EveryoneModeTests : IDisposable
{
    private readonly StickSession _session = new("host");
    private readonly FakeInjector _inj = new();
    private readonly FakePad _pad = new();
    private readonly ManualClock _clock = new();
    private readonly InputPipeline _pipe;

    public EveryoneModeTests()
    {
        foreach (var id in new[] { "a", "b", "c", "d", "e" })
        {
            _session.AddPlayer(id, id.ToUpperInvariant());
        }

        _pipe = new InputPipeline(_session, _inj, _pad, _clock);
    }

    public void Dispose() => _pipe.Dispose();

    private static PadState Pad(PadButtons b) => new(b, 0, 0, 0, 0, 0, 0);

    [Fact]
    public void In_one_at_a_time_mode_the_holder_always_drives_pad_one_whatever_their_slot()
    {
        _session.PassTo("d");
        Assert.Equal(InputResult.Accepted, _pipe.HandlePad("d", _session.Epoch, 1, Pad(PadButtons.A)));
        Assert.Equal(PadButtons.A, _pad.State[0].Buttons);
        Assert.Single(_pad.State);
    }

    [Fact]
    public void Switching_to_everyone_lets_every_guest_play_on_their_own_pad_and_bumps_the_epoch()
    {
        var before = _session.Epoch;
        Assert.True(_session.SetMode(TurnMode.Everyone));
        Assert.Equal(before + 1, _session.Epoch);
        Assert.False(_session.SetMode(TurnMode.Everyone));

        foreach (var (id, slot) in new[] { ("a", 0), ("b", 1), ("c", 2), ("d", 3) })
        {
            Assert.Equal(InputResult.Accepted, _pipe.HandlePad(id, _session.Epoch, 1, Pad(PadButtons.A)));
            Assert.Equal(PadButtons.A, _pad.State[slot].Buttons);
        }
    }

    [Fact]
    public void A_fifth_player_gets_no_pad_because_windows_games_read_four()
    {
        _session.SetMode(TurnMode.Everyone);
        Assert.Equal(InputResult.NoPadSlot, _pipe.HandlePad("e", _session.Epoch, 1, Pad(PadButtons.A)));
        Assert.False(_pad.State.ContainsKey(4));
    }

    [Fact]
    public void The_keyboard_belongs_to_the_first_player_only_and_the_host_and_strangers_never_send()
    {
        _session.SetMode(TurnMode.Everyone);
        Assert.Equal(InputResult.Accepted, _pipe.HandleKey("a", _session.Epoch, "KeyW", true));
        Assert.Equal(InputResult.NotKeyboardOwner, _pipe.HandleKey("b", _session.Epoch, "KeyS", true));
        Assert.Equal(InputResult.NotHolder, _pipe.HandleKey("host", _session.Epoch, "KeyS", true));
        Assert.Equal(InputResult.NotHolder, _pipe.HandleKey("stranger", _session.Epoch, "KeyS", true));
        Assert.Single(_inj.Down);
    }

    [Fact]
    public void Switching_modes_releases_everything()
    {
        _session.SetMode(TurnMode.Everyone);
        _pipe.HandlePad("a", _session.Epoch, 1, Pad(PadButtons.B));
        _pipe.HandlePad("b", _session.Epoch, 1, Pad(PadButtons.B));
        _pipe.HandleKey("a", _session.Epoch, "KeyW", true);
        _session.SetMode(TurnMode.OneAtATime);
        Assert.Empty(_inj.Down);
        Assert.All(_pad.State.Values, s => Assert.Equal(PadState.Neutral, s));
        Assert.Equal(InputResult.NotHolder, _pipe.HandlePad("a", _session.Epoch, 2, Pad(PadButtons.A)));
    }

    [Fact]
    public void A_player_who_leaves_loses_only_their_own_held_buttons()
    {
        _session.SetMode(TurnMode.Everyone);
        _pipe.HandlePad("a", _session.Epoch, 1, Pad(PadButtons.A));
        _pipe.HandlePad("b", _session.Epoch, 1, Pad(PadButtons.B));
        _session.RemovePlayer("a");
        Assert.Equal(PadState.Neutral, _pad.State[0]);
        Assert.Equal(PadButtons.B, _pad.State[1].Buttons);
    }

    [Fact]
    public void A_player_who_goes_quiet_is_released_while_a_chatty_one_stays_held()
    {
        _session.SetMode(TurnMode.Everyone);
        _pipe.HandlePad("a", _session.Epoch, 1, Pad(PadButtons.A));
        _pipe.HandlePad("b", _session.Epoch, 1, Pad(PadButtons.B));
        for (var i = 0; i < 6; i++)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(200));
            _pipe.HandleKeepalive("b", _session.Epoch);
            _pipe.Tick();
        }

        Assert.Equal(PadState.Neutral, _pad.State[0]);
        Assert.Equal(PadButtons.B, _pad.State[1].Buttons);
    }

    [Fact]
    public void Each_player_has_their_own_sequence_numbers()
    {
        _session.SetMode(TurnMode.Everyone);
        Assert.Equal(InputResult.Accepted, _pipe.HandlePad("a", _session.Epoch, 500, Pad(PadButtons.A)));
        Assert.Equal(InputResult.Accepted, _pipe.HandlePad("b", _session.Epoch, 3, Pad(PadButtons.A)));
        Assert.Equal(InputResult.StaleSequence, _pipe.HandlePad("a", _session.Epoch, 400, Pad(PadButtons.A)));
    }

    [Fact]
    public void Passing_means_nothing_when_everyone_is_playing_and_the_rotation_resumes_afterwards()
    {
        using var coordinator = new PassCoordinator(_session);
        _session.SetMode(TurnMode.Everyone);
        Assert.Equal(PassOutcome.NotApplicable, coordinator.Pass());
        _session.SetMode(TurnMode.OneAtATime);
        Assert.Equal(PassOutcome.PassedToNext, coordinator.Pass());
    }
}

public sealed class TurnTimerTests : IDisposable
{
    private readonly StickSession _session = new("host");
    private readonly ManualClock _clock = new();
    private readonly PassCoordinator _pass;
    private readonly TurnTimer _timer;

    public TurnTimerTests()
    {
        _session.AddPlayer("a", "A");
        _session.AddPlayer("b", "B");
        _pass = new PassCoordinator(_session);
        _timer = new TurnTimer(_session, _pass, _clock) { Limit = TimeSpan.FromSeconds(30) };
    }

    public void Dispose()
    {
        _timer.Dispose();
        _pass.Dispose();
    }

    [Fact]
    public void A_guests_turn_ends_when_the_time_is_up_and_the_stick_goes_to_the_next_player()
    {
        _session.PassTo("a");
        _clock.Advance(TimeSpan.FromSeconds(29));
        _timer.Tick();
        Assert.Equal("a", _session.Holder);
        Assert.Equal(TimeSpan.FromSeconds(1), _timer.Remaining);

        _clock.Advance(TimeSpan.FromSeconds(1));
        _timer.Tick();
        Assert.Equal("b", _session.Holder);
        Assert.Equal(TimeSpan.FromSeconds(30), _timer.Remaining);
    }

    [Fact]
    public void The_rotation_returns_to_the_host_whose_own_turns_are_not_timed()
    {
        _session.PassTo("b");
        _clock.Advance(TimeSpan.FromSeconds(31));
        _timer.Tick();
        Assert.Equal("host", _session.Holder);
        Assert.Null(_timer.Remaining);
        _clock.Advance(TimeSpan.FromHours(1));
        _timer.Tick();
        Assert.Equal("host", _session.Holder);
    }

    [Fact]
    public void No_limit_or_everyone_mode_means_nothing_is_timed()
    {
        _session.PassTo("a");
        _timer.Limit = TimeSpan.Zero;
        _clock.Advance(TimeSpan.FromHours(1));
        _timer.Tick();
        Assert.Equal("a", _session.Holder);
        Assert.Null(_timer.Remaining);

        _timer.Limit = TimeSpan.FromSeconds(30);
        _session.SetMode(TurnMode.Everyone);
        _clock.Advance(TimeSpan.FromHours(1));
        _timer.Tick();
        Assert.Null(_timer.Remaining);
        Assert.Equal("host", _session.Holder);
    }

    [Fact]
    public void A_pass_by_hand_gives_the_next_player_a_full_turn_and_changing_the_limit_restarts_the_clock()
    {
        _session.PassTo("a");
        _clock.Advance(TimeSpan.FromSeconds(25));
        _session.PassTo("b");
        Assert.Equal(TimeSpan.FromSeconds(30), _timer.Remaining);
        _clock.Advance(TimeSpan.FromSeconds(20));
        _timer.Limit = TimeSpan.FromSeconds(60);
        Assert.Equal(TimeSpan.FromSeconds(60), _timer.Remaining);
    }
}

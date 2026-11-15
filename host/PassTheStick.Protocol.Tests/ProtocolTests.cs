namespace PassTheStick.Protocol.Tests;

public class StateFrameCodecTests
{
    [Fact]
    public void Pad_round_trips()
    {
        var pad = new PadState(PadButtons.A | PadButtons.DpadLeft, -32768, 32767, -1, 1, 255, 7);
        var ok = StateFrameCodec.TryDecode(StateFrameCodec.EncodePad(513, pad), out var f);
        Assert.True(ok);
        Assert.Equal(StateKind.Pad, f.Kind);
        Assert.Equal((ushort)513, f.Seq);
        Assert.Equal(pad, f.Pad);
    }

    [Fact]
    public void Pad_frame_matches_the_golden_bytes_asserted_in_the_web_guest_tests()
    {
        var pad = new PadState(PadButtons.A | PadButtons.DpadLeft, -32768, 32767, -1, 1, 255, 7);
        Assert.Equal("010102041000" + "80ff7fffff0100ff07", Convert.ToHexStringLower(StateFrameCodec.EncodePad(513, pad)));
    }

    [Fact]
    public void Motion_round_trips()
    {
        var m = new MotionState(0.1f, -1f, 0.98f, 90f, -45.5f, 0f, 123456);
        Assert.True(StateFrameCodec.TryDecode(StateFrameCodec.EncodeMotion(1, m), out var f));
        Assert.Equal(m, f.Motion);
    }

    [Fact]
    public void Mouse_round_trips_with_relative_flag_and_buttons()
    {
        var p = new MouseState(-5, 300, true, 0b101);
        Assert.True(StateFrameCodec.TryDecode(StateFrameCodec.EncodeMouse(2, p), out var f));
        Assert.Equal(p, f.Mouse);
    }

    [Fact]
    public void Keepalive_round_trips()
    {
        Assert.True(StateFrameCodec.TryDecode(StateFrameCodec.EncodeKeepalive(9), out var f));
        Assert.Equal(StateKind.Keepalive, f.Kind);
        Assert.Equal((ushort)9, f.Seq);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Short_frames_are_rejected(int length) =>
        Assert.False(StateFrameCodec.TryDecode(new byte[length], out _));

    [Fact]
    public void Wrong_length_for_kind_is_rejected()
    {
        var longer = StateFrameCodec.EncodePad(1, default).Append((byte)0).ToArray();
        Assert.False(StateFrameCodec.TryDecode(longer, out _));
        Assert.False(StateFrameCodec.TryDecode(StateFrameCodec.EncodePad(1, default).AsSpan(0, 10), out _));
    }

    [Fact]
    public void Unknown_kind_is_rejected() =>
        Assert.False(StateFrameCodec.TryDecode(new byte[] { 99, 0, 0 }, out _));

    [Fact]
    public void Motion_with_nan_or_infinity_is_rejected()
    {
        var bad = StateFrameCodec.EncodeMotion(1, new MotionState(float.NaN, 0, 0, 0, 0, 0, 0));
        Assert.False(StateFrameCodec.TryDecode(bad, out _));
        var inf = StateFrameCodec.EncodeMotion(1, new MotionState(0, 0, 0, float.PositiveInfinity, 0, 0, 0));
        Assert.False(StateFrameCodec.TryDecode(inf, out _));
    }

    [Fact]
    public void Random_garbage_never_throws()
    {
        var rng = new Random(42);
        for (var i = 0; i < 5000; i++)
        {
            var buf = new byte[rng.Next(0, 40)];
            rng.NextBytes(buf);
            _ = StateFrameCodec.TryDecode(buf, out _);
        }
    }
}

public class SerialNumberTests
{
    [Theory]
    [InlineData(2, 1, true)]
    [InlineData(1, 1, false)]
    [InlineData(1, 2, false)]
    [InlineData(0, 65535, true)]
    [InlineData(65535, 0, false)]
    [InlineData(32768, 0, false)]
    [InlineData(32767, 0, true)]
    public void Wraps_correctly(ushort candidate, ushort current, bool expected) =>
        Assert.Equal(expected, SerialNumber.IsNewer(candidate, current));
}

public class RoomCodeTests
{
    [Fact]
    public void Generated_codes_are_valid_and_avoid_ambiguous_characters()
    {
        for (var i = 0; i < 2000; i++)
        {
            var code = RoomCode.Generate();
            Assert.True(RoomCode.IsValid(code));
            Assert.DoesNotContain(code, c => "01OIL".Contains(c, StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ABC12")]
    [InlineData("ABC1234")]
    [InlineData("ABCDE0")]
    [InlineData("abcdef")]
    public void Invalid_codes_are_rejected(string? code) => Assert.False(RoomCode.IsValid(code));

    [Fact]
    public void Normalize_fixes_case_and_whitespace_and_rejects_bad_input()
    {
        Assert.Equal("ABCDEF", RoomCode.Normalize("  abcdef "));
        Assert.Null(RoomCode.Normalize("abcde0"));
    }

    [Fact]
    public void Codes_do_not_repeat_in_a_small_sample()
    {
        var set = new HashSet<string>();
        for (var i = 0; i < 1000; i++)
        {
            set.Add(RoomCode.Generate());
        }

        Assert.True(set.Count > 990);
    }
}

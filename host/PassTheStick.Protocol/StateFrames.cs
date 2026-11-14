using System.Buffers.Binary;

namespace PassTheStick.Protocol;

public enum StateKind : byte
{
    Pad = 1,
    Motion = 2,
    Mouse = 3,
    Keepalive = 4,
}

/// <summary>Xbox 360 layout. Bit positions follow XInput so a gamepad mask can pass straight through.</summary>
[Flags]
public enum PadButtons : ushort
{
    None = 0,
    DpadUp = 0x0001,
    DpadDown = 0x0002,
    DpadLeft = 0x0004,
    DpadRight = 0x0008,
    Start = 0x0010,
    Back = 0x0020,
    LeftThumb = 0x0040,
    RightThumb = 0x0080,
    LeftShoulder = 0x0100,
    RightShoulder = 0x0200,
    Guide = 0x0400,
    A = 0x1000,
    B = 0x2000,
    X = 0x4000,
    Y = 0x8000,
}

public readonly record struct PadState(PadButtons Buttons, short Lx, short Ly, short Rx, short Ry, byte Lt, byte Rt)
{
    public static PadState Neutral => default;
}

public readonly record struct MotionState(float AccelX, float AccelY, float AccelZ, float GyroX, float GyroY, float GyroZ, uint TimestampMicros);

public readonly record struct MouseState(short X, short Y, bool Relative, byte Buttons);

/// <summary>One decoded frame from the unreliable state channel.</summary>
public readonly record struct StateFrame(StateKind Kind, ushort Seq, PadState Pad, MotionState Motion, MouseState Mouse);

/// <summary>Little-endian wire format from docs/protocol.md section 4. Decoding is strict: wrong length means rejected.</summary>
public static class StateFrameCodec
{
    private const int HeaderLength = 3;
    public const int PadLength = HeaderLength + 2 + 8 + 2;
    public const int MotionLength = HeaderLength + 24 + 4;
    public const int MouseLength = HeaderLength + 5;
    public const int KeepaliveLength = HeaderLength;

    public static byte[] EncodePad(ushort seq, in PadState pad)
    {
        var b = new byte[PadLength];
        WriteHeader(b, StateKind.Pad, seq);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(3), (ushort)pad.Buttons);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(5), pad.Lx);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(7), pad.Ly);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(9), pad.Rx);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(11), pad.Ry);
        b[13] = pad.Lt;
        b[14] = pad.Rt;
        return b;
    }

    public static byte[] EncodeMotion(ushort seq, in MotionState m)
    {
        var b = new byte[MotionLength];
        WriteHeader(b, StateKind.Motion, seq);
        var values = new[] { m.AccelX, m.AccelY, m.AccelZ, m.GyroX, m.GyroY, m.GyroZ };
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(3 + (i * 4)), values[i]);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(27), m.TimestampMicros);
        return b;
    }

    public static byte[] EncodeMouse(ushort seq, in MouseState p)
    {
        var b = new byte[MouseLength];
        WriteHeader(b, StateKind.Mouse, seq);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(3), p.X);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(5), p.Y);
        b[7] = (byte)((p.Buttons & 0x7F) | (p.Relative ? 0x80 : 0));
        return b;
    }

    public static byte[] EncodeKeepalive(ushort seq)
    {
        var b = new byte[KeepaliveLength];
        WriteHeader(b, StateKind.Keepalive, seq);
        return b;
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, out StateFrame frame)
    {
        frame = default;
        if (data.Length < HeaderLength)
        {
            return false;
        }

        var kind = (StateKind)data[0];
        var seq = BinaryPrimitives.ReadUInt16LittleEndian(data[1..]);
        switch (kind)
        {
            case StateKind.Pad when data.Length == PadLength:
                frame = new StateFrame(kind, seq, new PadState(
                    (PadButtons)BinaryPrimitives.ReadUInt16LittleEndian(data[3..]),
                    BinaryPrimitives.ReadInt16LittleEndian(data[5..]),
                    BinaryPrimitives.ReadInt16LittleEndian(data[7..]),
                    BinaryPrimitives.ReadInt16LittleEndian(data[9..]),
                    BinaryPrimitives.ReadInt16LittleEndian(data[11..]),
                    data[13],
                    data[14]), default, default);
                return true;
            case StateKind.Motion when data.Length == MotionLength:
                var m = new MotionState(
                    BinaryPrimitives.ReadSingleLittleEndian(data[3..]),
                    BinaryPrimitives.ReadSingleLittleEndian(data[7..]),
                    BinaryPrimitives.ReadSingleLittleEndian(data[11..]),
                    BinaryPrimitives.ReadSingleLittleEndian(data[15..]),
                    BinaryPrimitives.ReadSingleLittleEndian(data[19..]),
                    BinaryPrimitives.ReadSingleLittleEndian(data[23..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(data[27..]));
                if (!IsFinite(m))
                {
                    return false;
                }

                frame = new StateFrame(kind, seq, default, m, default);
                return true;
            case StateKind.Mouse when data.Length == MouseLength:
                frame = new StateFrame(kind, seq, default, default, new MouseState(
                    BinaryPrimitives.ReadInt16LittleEndian(data[3..]),
                    BinaryPrimitives.ReadInt16LittleEndian(data[5..]),
                    (data[7] & 0x80) != 0,
                    (byte)(data[7] & 0x7F)));
                return true;
            case StateKind.Keepalive when data.Length == KeepaliveLength:
                frame = new StateFrame(kind, seq, default, default, default);
                return true;
            default:
                return false;
        }
    }

    private static bool IsFinite(in MotionState m) =>
        float.IsFinite(m.AccelX) && float.IsFinite(m.AccelY) && float.IsFinite(m.AccelZ) &&
        float.IsFinite(m.GyroX) && float.IsFinite(m.GyroY) && float.IsFinite(m.GyroZ);

    private static void WriteHeader(Span<byte> b, StateKind kind, ushort seq)
    {
        b[0] = (byte)kind;
        BinaryPrimitives.WriteUInt16LittleEndian(b[1..], seq);
    }
}

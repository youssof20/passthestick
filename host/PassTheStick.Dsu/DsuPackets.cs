using System.Buffers.Binary;
using System.IO.Hashing;
using PassTheStick.Protocol;

namespace PassTheStick.Dsu;

/// <summary>Cemuhook DSU protocol v1001 packet building and checking. All little-endian.</summary>
public static class DsuPackets
{
    public const ushort ProtocolVersion = 1001;
    public const uint TypeVersion = 0x100000;
    public const uint TypeInfo = 0x100001;
    public const uint TypeData = 0x100002;
    public const int HeaderLength = 16;
    public const int InfoPacketLength = 32;
    public const int DataPacketLength = 100;
    public const int MaxSlots = 4;

    /// <summary>Wraps a payload (without the 4-byte message type) in a header with length and CRC32.</summary>
    public static byte[] Frame(string magic, uint serverId, uint type, ReadOnlySpan<byte> payload)
    {
        var packet = new byte[HeaderLength + 4 + payload.Length];
        for (var i = 0; i < 4; i++)
        {
            packet[i] = (byte)magic[i];
        }

        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4), ProtocolVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), (ushort)(4 + payload.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), serverId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(16), type);
        payload.CopyTo(packet.AsSpan(20));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), Crc32.HashToUInt32(packet)); // CRC field is zero while hashing
        return packet;
    }

    public static byte[] VersionResponse(uint serverId)
    {
        var payload = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, ProtocolVersion);
        return Frame("DSUS", serverId, TypeVersion, payload);
    }

    public static byte[] InfoResponse(uint serverId, int slot, bool connected)
    {
        var payload = new byte[12];
        WriteSharedHeader(payload, slot, connected);
        return Frame("DSUS", serverId, TypeInfo, payload);
    }

    /// <summary>One controller-data packet. Buttons and sticks are neutral: only motion is driven by guests.</summary>
    public static byte[] DataResponse(uint serverId, int slot, uint packetNumber, ulong timestampMicros, in MotionState motion)
    {
        var p = new byte[80];
        WriteSharedHeader(p, slot, connected: true);
        p[11] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(12), packetNumber);
        p[20] = p[21] = p[22] = p[23] = 128; // sticks centered
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(48), timestampMicros);
        float[] values = [motion.AccelX, motion.AccelY, motion.AccelZ, motion.GyroX, motion.GyroY, motion.GyroZ];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(56 + (i * 4)), values[i]);
        }

        return Frame("DSUS", serverId, TypeData, p);
    }

    /// <summary>True when the packet has the right magic, version, length field and CRC32.</summary>
    public static bool IsValid(ReadOnlySpan<byte> packet, string expectedMagic)
    {
        if (packet.Length < HeaderLength + 4 || packet[0] != expectedMagic[0] || packet[1] != expectedMagic[1] || packet[2] != expectedMagic[2] || packet[3] != expectedMagic[3])
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(packet[4..]) != ProtocolVersion
            || BinaryPrimitives.ReadUInt16LittleEndian(packet[6..]) != packet.Length - HeaderLength)
        {
            return false;
        }

        var crc = BinaryPrimitives.ReadUInt32LittleEndian(packet[8..]);
        Span<byte> copy = stackalloc byte[packet.Length];
        packet.CopyTo(copy);
        copy[8] = copy[9] = copy[10] = copy[11] = 0;
        return Crc32.HashToUInt32(copy) == crc;
    }

    public static uint MessageType(ReadOnlySpan<byte> packet) => BinaryPrimitives.ReadUInt32LittleEndian(packet[16..]);

    private static void WriteSharedHeader(Span<byte> p, int slot, bool connected)
    {
        p[0] = (byte)slot;
        p[1] = (byte)(connected ? 2 : 0); // 2 = connected
        p[2] = 2; // model: full gyro
        p[3] = 2; // connection type: bluetooth
        p[10] = 0x05; // battery: full
    }
}

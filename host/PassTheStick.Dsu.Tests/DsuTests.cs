using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using PassTheStick.Dsu;
using PassTheStick.Protocol;

namespace PassTheStick.Dsu.Tests;

public class DsuPacketTests
{
    private static readonly MotionState Motion = new(0.1f, 1f, -0.2f, 10f, 90f, -5f, 0);

    [Fact]
    public void Packets_have_the_documented_sizes_and_valid_headers_and_crc()
    {
        var version = DsuPackets.VersionResponse(1);
        var info = DsuPackets.InfoResponse(1, 0, true);
        var data = DsuPackets.DataResponse(1, 0, 7, 123456, Motion);
        Assert.Equal(22, version.Length);
        Assert.Equal(DsuPackets.InfoPacketLength, info.Length);
        Assert.Equal(DsuPackets.DataPacketLength, data.Length);
        foreach (var p in new[] { version, info, data })
        {
            Assert.True(DsuPackets.IsValid(p, "DSUS"));
        }
    }

    [Fact]
    public void Data_packet_carries_slot_state_counter_timestamp_and_motion_at_the_documented_offsets()
    {
        var p = DsuPackets.DataResponse(1, 2, 9, 5_000_000, Motion);
        Assert.Equal(DsuPackets.TypeData, DsuPackets.MessageType(p));
        Assert.Equal(2, p[20]);
        Assert.Equal(2, p[21]);
        Assert.Equal(1, p[31]);
        Assert.Equal(9u, BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(32)));
        Assert.Equal(5_000_000ul, BinaryPrimitives.ReadUInt64LittleEndian(p.AsSpan(68)));
        Assert.Equal(0.1f, BinaryPrimitives.ReadSingleLittleEndian(p.AsSpan(76)));
        Assert.Equal(90f, BinaryPrimitives.ReadSingleLittleEndian(p.AsSpan(92)));
    }

    [Fact]
    public void A_flipped_bit_or_wrong_magic_or_truncation_is_rejected()
    {
        var p = DsuPackets.DataResponse(1, 0, 0, 0, Motion);
        Assert.False(DsuPackets.IsValid(p, "DSUC"));
        Assert.False(DsuPackets.IsValid(p.AsSpan(0, 50), "DSUS"));
        var flipped = (byte[])p.Clone();
        flipped[60] ^= 1;
        Assert.False(DsuPackets.IsValid(flipped, "DSUS"));
        Assert.False(DsuPackets.IsValid([], "DSUS"));
    }
}

public sealed class DsuServerTests : IDisposable
{
    private readonly DsuServer _server = new(0);
    private readonly UdpClient _client = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly IPEndPoint _target;

    public DsuServerTests()
    {
        _target = new IPEndPoint(IPAddress.Loopback, _server.Port);
        _server.Start();
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Dispose();
    }

    private async Task<byte[]> ReceiveAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        return (await _client.ReceiveAsync(cts.Token)).Buffer;
    }

    private async Task SendAsync(uint type, byte[] body) =>
        await _client.SendAsync(DsuPackets.Frame("DSUC", 42, type, body), _target);

    [Fact]
    public void Binds_to_loopback_only() => Assert.Equal(IPAddress.Loopback, _server.LocalEndPoint.Address);

    [Fact]
    public async Task Answers_version_and_slot_info_requests()
    {
        await SendAsync(DsuPackets.TypeVersion, []);
        var version = await ReceiveAsync();
        Assert.True(DsuPackets.IsValid(version, "DSUS"));
        Assert.Equal(DsuPackets.ProtocolVersion, BinaryPrimitives.ReadUInt16LittleEndian(version.AsSpan(20)));

        _server.SetConnected(1, true);
        var body = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(body, 2);
        body[4] = 0;
        body[5] = 1;
        await SendAsync(DsuPackets.TypeInfo, body);
        var first = await ReceiveAsync();
        var second = await ReceiveAsync();
        Assert.Equal((0, 0), (first[20], first[21]));
        Assert.Equal((1, 2), (second[20], second[21]));
    }

    [Fact]
    public async Task Subscribers_get_motion_and_sequence_numbers_increase()
    {
        await SendAsync(DsuPackets.TypeData, new byte[8]);
        for (var i = 0; i < 50 && _server.SubscriberCount == 0; i++)
        {
            await Task.Delay(10);
        }

        _server.Update(0, new MotionState(0, 1, 0, 0, 45, 0, 0));
        _server.Update(0, new MotionState(0, 1, 0, 0, 46, 0, 0));
        var a = await ReceiveAsync();
        var b = await ReceiveAsync();
        Assert.True(DsuPackets.IsValid(a, "DSUS"));
        Assert.Equal(45f, BinaryPrimitives.ReadSingleLittleEndian(a.AsSpan(92)));
        Assert.Equal(BinaryPrimitives.ReadUInt32LittleEndian(a.AsSpan(32)) + 1, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(32)));
    }

    [Fact]
    public async Task Nothing_is_sent_without_a_subscriber_and_garbage_is_ignored()
    {
        _server.Update(0, default);
        await _client.SendAsync(new byte[] { 1, 2, 3 }, _target);
        await _client.SendAsync(new byte[200], _target);
        await Task.Delay(100);
        Assert.Equal(0, _server.SubscriberCount);
        Assert.Equal(0, _client.Available);
    }

    [Fact]
    public void Out_of_range_slots_are_ignored()
    {
        _server.Update(-1, default);
        _server.Update(4, default);
        _server.SetConnected(9, true);
    }
}

public class DsuExpiryTests
{
    private sealed class Clock : TimeProvider
    {
        public long Now { get; set; }

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => Now;
    }

    [Fact]
    public async Task A_subscriber_that_stops_renewing_expires_after_five_seconds()
    {
        var clock = new Clock();
        using var server = new DsuServer(0, clock);
        server.Start();
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await client.SendAsync(DsuPackets.Frame("DSUC", 1, DsuPackets.TypeData, new byte[8]), new IPEndPoint(IPAddress.Loopback, server.Port));
        for (var i = 0; i < 50 && server.SubscriberCount == 0; i++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(1, server.SubscriberCount);
        clock.Now = 6000;
        server.Update(0, default);
        Assert.Equal(0, server.SubscriberCount);
    }
}

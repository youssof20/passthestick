using System.Buffers.Binary;
using System.IO.Hashing;
using System.Net;
using System.Net.Sockets;

const uint ServerId = 0x50545331;
const ushort Proto = 1001;
using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 26760));
var subs = new Dictionary<IPEndPoint, DateTime>();
uint packetNo = 0;
var cts = new CancellationTokenSource();

var serverTask = Task.Run(async () =>
{
    var pump = Task.Run(async () => { // 250 Hz motion push to subscribers
        float t = 0;
        while (!cts.IsCancellationRequested)
        {
            await Task.Delay(4);
            t += 0.004f;
            byte[] pkt = DataPacket(packetNo++, (ulong)(DateTime.UtcNow.Ticks / 10), 0f, 1f, 0f, 0f, 90f * MathF.Sin(t * 6.28f), 0f);
            lock (subs) foreach (var ep in subs.Keys.ToList()) server.Send(pkt, pkt.Length, ep);
        }
    });
    while (!cts.IsCancellationRequested)
    {
        UdpReceiveResult r;
        try { r = await server.ReceiveAsync(cts.Token); } catch { break; }
        var b = r.Buffer;
        if (b.Length < 20 || b[0] != 'D' || b[1] != 'S' || b[2] != 'U' || b[3] != 'C') continue;
        uint type = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(16));
        if (type == 0x100000) { var p = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(p, Proto); Send(r.RemoteEndPoint, type, p); }
        else if (type == 0x100001) { var p = new byte[12]; p[0] = 0; p[1] = 2; p[2] = 2; p[3] = 2; p[10] = 0x05; Send(r.RemoteEndPoint, type, p); }
        else if (type == 0x100002) lock (subs) subs[r.RemoteEndPoint] = DateTime.UtcNow;
    }
});

void Send(IPEndPoint ep, uint type, byte[] payload) { var pkt = Frame("DSUS", type, payload); server.Send(pkt, pkt.Length, ep); }

static byte[] Frame(string magic, uint type, byte[] payload)
{
    var pkt = new byte[20 + payload.Length];
    for (int i = 0; i < 4; i++) pkt[i] = (byte)magic[i];
    BinaryPrimitives.WriteUInt16LittleEndian(pkt.AsSpan(4), Proto);
    BinaryPrimitives.WriteUInt16LittleEndian(pkt.AsSpan(6), (ushort)(4 + payload.Length));
    BinaryPrimitives.WriteUInt32LittleEndian(pkt.AsSpan(12), 0x50545331);
    BinaryPrimitives.WriteUInt32LittleEndian(pkt.AsSpan(16), type);
    payload.CopyTo(pkt, 20);
    BinaryPrimitives.WriteUInt32LittleEndian(pkt.AsSpan(8), Crc32.HashToUInt32(pkt)); // crc field is zero while hashing
    return pkt;
}

static byte[] DataPacket(uint no, ulong tsUs, float ax, float ay, float az, float gp, float gy, float gr)
{
    var p = new byte[80];
    p[0] = 0; p[1] = 2; p[2] = 2; p[3] = 2; p[10] = 0x05; // slot 0, connected, full gyro, BT, battery full
    p[11] = 1;                                           // active
    BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(12), no);
    // 16..47: buttons, sticks (128 = centre), analog, touch: all neutral
    p[20] = 128; p[21] = 128; p[22] = 128; p[23] = 128;
    // (bytes 24..47 stay zero)
    BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(48), tsUs);
    var f = new[] { ax, ay, az, gp, gy, gr };
    for (int i = 0; i < 6; i++) BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(56 + i * 4), f[i]);
    return Frame("DSUS", 0x100002, p);
}

// ---- test client ----
using var client = new UdpClient(0);
var srv = new IPEndPoint(IPAddress.Loopback, 26760);
byte[] Req(uint type, byte[] body) { var q = Frame("DSUC", type, body); return q; }
await client.SendAsync(Req(0x100000, []), srv);
var v = await client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
Console.WriteLine($"version reply: {v.Buffer.Length} bytes, proto={BinaryPrimitives.ReadUInt16LittleEndian(v.Buffer.AsSpan(20))}");
var info = new byte[8]; info[0] = 1; info[4] = 0; // 1 port, slot 0
await client.SendAsync(Req(0x100001, info), srv);
var i1 = await client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
Console.WriteLine($"info reply: {i1.Buffer.Length} bytes (expect 32), state={i1.Buffer[21]} model={i1.Buffer[22]}");
await client.SendAsync(Req(0x100002, new byte[8]), srv);
int n = 0, bad = 0; uint last = 0; int gaps = 0; float maxGyro = 0;
var end = DateTime.UtcNow.AddSeconds(2);
while (DateTime.UtcNow < end)
{
    var r = await client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(1));
    var b = r.Buffer; uint crc = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8));
    var tmp = (byte[])b.Clone(); tmp[8] = tmp[9] = tmp[10] = tmp[11] = 0;
    if (Crc32.HashToUInt32(tmp) != crc) bad++;
    uint no = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(32));
    if (n > 0 && no != last + 1) gaps++;
    last = no; n++;
    maxGyro = Math.Max(maxGyro, Math.Abs(BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(20 + 56 + 16))));
}
Console.WriteLine($"data packets: {n} ({n / 2.0:F0}/s), len={100}, bad crc={bad}, seq gaps={gaps}, max |gyro yaw|={maxGyro:F1}");
cts.Cancel();

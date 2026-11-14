using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using PassTheStick.Protocol;

namespace PassTheStick.Dsu;

/// <summary>
/// Serves guest phone motion to emulators (Dolphin, Cemu and others) as a DSU controller on loopback.
/// Binds 127.0.0.1 only. Motion is pushed to subscribers whenever it changes, up to four slots.
/// </summary>
public sealed class DsuServer : IDisposable
{
    public const int DefaultPort = 26760;
    private static readonly TimeSpan SubscriptionLife = TimeSpan.FromSeconds(5);

    private readonly UdpClient _udp;
    private readonly TimeProvider _clock;
    private readonly uint _serverId = (uint)Random.Shared.Next();
    private readonly Dictionary<IPEndPoint, long> _subscribers = [];
    private readonly bool[] _connected = new bool[DsuPackets.MaxSlots];
    private readonly uint[] _packetNumber = new uint[DsuPackets.MaxSlots];
    private readonly long _startedAt;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private Task? _loop;

    public DsuServer(int port = DefaultPort, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _startedAt = _clock.GetTimestamp();
        _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
    }

    public int Port { get; }

    public IPEndPoint LocalEndPoint => (IPEndPoint)_udp.Client.LocalEndPoint!;

    public int SubscriberCount
    {
        get
        {
            lock (_gate)
            {
                return _subscribers.Count;
            }
        }
    }

    public void Start() => _loop ??= Task.Run(() => ListenAsync(_stop.Token));

    /// <summary>Marks a slot as connected or not; emulators see this in the controller list.</summary>
    public void SetConnected(int slot, bool connected)
    {
        if (slot is >= 0 and < DsuPackets.MaxSlots)
        {
            lock (_gate)
            {
                _connected[slot] = connected;
            }
        }
    }

    /// <summary>Sends the latest motion for a slot to every live subscriber.</summary>
    public void Update(int slot, in MotionState motion)
    {
        if (slot is < 0 or >= DsuPackets.MaxSlots)
        {
            return;
        }

        lock (_gate)
        {
            _connected[slot] = true;
            Expire();
            if (_subscribers.Count == 0)
            {
                return;
            }

            var micros = (ulong)(_clock.GetElapsedTime(_startedAt).TotalMicroseconds);
            var packet = DsuPackets.DataResponse(_serverId, slot, _packetNumber[slot]++, micros, motion);
            foreach (var endpoint in _subscribers.Keys)
            {
                _ = _udp.Send(packet, packet.Length, endpoint);
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _udp.Dispose();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // The receive loop ends with a cancellation or socket-closed exception.
        }

        _stop.Dispose();
    }

    private async Task ListenAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await _udp.ReceiveAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                if (token.IsCancellationRequested || ex is ObjectDisposedException)
                {
                    return;
                }

                continue; // a client went away mid-send (ICMP port unreachable): keep serving
            }

            Handle(received.Buffer, received.RemoteEndPoint);
        }
    }

    private void Handle(byte[] packet, IPEndPoint from)
    {
        if (!DsuPackets.IsValid(packet, "DSUC"))
        {
            return;
        }

        switch (DsuPackets.MessageType(packet))
        {
            case DsuPackets.TypeVersion:
                Send(DsuPackets.VersionResponse(_serverId), from);
                break;
            case DsuPackets.TypeInfo:
                AnswerInfo(packet, from);
                break;
            case DsuPackets.TypeData:
                lock (_gate)
                {
                    _subscribers[from] = _clock.GetTimestamp();
                    Expire();
                }

                break;
            default:
                break;
        }
    }

    private void AnswerInfo(byte[] packet, IPEndPoint from)
    {
        if (packet.Length < 24)
        {
            return;
        }

        var count = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(20)), 0, DsuPackets.MaxSlots);
        for (var i = 0; i < count && 24 + i < packet.Length; i++)
        {
            var slot = packet[24 + i];
            if (slot >= DsuPackets.MaxSlots)
            {
                continue;
            }

            bool connected;
            lock (_gate)
            {
                connected = _connected[slot];
            }

            Send(DsuPackets.InfoResponse(_serverId, slot, connected), from);
        }
    }

    private void Expire()
    {
        foreach (var (endpoint, seen) in _subscribers.ToArray())
        {
            if (_clock.GetElapsedTime(seen) > SubscriptionLife)
            {
                _subscribers.Remove(endpoint);
            }
        }
    }

    private void Send(byte[] packet, IPEndPoint to) => _ = _udp.Send(packet, packet.Length, to);
}

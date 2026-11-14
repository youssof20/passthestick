using System.Diagnostics;
using SIPSorcery.Net;

var cfg = new RTCConfiguration { iceServers = new List<RTCIceServer>() };
var host = new RTCPeerConnection(cfg);
var guest = new RTCPeerConnection(cfg);
host.onicecandidate += c => { if (c != null) guest.addIceCandidate(new RTCIceCandidateInit{ candidate=c.ToString(), sdpMid=c.sdpMid, sdpMLineIndex=c.sdpMLineIndex, usernameFragment=c.usernameFragment }); };
guest.onicecandidate += c => { if (c != null) host.addIceCandidate(new RTCIceCandidateInit{ candidate=c.ToString(), sdpMid=c.sdpMid, sdpMLineIndex=c.sdpMLineIndex, usernameFragment=c.usernameFragment }); };

var unreliable = await host.createDataChannel("state", new RTCDataChannelInit { ordered = false, maxRetransmits = 0 });
var reliable = await host.createDataChannel("control", new RTCDataChannelInit { ordered = true });
var guestState = new TaskCompletionSource<RTCDataChannel>();
guest.ondatachannel += dc => { if (dc.label == "state") guestState.TrySetResult(dc); };
var open = new TaskCompletionSource();
unreliable.onopen += () => open.TrySetResult();

var offer = host.createOffer(); await host.setLocalDescription(offer);
guest.setRemoteDescription(new RTCSessionDescriptionInit { sdp = offer.sdp, type = RTCSdpType.offer });
var answer = guest.createAnswer(); await guest.setLocalDescription(answer);
host.setRemoteDescription(new RTCSessionDescriptionInit { sdp = answer.sdp, type = RTCSdpType.answer });

var sw0 = Stopwatch.StartNew();
await open.Task.WaitAsync(TimeSpan.FromSeconds(15));
var gdc = await guestState.Task.WaitAsync(TimeSpan.FromSeconds(5));
Console.WriteLine($"connect+open: {sw0.ElapsedMilliseconds} ms");

// guest echoes; host measures RTT with 16-byte packets at 60 Hz
gdc.onmessage += (dc, proto, data) => dc.send(data);
var rtts = new List<double>(); var pending = new Dictionary<int, long>(); int seq = 0, got = 0;
unreliable.onmessage += (dc, proto, data) =>
{
    int s = BitConverter.ToInt32(data, 0);
    lock (pending) if (pending.Remove(s, out var t)) { rtts.Add((Stopwatch.GetTimestamp() - t) * 1000.0 / Stopwatch.Frequency); got++; }
};
var buf = new byte[16];
int total = 1200;
for (int i = 0; i < total; i++)
{
    BitConverter.TryWriteBytes(buf, seq);
    lock (pending) pending[seq] = Stopwatch.GetTimestamp();
    seq++;
    unreliable.send(buf);
    await Task.Delay(1000 / 60);
}
await Task.Delay(500);
rtts.Sort();
double P(double p) => rtts[(int)Math.Min(rtts.Count - 1, p * rtts.Count)];
Console.WriteLine($"sent {total} recv {got} loss {(1 - got / (double)total):P2}");
Console.WriteLine($"rtt ms: p50 {P(.5):F2} p95 {P(.95):F2} p99 {P(.99):F2} max {rtts[^1]:F2} mean {rtts.Average():F2}");
host.close(); guest.close();


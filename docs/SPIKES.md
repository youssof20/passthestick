# Phase 2 spike results (measured on this machine, Windows 10 LTSC, loopback)

Code lives in `spikes/`. Numbers are one run each, so treat them as direction, not benchmarks.

## UI framework (hello-world window, .NET 10, win-x64)

| Option | Exe size | Folder size | Window shown (ms, 6 runs) | Working set |
|---|---|---|---|---|
| WPF self-contained single file, compressed | 58.3 MB | 66 MB | 600-740 | 178 MB |
| WPF framework-dependent single file | 0.2 MB | 0.2 MB | 440-630 | 86 MB |
| Avalonia 12 self-contained, trimmed, compressed | 13.4 MB | 131 MB | 1240-1300 | 84 MB |
| WinUI 3 | not measured | | | |

Result: WPF starts about twice as fast as Avalonia here and the framework-dependent build is tiny. WinUI 3 was not tried (Windows App SDK packaging cost); revisit only if WPF theming blocks the design.

## WebRTC data channel in .NET (unordered, maxRetransmits 0, 16-byte echo at 60 Hz, 1200 packets)

| Library | Connect + open | RTT p50 / p95 / p99 | Loss |
|---|---|---|---|
| SIPSorcery 10.0.17 (pure C#) | 453 ms | 32.1 / 48.1 / 48.3 ms | 0% |
| DataChannelDotnet 1.3.1 (libdatachannel) | 1028 ms | 0.45 / 0.61 / 0.71 ms | 0% |

Result: libdatachannel is about 70x lower latency on loopback. SIPSorcery's 32 ms looks like timer-quantised SCTP sending, which is unacceptable for input. Open issues: the process exited with code 5 at teardown in the libdatachannel test (peer connections were not disposed; needs checking), and a lossy-link test is not done because Windows has no netem; plan a UDP impairment proxy in phase 8.

## Signaling on Cloudflare Workers + Durable Object (wrangler dev, hibernation API)

- Host and guest WebSocket relay worked; a second host on the same room was rejected with 409.
- Relay RTT locally: p50 19 ms, p95 68 ms, max 175 ms. This includes the dev server and is the fallback path only. Production numbers need a real deploy (needs your Cloudflare account).
- npm 11 blocks postinstall scripts; `npm install-scripts approve workerd esbuild` was needed.

## DSU (cemuhook) server

- Implemented version, controller-info and data messages in `spikes/DsuSpike`. A test client got correct reply sizes (info 32 bytes, data 100 bytes), valid CRC32 on every packet and no sequence gaps.
- Push rate was 64 packets/s, not the intended 250/s, because `Task.Delay(4)` rounds to the Windows timer tick (about 15.6 ms). Production needs a high-resolution timer or send-on-input-change.
- Not tested against Dolphin (not installed here).

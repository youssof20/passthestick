# Architecture

PassTheStick lets one person run a game and friends send input to it over the internet. Video stays in Discord. See `docs/protocol.md` for the wire format and `docs/SPIKES.md` for the measurements behind these choices.

## Components

```
 Guest browser (web client)  <--WebRTC data channels-->  Host app (Windows, .NET 10, WPF)
          \                                                  /
           \--WebSocket (signaling, relay fallback)---  Cloudflare Worker + Durable Object (one per room)
```

1. **Host app** (`src/Host`): sessions, input injection, virtual pads, DSU motion server, overlay, tray, LAN mode.
2. **Web guest** (`web/guest`): static page served at `/join`. Gamepad API, keyboard on a focused surface only, phone layouts, gyro.
3. **Signaling** (`server/worker`): TypeScript Worker, one Durable Object per room, WebSocket hibernation. A Node/Docker build of the same protocol (`server/selfhost`) for people who do not want Cloudflare.
4. **Site** (`site`): static landing page and docs on GitHub Pages.
5. **Native guest mode**: later, inside the host app, SDL3 controllers and a focused capture window.

## Host solution layout

| Project | Owns | Depends on |
|---|---|---|
| `PassTheStick.Protocol` | Message types, binary encode/decode, version constant. No I/O. | nothing |
| `PassTheStick.Core` | Session state machine (stick holder, epochs, turn modes, slots), input pipeline (validate, map, release-all), settings schema. Pure logic, fully unit tested. | Protocol |
| `PassTheStick.Platform` | Interfaces: `IInputInjector`, `IVirtualPadBackend`, `IDeviceHider`, `IWindowTracker`, `IOverlay`, `ITransport`. | Core |
| `PassTheStick.Windows` | Win32 implementations: SendInput injector, ViGEm backend (VIIPER later), HidHide, window tracker, hotkeys. | Platform |
| `PassTheStick.Net` | libdatachannel peer manager, signaling client, relay fallback, LAN endpoint. | Core, Platform |
| `PassTheStick.Dsu` | cemuhook UDP server. | Protocol |
| `PassTheStick.App` | WPF shell, view models (CommunityToolkit.Mvvm), `Microsoft.Extensions.Hosting` composition root, tray, overlay. No logic in code-behind. | all |
| `*.Tests` | xUnit tests per project. | |

Rule: `Core` never references Windows or WPF types. A Linux host would add `PassTheStick.Linux` (uinput) and a different shell without touching `Core`.

## Key flows

**Create session.** Host app calls `create` on the Worker, gets `code` + `hostToken`, stores the token in settings (user-profile, DPAPI-protected). Overlay and invite link show the code. Link is `https://<site>/join#CODE`; the code is in the fragment so it is not sent to the site's server.

**Join.** Guest opens the link, connects the signaling socket, receives `welcome`. Host and guest exchange SDP and ICE through `signal`. When both channels open the guest shows "Connected, direct" with RTT. If ICE fails in 8 s, try TURN; if that fails, switch to relay frames over the room socket.

**Pass the stick.** Host UI, hotkey, controller chord, timer or "I died" hotkey calls `Session.PassTo(id)`. The state machine bumps the epoch, runs `ReleaseAll()` on the injector and pad backend, emits `holder`, then re-focuses the game. The overlay animates. All of this is a single method with tests per trigger.

**Input.** Frames arrive on a transport thread, are decoded, checked against `holder`/`epoch`, then handed to a single input-pump thread that owns the injector and the pad backend. Nothing else touches them. The pump runs a 4 ms high-resolution loop for pad/motion and releases everything if the window is not the game or a 750 ms deadline passes.

**Resume.** Host restart: reads `hostToken`, reattaches within the 60 s grace window, state is restored from the room's `presence`. Guest blip: reconnects with `resumeToken` and backoff (0.5 s doubling to 8 s), UI shows "Reconnecting".

**Motion.** Phone gyro frames feed the DSU server on `127.0.0.1:26760`, slot per guest. Dolphin reads it as an alternate input source. Ship Dolphin profiles in `docs/emulators/`.

## Drivers

- Keyboard and mouse sessions need no driver.
- Pads: `IVirtualPadBackend`, ViGEmBus default (final release, still widely installed), VIIPER opt-in later.
- HidHide: optional. Before hiding, write a journal (`%LocalAppData%\PassTheStick\hidhide.json`) listing exactly what we added. On startup, if a journal exists, undo it. On exit and on uninstall, undo it. We never set HidHide inactive globally and never remove entries we did not add.

## Security posture

- Host and relay both enforce holder-only input. Tokens are hashed at rest. Rate and size limits on every socket.
- Guest keyboard capture lives on a focused page element, shows a visible "capturing" badge, and a panic key (Esc held 1 s) releases it. No global hooks on the guest.
- Logs are redacted (tokens, IPs of peers) before the diagnostics bundle is written.

## Open items for later phases

- Final choice of TURN provider (needs an account decision).
- Loss/jitter impairment proxy for network tests (phase 8).
- Whether the self-hosted server is Node or .NET.
- Teardown crash seen once in the libdatachannel spike; reproduce and fix before wiring it in.

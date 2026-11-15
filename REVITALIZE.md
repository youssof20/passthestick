# PassTheStick: full revitalization brief

You have full permission to rebuild this project from the ground up. Rewrite, delete, restructure, change frameworks, change hosting. The only goal is a product that is engineered properly end to end, looks and feels great, installs in one click, and works every time. Do not ask me for approval between phases. Make the call, write it down in `docs/DECISIONS.md` (decision, alternatives considered, why), and keep moving. Stop and ask only if something needs my accounts or money (Cloudflare login, code-signing certificate, domain purchase).

Read this whole file before doing anything.

---

## 0. Setup (do this first)

### 0.1 Skills and plugins

Check which of these are already available (`/plugin` list, `~/.claude/skills`, `.claude/skills`). Install whatever is missing. Prefer project-local installs under `.claude/skills/` so the setup is committed with the repo. If a `/plugin` slash command can't be run from inside your session, try the `claude plugin` CLI from the shell; if that doesn't exist either, fall back to `npx skills add ... --agent claude-code` or `git clone` into `.claude/skills/<name>`. If something still can't be installed, list the exact commands for me in `HANDOFF.md` and continue.

| Skill / plugin | Why | Install |
|---|---|---|
| **superpowers** (obra) | The working method: brainstorm, write plan, subagent-driven execution, TDD, systematic debugging, verification before completion | `/plugin install superpowers@claude-plugins-official` (or `/plugin marketplace add obra/superpowers-marketplace` then `/plugin install superpowers@superpowers-marketplace`) |
| **frontend-design** (Anthropic) | Distinctive visual direction for the website, web guest client and app | `/plugin install frontend-design@claude-plugins-official` |
| **ui-ux-pro-max** | Design system generation: palettes, type pairing, UX rules, accessibility checks | `/plugin marketplace add nextlevelbuilder/ui-ux-pro-max-skill` then `/plugin install ui-ux-pro-max@ui-ux-pro-max-skill` (needs Python 3) |
| **code-review** (Anthropic) | Parallel review agents before each milestone merge | `/plugin install code-review@claude-plugins-official` |
| **security-guidance** (Anthropic) | This app injects input, hooks the keyboard, installs drivers and talks to the internet. Security matters | `/plugin install security-guidance@claude-plugins-official` |
| **context7** | Current docs for .NET, WPF/WinUI, SIPSorcery, Velopack, Cloudflare Workers, SDL3. Do not code against stale APIs from memory | `/plugin install context7@claude-plugins-official` |
| **humanizer** (blader) | Anti-AI-writing pass on every piece of user-facing text | `git clone https://github.com/blader/humanizer.git .claude/skills/humanizer` |
| **web-quality-skills** (addyosmani) | Performance, Core Web Vitals, accessibility and SEO for the site | `npx skills add addyosmani/web-quality-skills --agent claude-code` |
| **claude-seo** (AgriciDaniel) | Full SEO audit, schema / JSON-LD, sitemap, GEO for AI search | `/plugin marketplace add AgriciDaniel/claude-seo` then `/plugin install claude-seo@AgriciDaniel-claude-seo` (name may be lowercase `agricidaniel-claude-seo`; check `/plugin` Discover) |

Use them actively, not decoratively:
- superpowers drives the whole process (brainstorm, plan, execute, verify).
- ui-ux-pro-max + frontend-design produce the design system in phase 3.
- humanizer runs on the README, website copy, in-app strings, installer text, release notes and docs before they are committed.
- code-review + security-guidance run at the end of every phase.
- web-quality-skills + claude-seo audit the site in phase 6.

### 0.2 Branch and safety
- Work on a new branch `revamp`. Commit at every green milestone with clear messages. Never force-push `main`.
- Keep the old code reachable via a git tag `legacy-v0.1.29` before deleting anything.

### 0.3 Create `CLAUDE.md` at the repo root
Write it once the architecture is decided (end of phase 2), then keep it current. Keep it under ~200 lines. Include:
- One-paragraph product description and the core user stories (section 1 below).
- Repo map: every project/folder and what it owns.
- Exact commands: build, test, run host, run web guest locally, run signaling server locally, build installer, build site.
- Architecture rules: layering, no logic in code-behind, where platform-specific code lives, the protocol versioning rule, the "input is only accepted from the current stick holder" invariant, the "every held key/button is released on any state change" invariant.
- Code rules: nullable on, warnings as errors, analyzers on, no dead code, no commented-out code, no `async void` except event handlers, cancellation tokens on all async I/O, structured logging.
- Design rules: tokens live in one source file; never hard-code a color, radius, spacing or font in app or site.
- Writing rules (section 7).
- Definition of done (section 9).

---

## 1. What this product is

PassTheStick lets friends play a game together over the internet when only one person has the game. The host runs the game. Guests send their controller or keyboard input to the host's PC. Everyone watches the host's Discord screenshare. No video encoding, no streaming service, no account.

Today's core loop is turn-based: one player "holds the stick," the host passes it. That is the soul of the app and stays the headline feature. But the concept is bigger than that.

### Prior art (know it, position against it)
- **Pop** (pop.com, from the Screenhero founders): multiplayer screen sharing where everyone controls one screen with their own mouse and keyboard. Built for pair programming, not games. This is the "P" thing the concept resembles.
- **Steam Remote Play Together**: streams video and routes guest input, but only for Steam games that are flagged as supporting it, and the host must be on Steam.
- **Parsec**: low-latency remote desktop and game streaming, now owned by Unity and increasingly enterprise-focused.

PassTheStick's position: input-only, works with **any** window (emulators, non-Steam games, old games, single-player games), uses the video stream people already have open in Discord, costs nothing, open source. Say this honestly on the website.

### User stories that must feel great
1. **Couch Wii Bowling over the internet.** Host runs Dolphin with Wii Sports. Four friends on Discord. Each takes a turn bowling. A guest uses their phone as the Wii Remote (gyro swing + pointer), or a controller with gyro, or plain buttons. Turn passes automatically or with one tap.
2. **Souls deathless relay / horror game "pass on death."** Host passes the stick each time someone dies. One hotkey or a controller chord, no alt-tab.
3. **Real co-op.** A local-multiplayer game (Overcooked-style, fighting game, emulated N64/GameCube party game). Each guest gets their own virtual controller at the same time.
4. **Twitch/Discord party.** Many viewers queue for a turn, host approves, timer per turn.
5. **First-timer friend.** Gets a link in Discord, clicks it, is in, no install, no drivers, no port forwarding, plays in under 30 seconds.

---

## 2. What is wrong today (verified by reading the code)

Fix all of these. Confirm each one in the code first, then fix it with a test.

**Networking and security**
- `server.js`: `HOST_REJOIN` lets **any** client claim host of any room code that exists or not. Anyone who guesses a code becomes host and receives every guest's keystrokes. Needs a host secret token issued at creation.
- Room codes come from `Math.random()`, 4 characters, no collision check. Use a CSPRNG, an unambiguous alphabet (no 0/O/1/I/L), check collisions, rate-limit join attempts.
- The relay forwards `KEY_EVENT` / `PAD_STATE` from **any** guest. The relay and the host must both enforce that only the current stick holder (or the assigned co-op slot) can send input.
- No message size limits, no schema validation, no rate limiting, no protocol version, no auth on anything.
- All traffic, including 60 Hz gamepad state, is JSON over a single TCP WebSocket through a relay. Head-of-line blocking adds latency spikes under packet loss, and every input pays the round trip to the relay's region.
- The cloud relay is a Render free web service. Free services spin down after 15 minutes without traffic and take around a minute to cold start, so the first session of the evening often looks broken.
- Host disconnect deletes the room; a crash or Wi-Fi blip ends the session for everyone.

**Privacy**
- The guest's `KeyboardCapture` is a global low-level hook: while the guest holds the stick, **every** keystroke on their PC (typing in Discord, passwords) is sent to the host, and keys are not swallowed locally. Input capture must be scoped to an explicit focused capture surface, with an obvious indicator and a panic key.

**Drivers and dependencies**
- ViGEmBus was retired and archived in November 2023 (v1.22.0 is final). It still works and is still the most widely installed option, but it gets no fixes. Successors: VIIPER (open source, userspace over USB/IP with the signed usbip-win2 driver, GPL server you run as a separate process with a TCP API; DS4Windows 5 uses it) and Nefarius VirtualPad (commercial).
- HidHide is maintained (NuGet `Nefarius.Drivers.HidHide` 3.4.0, May 2026). If the app crashes while the host's physical pad is hidden, the host's controller stays hidden. That must never happen.
- Guest controller capture uses SharpDX XInput. SharpDX is abandoned, and XInput only sees Xbox-style pads (no DualSense/Switch Pro, no gyro).
- The installer bundles a whole Node.js runtime just for an optional LAN relay. Bloat.

**App**
- `HostPage.xaml.cs` is ~1,800 lines of code-behind. No MVVM, no DI, no tests.
- No mouse support, no motion support.
- Overlay disappears in exclusive fullscreen.
- Inno Setup installer, unsigned binaries (SmartScreen warnings), no auto-update.
- The UI is visually dated and inconsistent.

---

## 3. Target architecture (my recommendation; you may change it with reasons in DECISIONS.md)

Verify every version and API with context7 or the web before relying on it.

### 3.1 Components
1. **Host app (Windows, .NET 10 LTS).** The only thing that must be installed. Owns sessions, input injection, virtual controllers, the overlay, the tray, the DSU motion server, and LAN mode.
   - UI: either WPF on .NET 10 with the Fluent theme, or WinUI 3, or Avalonia. Pick one after a short spike. Criteria: startup time, installer size, theming control, overlay/topmost behavior, and whether a future Linux/Steam Deck host is realistic. Whatever you pick: MVVM (CommunityToolkit.Mvvm), `Microsoft.Extensions.Hosting` for DI, logging and config.
   - Every OS-specific capability behind an interface: `IInputInjector`, `IVirtualPadBackend`, `IDeviceHider`, `IWindowTracker`, `IOverlay`. Windows implementations now; the seams let a Linux (uinput) host exist later.
2. **Web guest client (browser, zero install).** The default way to join. Opens from a link like `https://<site>/join#ABC123`. Works on desktop and phone browsers.
   - Desktop: Gamepad API for controllers, keyboard capture only while the page is focused, optional pointer lock for mouse.
   - Phone: on-screen controller layouts (Xbox, Wii Remote + Nunchuk, simple 4-button), `DeviceMotion`/`DeviceOrientation` for gyro and accelerometer, haptics where supported, wake lock, fullscreen, landscape handling, iOS motion-permission prompt.
   - Rumble from the host's virtual pad goes back to the guest (Gamepad `vibrationActuator` or phone vibration).
3. **Native guest mode (optional, inside the same app).** For people who want lower-level capture or better controller support. Uses SDL3 (via a maintained C# binding) for controllers so DualSense, Switch Pro, 8BitDo and gyro all work. Keyboard capture only through a focused capture window, never a global hook.
4. **Signaling + fallback relay (Cloudflare Workers + Durable Objects).** One Durable Object per room. WebSocket hibernation so idle rooms cost nothing and there are no cold starts like Render. Handles room creation, join, host tokens, presence, and relays traffic only when peer-to-peer fails. TypeScript, tested with the Workers test tooling. Self-hostable alternative: the same protocol in a small Node or .NET server with a Dockerfile, for people who don't want Cloudflare.
5. **Website (GitHub Pages).** Landing page, docs, download, and the web guest client at `/join`. Static. A static-site generator like Astro is fine if it stays fast and simple; plain HTML/CSS is also fine.

### 3.2 Networking
- **WebRTC data channels, peer-to-peer**, host to each guest, with the Durable Object as the signaling server. In .NET, evaluate SIPSorcery first (pure C#); compare against a libdatachannel binding if SIPSorcery's data channel latency or stability isn't good enough. Measure, don't guess.
- Two channels per guest:
  - `state`: unordered, unreliable (`maxRetransmits: 0`). Gamepad + motion + pointer state as full snapshots with a sequence number. Latest wins, stale packets are dropped. Send on change plus a keepalive at a low rate.
  - `control`: ordered, reliable. Key down/up events, stick passing, chat/emotes, settings, rumble feedback.
- STUN for NAT traversal, TURN fallback (find a free or cheap TURN option and document it), then the Durable Object WebSocket relay as the last resort. Show the user which path they are on (Direct / TURN / Relay) and the measured RTT.
- **LAN mode**: host discovers and serves guests on the local network directly (mDNS or UDP broadcast + a small built-in WebSocket/WebRTC endpoint inside the host app). No Node, no internet needed.
- Compact binary encoding for the hot path (hand-rolled little-endian struct or MessagePack). JSON is fine for low-rate control messages if it keeps the code simpler.
- A single protocol spec in `docs/protocol.md`, generated or shared types between C# and TypeScript so they cannot drift. Protocol version in the handshake; mismatched versions get a clear "update your app" message.
- Session resume: host and guests get resume tokens. A host app restart or a network blip within a grace window (say 60 s) resumes the same room, same players, same stick holder. Guests auto-reconnect with backoff and a visible "reconnecting" state.
- Server-authoritative stick state on the host (the host app is the authority; the relay just enforces that only the holder's input is forwarded).
- Abuse limits: max players per room, message rate caps, payload size caps, join attempt throttling, room expiry.

### 3.3 Input and drivers
- **Keyboard**: scan-code injection with layout mapping (keep what works from today, cover AZERTY/QWERTZ guests and hosts with tests).
- **Mouse**: relative and absolute pointer, clamped to the pinned game window. Needed for Dolphin's IR pointer, point-and-click games and menus.
- **Virtual controllers**: `IVirtualPadBackend` with ViGEmBus as the default (stable, widely installed, final WHQL release) and VIIPER as an opt-in experimental backend. Detect what's installed, install the chosen backend silently with a pinned version and checksum, explain reboots clearly. Keyboard-only sessions require **no** drivers.
- **Co-op mode**: one virtual pad per guest slot, mapped to player 1-4 in a predictable order, with a "swap slots" UI.
- **Motion**: run a **DSU (cemuhook protocol) server** on the host on `127.0.0.1:26760`. Feed it from guest phone gyro/accelerometer and from SDL3 gyro controllers. Dolphin, Cemu and other emulators read DSU as an "alternate input source." This is what makes Wii Bowling with a phone actually work. Ship a guide and ready-made Dolphin controller profiles for "phone as Wii Remote" and "controller with gyro."
- **HidHide** (optional, advanced): hide the host's physical controller while a guest drives, so the game only sees the virtual pad. Cleanup must be guaranteed: restore on normal exit, on crash (watchdog or a startup check that restores any leftover state), and on uninstall.
- **Stuck input**: on stick pass, disconnect, focus loss, pause or app exit, release every held key, button and trigger and neutralize sticks. Test this exhaustively.
- **Focus**: injection only targets the pinned window. If the game loses focus, pause injection and tell everyone on the overlay and in the guest UI.
- Keep the anti-cheat warning: virtual devices and injection can trip kernel anti-cheat. Online/competitive modes are out of scope and the app says so.

### 3.4 Host experience and QoL
- **Turn modes**: Manual pass, Round-robin with an optional per-turn timer, Pass-on-hotkey ("I died"), Request-the-stick (guest asks, host approves with one key), Co-op (everyone at once), and Spectator/queue for big groups.
- **Hotkeys that work from the couch**: keyboard chords plus a controller chord on the host's own pad (for example Guide/Home + shoulder). Rebindable, conflict detection.
- **Game picker**: auto-detect common emulators and launchers (Dolphin, PCSX2, RPCS3, RetroArch, Cemu, DuckStation, Steam games) and remember the last game. Per-game profiles: preferred mode, mappings, motion on/off.
- **Overlay**: who has the stick, queue, timer, connection quality. Works in borderless; detects exclusive fullscreen and suggests switching. Never steals focus.
- **Invite flow**: one big "Copy invite link" button that produces the web link. Also a `passthestick://join/CODE` deep link for native guests, and Discord Rich Presence with a Join button if it's practical.
- **Input test screen**: before playing, each guest sees their input arrive on the host in real time (a little controller diagram lights up). Kills most "it doesn't work" support questions.
- **First-run wizard** that's short: pick game window, choose keyboard-only or controllers, install drivers only if needed, done.
- **Diagnostics**: one button exports a redacted log bundle for bug reports. Clear, human error messages with a next step for every failure.
- **Sounds and haptics**: subtle, optional, for "you have the stick," "your turn in 10 s," and passing.
- **Elevation**: explain why admin is needed (only when the game itself runs elevated) instead of always asking.
- **Settings** persisted safely (atomic writes, versioned schema, migration).
- Strings in resource files so translation is possible later.

### 3.5 Install, update, release
- Replace Inno Setup with **Velopack** (or justify another choice): one-click install to the user profile, delta auto-updates from GitHub Releases, no admin unless installing drivers.
- Also publish a **winget** manifest and a portable zip.
- Trimmed, ready-to-run single file. Measure installer size and cold start time before and after, and record them in the README.
- **Code signing**: set up the CI hooks for SignPath (free signing for qualifying open-source projects) or Azure Trusted Signing. I'll handle the account side; document the steps in HANDOFF.md.
- CI (GitHub Actions, windows-latest): build, test, lint, package on every PR; release on tag; deploy site to Pages; deploy the Worker with `wrangler` when a token secret exists.
- Delete the bundled Node relay from the installer entirely.

---

## 4. Process (use superpowers)

Run these phases in order. Each phase: brainstorm briefly, write a plan in `docs/plans/NN-<name>.md`, execute with subagents where tasks are independent, run the test suite, run code-review and security review, commit.

1. **Audit.** Read every file. Produce `docs/AUDIT.md`: confirmed bugs, security issues, dead code, what's worth keeping (the scan-code mapping, window tracking, focus logic and HidHide handling may have hard-won edge-case knowledge; port the knowledge, not necessarily the code).
2. **Architecture + spikes.** UI framework spike, WebRTC-in-.NET spike (measure RTT and jitter on loopback and with simulated loss), Durable Object signaling spike, DSU server spike against Dolphin's input config. Write `docs/ARCHITECTURE.md`, `docs/protocol.md`, `docs/DECISIONS.md`, then `CLAUDE.md`.
3. **Identity and design system.** See section 5. Output: `design/tokens.json` as the single source, generated into the app's resource dictionary and the site's CSS custom properties, plus `design/BRAND.md`.
4. **Core rebuild.** Protocol library, signaling server, host core (session state machine, input pipeline, drivers behind interfaces), web guest client. Test-first for the state machine, protocol encoding, input mapping and stuck-input release.
5. **Host UI rebuild.** Every screen redone with the design system: launcher, host lobby, game picker, live session, guest view, settings, wizard, overlay, tray, error states, empty states, loading and reconnecting states.
6. **Website + docs + SEO.** See section 6.
7. **Installer, updater, CI, release pipeline.**
8. **Hardening pass.** Edge-case matrix (section 8), chaos tests on the network, a full manual-test script in `docs/MANUAL_TEST.md` for what can't be automated (real games, real drivers).
9. **Final review.** code-review over the whole diff, security review, humanizer pass on all text, update README, CLAUDE.md, and write `HANDOFF.md` (what I need to do: accounts, secrets, signing, first release steps, anything you could not verify on this machine).

---

## 5. Design and identity

Use ui-ux-pro-max to generate the design system and frontend-design to push it somewhere memorable. Same tokens and the same voice in the app, the web guest client, the site, the installer and the README images.

Direction to start from (you can push past it):
- The feeling is a living room on game night, not an esports dashboard. Warm, tactile, a bit playful, confident. Think the physical object of a controller being handed across a couch.
- The "stick" is the brand object. The handoff moment (passing the stick) gets a signature animation that is fast (under 300 ms), satisfying, and works with reduced-motion settings.
- Avoid the generic AI look: no purple-to-blue gradients, no glassmorphism everywhere, no neon "gamer" clichés, no stock 3D blobs, no emoji as icons.
- One display typeface with character for headings and the room code, one highly legible UI face. The room code is huge, monospaced or tabular, and impossible to misread.
- Dark and light themes from the same tokens. WCAG AA contrast minimum everywhere. Full keyboard navigation in the app and site. Visible focus states.
- Redraw the logo and app icon if the current one doesn't fit the new identity. Produce the icon at every size Windows needs plus favicons and an Open Graph image.
- Consistency check at the end: screenshot every screen and page side by side and fix anything that drifts from the tokens.

---

## 6. Website (GitHub Pages)

Purpose: someone lands from a search or a Discord link, understands it in five seconds, downloads in one click, or joins a game in one click.

- **Hero**: one sentence on what it does, one primary button "Download for Windows" that resolves to the latest release asset through the GitHub API (with a fallback link to the Releases page), and a secondary "Join a game" field for a room code.
- A short looping demo (recorded or animated) showing the stick being passed.
- **How it works** in three steps. **Use cases** with the stories from section 1 (Wii Bowling on Dolphin, deathless relay, co-op party games, stream parties). **Honest comparison** with Steam Remote Play Together and Parsec: when to use which. **FAQ** (anti-cheat, latency, drivers, privacy, does my friend need to install anything). **Docs**: setup, emulator guides with Dolphin motion setup, self-hosting the signaling server, troubleshooting.
- `/join` hosts the web guest client.
- SEO and quality: semantic HTML, unique titles and meta descriptions, Open Graph and Twitter cards, `SoftwareApplication` JSON-LD with operatingSystem, applicationCategory, offers price 0 and a link to the license, `sitemap.xml`, `robots.txt`, canonical URLs, fast LCP, no layout shift, no heavy JS on the landing page, images in modern formats with dimensions set. Target keywords people actually search: "play single player games with friends online," "pass the controller online," "local co-op over internet without Steam," "Dolphin netplay alternative," "Wii Sports online with friends." Run claude-seo and web-quality-skills audits and fix what they flag. Lighthouse 95+ on all four categories.
- GitHub repo polish: description, topics, social preview image, README that mirrors the site (hero image, one-line pitch, download badge, quick start, link to docs), CONTRIBUTING.md, issue templates, SECURITY.md.

---

## 7. Writing rules (all user-facing text)

Run humanizer on everything users read. In addition:
- Plain, specific, short. Say what happens, then stop.
- Banned: "seamless," "effortless," "elevate," "unleash," "supercharge," "robust," "cutting-edge," "game-changer," "dive in," "in today's world," "whether you're X or Y," rule-of-three filler lists, rhetorical questions as headings, exclamation marks in UI, emoji in docs and UI copy, em dashes used as decoration.
- No hype taglines on every section. One good line in the hero is enough.
- Error messages say what went wrong and what to do next, in one or two sentences.
- Write like a person who made a tool for their friends and is proud of it.

---

## 8. Edge cases that must be handled (and tested)

Networking: host loses Wi-Fi for 10 s; guest on mobile data with 5% packet loss; symmetric NAT on both sides (TURN or relay path); guest joins a full room; wrong or expired code; two hosts with the same code (impossible by construction, test it); host app restarts mid-session; signaling server restarts; protocol version mismatch; clock skew; a malicious client sending oversized, malformed or flood traffic; a guest spoofing another guest's id.

Input: stick passed while keys or buttons are held; guest disconnects mid-press; game window closed or minimized; game runs elevated and host doesn't; exclusive fullscreen; multiple monitors and DPI scaling; host and guest with different keyboard layouts; controller unplugged and replugged on either side; two guests in co-op with the same controller model; a host with no ViGEm/VIIPER installed choosing keyboard-only; HidHide left enabled after a crash; app crash during driver install; reboot pending.

Web guest: iOS Safari motion permission flow; phone screen locking; tab backgrounded; browser without Gamepad API support; controller connected after the page loaded; landscape/portrait switch; very small screens.

App: first run with no settings file; corrupted settings file; running two instances; update applied while a session is active (defer it); uninstall cleanup (drivers left alone unless the user asks, our own settings and HidHide rules removed).

---

## 9. Definition of done

- Fresh Windows machine: download from the site, install, host a session, and a friend joins from a phone browser and bowls in Wii Sports on Dolphin with gyro. Documented in `docs/MANUAL_TEST.md` and run as far as this machine allows.
- `dotnet test`, the web client tests and the Worker tests all pass in CI. Coverage on the protocol, session state machine and input pipeline is high and meaningful.
- No compiler warnings, no analyzer warnings, no TODOs left in code.
- Installer size, idle CPU, memory, and input latency (loopback and simulated WAN) measured and published in the README.
- Every screen and page uses only design tokens and passes AA contrast.
- All user-facing text has been through humanizer and the writing rules.
- `CLAUDE.md`, `README.md`, `docs/` and `HANDOFF.md` are current.
- Everything committed on `revamp`, with a summary PR description of what changed and why, ready for me to review and merge.

Start with phase 0, then phase 1. Go.

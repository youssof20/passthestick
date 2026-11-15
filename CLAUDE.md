# CLAUDE.md

PassTheStick lets one person run a game and friends send keyboard, controller, mouse and phone-motion input to it over the internet while everyone watches the host's Discord screenshare. Windows host app (.NET 10, WPF), browser guest client, Cloudflare Worker for signaling. Input only, no video, no account, any game window.

User stories: (1) Wii Bowling on Dolphin with a phone as the remote; (2) pass the stick on death in a Souls relay; (3) real co-op with one virtual pad per guest; (4) party with a queue and turn timer; (5) a friend joins from a link in under 30 seconds with nothing installed.

Status: rebuild in progress on branch `revamp`. Phases 0-1 done (`docs/AUDIT.md`), phase 2 spikes done (`docs/SPIKES.md`), architecture written. The legacy code under `src/` is tagged `legacy-v0.1.29` and is being replaced. Read `REVITALIZE.md` for the full brief and `HANDOFF.md` for what needs the owner.

## Repo map (target)

- `host/PassTheStick.{Protocol,Platform,Core,Windows,Net,Dsu,App}` host solution, see `docs/ARCHITECTURE.md`
- `server/worker` Cloudflare Worker + Durable Object; `server/selfhost` same protocol for self-hosting
- `web/guest` browser guest client; `site` landing page and docs
- `design/tokens.json` single source for colors, type, spacing; generated into WPF resources and site CSS
- `protocol/schema.json` single source for message types, generated for C# and TS
- `docs/` AUDIT, ARCHITECTURE, protocol, DECISIONS, SPIKES, MANUAL_TEST; `spikes/` throwaway experiments
- `.claude/skills/` project-local skills (superpowers, frontend-design, ui-ux-pro-max, humanizer, web-quality, claude-seo)

## Commands

Windows, PowerShell. If `dotnet` is not found in a fresh shell, refresh PATH first.

```
cd host; dotnet test                              # all host tests, analyzers as errors
cd server/worker; npm ci; npx wrangler dev        # signaling server locally (port 8787)
cd server/worker; npm test                        # Worker tests
cd web/guest; npm test; node build.mjs            # web guest tests; bundle into site/join
# End-to-end (host + Worker + libdatachannel guest): run `npx wrangler dev --port 8787` in server/worker, then
#   $env:PTS_WORKER_URL='http://127.0.0.1:8787'; cd host; dotnet test --filter Category=Integration
# Without PTS_WORKER_URL that test returns immediately, so a green run alone proves nothing about it.
```

Release build: see `.github/workflows/release.yml` (publish self-contained win-x64, then `vpk pack`). Version lives in `host/Directory.Build.props`. The old Inno Setup installer and Node relay are gone. NuGet source `nuget.org` must be configured (`dotnet nuget add source https://api.nuget.org/v3/index.json -n nuget.org`). npm 11 blocks postinstall scripts: approve `workerd` and `esbuild` with `npm install-scripts approve`.

## Architecture rules

- Layering: Protocol <- Platform <- Core <- Windows/Net <- App. `Core` has no Win32 or WPF types.
- No logic in code-behind. View models via CommunityToolkit.Mvvm, DI and logging via `Microsoft.Extensions.Hosting`.
- Platform code sits behind `IInputInjector`, `IVirtualPadBackend`, `IDeviceHider`, `IWindowTracker`, `IOverlay`.
- Protocol: versioned in `hello`/`welcome`; mismatch gets a clear "update your app" error. Change `protocol/schema.json`, regenerate, never hand-edit generated types.
- Invariant 1: input is accepted only from the current stick holder (or the guest's co-op slot), checked on the relay and on the host, with epochs.
- Invariant 2: every held key, button and trigger is released and sticks centered on any holder change, disconnect, focus loss, pause, timeout or exit. Each trigger has a test.
- One input-pump thread owns the injector and pad backend.
- HidHide: journal every change, undo from the journal at startup, exit and uninstall; never touch entries we did not add.

## Code rules

Nullable on, warnings as errors, analyzers on, no dead or commented-out code, no TODOs, no `async void` except event handlers, `CancellationToken` on all async I/O, structured logging (`ILogger`), no empty `catch`. Atomic settings writes with a versioned schema and migration. Strings in resource files.

## Design rules

All color, radius, spacing and type come from `design/tokens.json`. Never hard-code them in the app or site. Light and dark from the same tokens, WCAG AA, full keyboard use, visible focus. No emoji as icons, no purple-blue gradients.

## Writing rules

Plain, specific, short. Banned: seamless, effortless, elevate, unleash, supercharge, robust, cutting-edge, game-changer, dive in, "whether you're X or Y", rhetorical-question headings, exclamation marks in UI, emoji in docs and UI, decorative em dashes. Errors say what failed and what to do next. Run the humanizer skill on user-facing text before committing.

## Definition of done

Tests pass for the host, the web client and the Worker in CI; no warnings; every screen uses tokens and passes AA; text humanized; installer size, idle CPU, memory and input latency measured and in the README; `docs/MANUAL_TEST.md` run as far as this machine allows; `CLAUDE.md`, `README.md`, `docs/` and `HANDOFF.md` current; everything committed on `revamp` with a PR summary.

Run the app: `dotnet run --project host/PassTheStick.App` (needs a Worker; see the server address field, default http://127.0.0.1:8787).
Regenerate tokens after editing design/tokens.json: `node scripts/gen-design.mjs`. Windows do not pick up implicit styles: give each window Style="{StaticResource PtsWindow}".

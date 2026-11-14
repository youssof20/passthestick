# Decisions

Format: decision, alternatives considered, why.

## D1. Skills installed by cloning into `.claude/skills/`

- **Decision:** superpowers, frontend-design, ui-ux-pro-max, humanizer, web-quality-skills and claude-seo are copied into `.claude/skills/`. context7 is configured in `.mcp.json`.
- **Alternatives:** `/plugin install` (needs the `claude` CLI, which is not on PATH here); `npx skills add` (would write to the same place).
- **Why:** project-local and committed, so the setup travels with the repo.
- **Not installed as plugins:** `code-review` and `security-guidance` are Claude Code plugins made of commands and hooks, not skills. The session already has `code-review` and `security-review` built in, so the plugin copies are not needed. The `security-guidance` hook sources were not vendored.
- **Needs a human:** if you want the real plugin installs, run the `/plugin install` lines from `REVITALIZE.md` once in a normal Claude Code session.

## D2. Branch and tag

- **Decision:** work on `revamp`; tag `legacy-v0.1.29` points at the last release commit on `main` before any deletion.

## D3. Host UI: WPF on .NET 10

- **Alternatives:** Avalonia 12, WinUI 3.
- **Why:** measured startup about 2x faster than Avalonia and a very small framework-dependent build (see `docs/SPIKES.md`). Linux host stays possible through the platform interfaces.

## D4. Host WebRTC: libdatachannel via DataChannelDotnet, not SIPSorcery

- **Alternatives:** SIPSorcery 10.0.17.
- **Why:** loopback RTT p50 0.45 ms against 32 ms (see `docs/SPIKES.md`). Risk: a one-maintainer wrapper; the raw C bindings are included as a fallback. Teardown crash to be checked.

## D5. Signaling: Cloudflare Worker + one Durable Object per room, hibernation API

- Works locally. Host tokens, resume tokens and rate limits are still to design in `docs/protocol.md`.

## D6. Visual identity: putty + ink + handoff yellow, not cream + terracotta

- **Alternatives:** warm cream with a terracotta accent (the brief's "living room" direction, but it is the most common generated look); dark with a neon accent (the gamer cliché the brief rules out).
- **Why:** console-plastic grey with a single lit yellow button keeps the "game night" warmth, is specific to the stick idea, and the lit button doubles as the holder indicator. Yellow fails as text on light, so a separate `accentText` token exists and the validator enforces it.
- **Fonts:** Familjen Grotesk, Atkinson Hyperlegible Next, JetBrains Mono. Not yet bundled or visually verified in the app.

## D7. Tokens in JSON with a contrast gate

- **Decision:** `design/tokens.json` is the only source; `scripts/gen-design.mjs` generates WPF dictionaries and site CSS and exits non-zero on any WCAG AA miss. First run caught the light border at 2.77:1; fixed.

## D8. New host code lives in `host/`, legacy stays in `src/` until phase 5

- **Why:** the legacy projects target net8 with relaxed rules and still build; the new code targets net10 with warnings as errors and `AnalysisMode=Recommended`. Separate roots keep both honest until the app is rebuilt, then `src/` is deleted.
- **Dependency order changed:** Platform (interfaces) sits below Core, because the input pipeline in Core needs the injector and pad interfaces. Docs updated.

## D9. Input identity and keys

- Key events carry `KeyboardEvent.code`, mapped to set 1 scan codes plus the extended flag in `KeyMap`. Windows keys are not in the table, so a guest cannot open the host's Start menu.
- Stuck-input release lives in one place (`InputPipeline.ReleaseAll`) and is triggered by holder change, pause, timeout, dispose. Each has a test, plus a concurrency test.

## D10. Epoch authority and relay-only keys

- The host owns epochs. The room no longer bumps the epoch when it purges or kicks a player; it clears the holder, the host sees the player leave presence, and the session's `HolderLeft` change sends the new holder and epoch. Before this, a room-side bump could put the room ahead of the host and make the host's next `holder` message fail and close its socket.
- State frames carry no epoch. `InputPipeline` checks the sender is the holder and resets sequence tracking on every holder change. Key messages on the reliable channel carry the epoch and are checked.

## D11. Net layer shape

- `IMessageSocket` serializes sends and reassembles fragments (audit S11, S12). `RoomClient`, `PeerHub` and `HostSessionController` sit behind `IRoomLink` and `IPeerLink`, so session logic is tested with fakes and the real path is covered by one end-to-end test.

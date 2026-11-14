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

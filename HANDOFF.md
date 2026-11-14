# Handoff

Running list of things only you can do or that could not be verified on this machine.

## Blocking

1. **.NET SDK is not installed** on this machine (`dotnet --list-sdks` is empty). The host cannot be built or tested here. Install the .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`) and the rebuild can continue from phase 2.

## Skills and plugins

- `claude` CLI is not on PATH, so skills were cloned into `.claude/skills/` (see `docs/DECISIONS.md` D1). For real plugin installs, run the `/plugin install` commands in `REVITALIZE.md` section 0.1 in a normal Claude Code session.

## Accounts and money (later phases)

- Cloudflare login (Workers + Durable Objects deploy).
- Code signing (SignPath or Azure Trusted Signing).
- Domain, if you want one beyond `github.io`.

# Handoff

Running list of things only you can do or that could not be verified on this machine.

## Resolved

- .NET 10 SDK 10.0.404 installed via winget on this machine.

## Skills and plugins

- `claude` CLI is not on PATH, so skills were cloned into `.claude/skills/` (see `docs/DECISIONS.md` D1). For real plugin installs, run the `/plugin install` commands in `REVITALIZE.md` section 0.1 in a normal Claude Code session.

## Accounts and money (later phases)

- Cloudflare login (Workers + Durable Objects deploy).
- Code signing (SignPath or Azure Trusted Signing).
- Domain, if you want one beyond `github.io`.


## Driver installation

- The first-run wizard checks for ViGEmBus and tells the person where to get it (github.com/nefarius/ViGEmBus/releases) and that a restart is needed. It does not download or install anything. The brief asks for a silent, pinned-version, checksum-verified install; that needs your decision on bundling or downloading the installer and is not built.

## Website (phase 6)

- `site/` is static and ready to deploy to GitHub Pages at https://youssof20.github.io/passthestick/ (canonical URLs, sitemap and JSON-LD already use that address; change them if you use a domain).
- The download button asks GitHub for the latest release. Today that is the legacy v0.1.29 installer, so publish a new release before sending anyone to the site.
- Lighthouse was not run: the tool was blocked in this session. Run it against the deployed site (target 95+ in all four categories) and fix what it flags.
- The Dolphin motion guide follows Dolphin's DSU client settings but was not checked against a real Dolphin build here. Verify it and adjust the wording.
- The app's `SiteUrl` setting (default the Pages address) decides where invite links point.

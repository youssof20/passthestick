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

## Release pipeline (phase 7)

What exists: `.github/workflows/ci.yml` (host, Worker, web, design checks), `release.yml` (tag `v*` builds, packs with Velopack, uploads to GitHub Releases, attaches winget manifests), `pages.yml` (deploys `site/`), `worker.yml` (deploys the Worker when a token exists). A local Velopack pack of the app worked and produced the numbers in the README. None of the workflows has run on GitHub yet, and an installed copy has not been through install, update and uninstall.

What you need to do:
1. **First release.** Merge, then push a tag such as `v0.2.0`. Check the Releases page and install the result on a clean Windows machine.
2. **Pages.** In the repository settings, set Pages source to "GitHub Actions". Add the repository variable `PTS_SIGNALING_URL` (for example `wss://<your-worker>.workers.dev`) before the first Pages run.
3. **Cloudflare.** Add secrets `CLOUDFLARE_API_TOKEN` and `CLOUDFLARE_ACCOUNT_ID`. Until they exist the Worker workflow only runs tests. Put the deployed address in `AppSettings.DefaultServerUrl` (`host/PassTheStick.App/Services/Settings.cs`) before a public release; it still points at `http://127.0.0.1:8787`.
4. **Code signing.** Apply for SignPath's free open-source program. Then add the secret `SIGNPATH_API_TOKEN` and the variables `SIGNPATH_ORGANIZATION_ID`, `SIGNPATH_PROJECT_SLUG`, `SIGNPATH_POLICY_SLUG`. The signing steps in `release.yml` are written but untested; expect to adjust them to your SignPath project. Until then SmartScreen will warn on the installer.
5. **winget.** The release attaches ready manifests. Submitting them to `microsoft/winget-pkgs` is a manual pull request.
6. **Updates need releases.** The update check finds nothing until a second version is published after the first.

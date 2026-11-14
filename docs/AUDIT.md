# Audit of PassTheStick v0.1.29 (tag `legacy-v0.1.29`)

Every file under `src/`, plus the installer and release workflow, was read. Each finding is marked **Confirmed** (visible in the code as written) or **Verify** (needs a runtime or library check; the .NET SDK is not installed on this machine, see "Environment" below). Each confirmed item gets a regression test in the rebuild.

## Environment limits found during the audit

- No .NET SDK on this machine (`dotnet --list-sdks` is empty). Nothing in the host can be built or tested here until one is installed. Written up in `HANDOFF.md`.
- The `claude` CLI is not on PATH, so `/plugin install` was not possible. Skills were installed by cloning their repos into `.claude/skills/` instead (see `docs/DECISIONS.md`).

## 1. Security and networking (server.js, RelayClient.cs, Messages.cs)

| # | Finding | Status | Where |
|---|---|---|---|
| S1 | `HOST_REJOIN` has no secret. Any client can send it with any code, becomes host, and receives every guest's keystrokes. If the room does not exist it is created for the caller. | Confirmed | `server.js:87-121` |
| S2 | Room codes are `Math.random().toString(36).substring(2,6)`: 4 characters, not a CSPRNG, ambiguous alphabet, no collision check (a collision overwrites the existing room and orphans its host). | Confirmed | `server.js:7-9, 43` |
| S3 | The relay forwards `KEY_EVENT` and `PAD_STATE` from any guest, not only the stick holder. | Confirmed | `server.js:123-128` |
| S4 | The host also trusts the relay. `OnKeyEvent` and `OnPadState` never compare `FromId` with the active player. A non-holder's input is injected if the relay forwards it. | Confirmed | `HostPage.xaml.cs:1211, 1423` |
| S5 | `PASS_STICK` accepts any `toId`, including ids that are not in the room, and sets `activePlayerId` to it. Nobody then holds the stick. | Confirmed | `server.js:66-73` |
| S6 | No size limit (the `ws` default is 100 MiB), no schema validation, no rate limit, no join throttling, no protocol version, no player cap (`MaxPlayersPerRoom = 8` is defined and never used), no name length or content checks. | Confirmed | `server.js`, `Constants.cs:43` |
| S7 | Guest ids are `Math.random()` strings, so they are guessable. Spoofing is limited only because the relay overwrites `fromId`. | Confirmed | `server.js:32` |
| S8 | Host disconnect deletes the room at once and tells every guest the session ended. A Wi-Fi blip ends the evening. The client "rejoin" then recreates the room under the same code, which guests have already been told is dead. | Confirmed | `server.js:140-147` |
| S9 | All traffic including 60 Hz pad state is JSON over one TCP WebSocket through a relay. No P2P path. | Confirmed | `RelayClient.cs` |
| S10 | Cloud relay is a Render free service (`passthestick.onrender.com`). Cold start is handled by a client-side 2-minute retry loop and a blocking 2 s sleep. | Confirmed | `Constants.cs:5`, `HostPage.xaml.cs:815-847`, `RelayClient.cs:67-87` |
| S11 | `ClientWebSocket.SendAsync` is called concurrently from the keyboard hook callback, the controller poll loop and the heartbeat with no lock. .NET allows one outstanding send; overlapping sends throw. Failures are swallowed by fire-and-forget (`_ = _sendKeyEvent(...)`), so key-up events can be lost. | Confirmed | `RelayClient.cs:163-168`, `KeyboardCapture.cs:53`, `ControllerCapture.cs:51` |
| S12 | The receive loop reads into a 4096-byte buffer and ignores `EndOfMessage`. A fragmented or larger message is truncated and parsed as broken JSON. | Confirmed | `RelayClient.cs:170-191` |
| S13 | `PONG`/latency uses `_lastPongTs == _lastPingTs` equality. Fine in the normal case, but a late PONG from an earlier ping causes a false "connection lost" abort. | Confirmed | `RelayClient.cs:305-322` |
| S14 | `OnDisconnected` runs on the socket's receive thread and disposes the very `RelayClient` that is running the callback. | Confirmed | `HostPage.xaml.cs:1486-1491` |
| S15 | The reconnect loop gives up after 10 tries of 5 s (50 s). It is sequential with `_relay` swapped from a thread-pool thread without a lock. | Confirmed | `HostPage.xaml.cs:1567-1659` |

## 2. Privacy (guest side)

| # | Finding | Status | Where |
|---|---|---|---|
| P1 | `KeyboardCapture` installs a global `WH_KEYBOARD_LL` hook at join time. While the guest holds the stick, **every** key on their PC is sent to the host, including Discord chat and password fields. Keys are not swallowed locally, so the guest also types into whatever app has focus. | Confirmed | `KeyboardCapture.cs`, `GuestPage.xaml.cs:183-195` |
| P2 | No visible capture indicator and no panic key. | Confirmed | `GuestPage` |

## 3. Input correctness

| # | Finding | Status | Where |
|---|---|---|---|
| I1 | **Pad is never neutralised.** On stick pass, guest disconnect, host disconnect, focus loss or exit, only held keyboard scan codes are released. The ViGEm virtual pad keeps its last report, so a held button or stick stays held. `FeedReport` is the only writer. | Confirmed | `HostPage.xaml.cs:1184-1209, 1486-1512`, `ViGEmControllerInjector.cs` |
| I2 | Held-key release only runs when `previousWasGuest` is true and the new holder differs. Keys held by the previous holder survive a pass to the same guest id after reconnect, and `Dispose` paths (window close, `EndSession`) do not release. | Confirmed | `HostPage.xaml.cs:1184-1209` |
| I3 | Button mapping loops `i < ButtonCount` and treats the guest XInput bitmask bit `i` as ViGEm button index `i`. XInput's A/B/X/Y are bits 12-15 (`0x1000..0x8000`); `Xbox360Button` indexes for those are different, so face buttons are likely shifted. | Verify (needs the library on disk) | `ViGEmControllerInjector.cs:27-29` |
| I4 | Any exception in `OnPadState` shows "Controller support requires ViGEmBus". A null target, a disposed client or an unrelated bug gets the same message. | Confirmed | `HostPage.xaml.cs:1436-1441` |
| I5 | `OnPadState` runs on the socket thread, not the dispatcher, and touches `_vigem` and `_hidHide*` fields with no lock. | Confirmed | `HostPage.xaml.cs:1423-1442` |
| I6 | Key mapping uses the guest's **virtual key** through the host's `MapVirtualKey`. That maps by character, not by physical position. An AZERTY guest pressing the physical W position sends `Z`; a QWERTY host injects the Z key, so WASD games get the wrong keys. Games want physical position. The wire format should carry the physical code (scan code or `KeyboardEvent.code`) and the host should inject that. | Confirmed (by reading); needs a layout test | `KeyboardInjectionHelper.cs:16-31` |
| I7 | Extended-key list contains `0x1C` and `0x35`, which are the main Enter and `/` scan codes. Setting `KEYEVENTF_EXTENDEDKEY` for them turns main Enter into numpad Enter and `/` into numpad divide. | Confirmed | `InputInjector.cs:52-56` |
| I8 | Right Ctrl/Alt, numpad Enter/divide, Insert/Delete/Home/End/arrows, PrintScreen and Pause need the extended flag. Several are missing or wrongly keyed on scan code alone (scan code alone cannot tell numpad `/` from main `/`, or numpad 4 from Left arrow, without the extended bit travelling with the event). The wire format has no extended bit. | Confirmed | `Messages.cs`, `InputInjector.cs` |
| I9 | `InjectKeyWithResult` calls `AttachThreadInput` per key, `Thread.Sleep(2)` and `GetAsyncKeyState` verification on the UI thread, plus up to 5 log lines per key. Fine at human speed; adds jitter and contention under load. | Confirmed | `InputInjector.cs:58-175` |
| I10 | `SetThreadDesktop` is called from the UI thread, which already owns windows. It fails for such threads, and the code swallows it. | Confirmed (known Win32 rule) | `InputInjector.cs:21-49` |
| I11 | No mouse and no motion. | Confirmed | n/a |
| I12 | Foreground check treats `pid == gamePid` as a match. A second window from the same process (launcher, crash reporter, Dolphin's log window) counts as the game. | Confirmed | `GameWindowTracker.cs:98-100` |
| I13 | `IsGameForeground` formats and emits an Info log line on every call, including one per key from the LL hook. | Confirmed | `GameWindowTracker.cs:102-104` |
| I14 | Host-side suppression hook runs `GetProcessById` and logs inside `WH_KEYBOARD_LL`. Windows removes a low-level hook that exceeds `LowLevelHooksTimeout` without telling the app, after which host keys leak into the game. | Confirmed (pattern), Verify (frequency) | `HookManager.cs:50-100` |
| I15 | The suppression hook swallows every non-injected key while a guest holds the stick and the game is foreground. `RegisterHotKey` is processed after low-level hooks, so the pass/take-back hotkeys are likely swallowed in exactly the state where they matter. The 3-key "mash" override exists as a workaround. | Verify | `HookManager.cs`, `HotkeyManager.cs` |
| I16 | `SoloTestModeAsync` sets the active player to `"__test__"` and then reports "keyboard blocking and passing both work correctly" without testing either. | Confirmed | `HostPage.xaml.cs:1548-1565` |

## 4. Drivers and device state

| # | Finding | Status | Where |
|---|---|---|---|
| D1 | HidHide cleanup only runs on a normal window close or session end. A crash, kill or power loss leaves the physical pad hidden. Nothing checks for leftover state at startup. | Confirmed | `HostPage.xaml.cs:184-201`, `HidHideManager.cs` |
| D2 | Cleanup sets `IsActive = false` globally. If the user already used HidHide for something else, this turns their setup off. It also removes the app path without checking whether it was added by us. | Confirmed | `HidHideManager.cs:37-57` |
| D3 | The hide list is every HID device whose *name* contains controller, gamepad, joystick, xbox, dualshock or dualsense. That includes the host's own pad that they may be using for their own hotkeys. Name matching is fragile (localized names, "Wireless Controller"). | Confirmed | `HidPhysicalGamepadEnumerator.cs` |
| D4 | HidHide setup is triggered inside the first `PAD_STATE` handler, so the first guest frame pays WMI and driver-IPC cost and the failure mode is a log line. | Confirmed | `HostPage.xaml.cs:1432-1433` |
| D5 | The guest uses SharpDX XInput (unmaintained), polls controller index 0-3 once at start (no hot-plug), reads only Xbox-style pads, no gyro. | Confirmed | `ControllerCapture.cs` |
| D6 | One virtual pad for the whole session, created lazily on the first frame. No per-guest slots, no rumble return. | Confirmed | `ViGEmControllerInjector.cs` |
| D7 | ViGEmBus is archived (final v1.22.0). `Nefarius.ViGEm.Client` 1.21.256 and `Nefarius.Drivers.HidHide` 3.3.0 are pinned; HidHide has newer releases. | Confirmed (from the brief; versions to re-verify with context7) | `PassTheStick.Host.csproj` |

## 5. App structure and quality

| # | Finding | Status |
|---|---|---|
| A1 | `HostPage.xaml.cs` is 1,784 lines: UI, session state, networking, hotkeys, HidHide, overlay, reconnect and injection all in code-behind. No MVVM, no DI, no interfaces, no tests. | Confirmed |
| A2 | Dozens of empty `catch { }` blocks hide real failures (settings, tray, overlay, focus, sounds). | Confirmed |
| A3 | `async void` used for non-event-handler work (`TakeStickBack`, `OnPickGuest`, `EndSessionFromTray`, `SoloTestModeAsync`), so exceptions crash the process or vanish. | Confirmed |
| A4 | Hard-coded hex colors in code (`"#00C896"`, `"#FF4444"`, `"#4A9EFF"`) and a second brush dictionary in `Shared`. | Confirmed |
| A5 | `SettingsStore.Save` writes with `File.WriteAllText` (not atomic). A corrupt file silently resets everything, with no backup and no message. `SettingsSchemaVersion = 2` is duplicated in `SettingsPage.xaml.cs:83`. | Confirmed |
| A6 | Settings are re-read from disk on every call to `Constants.RelayWebSocketUrl`. | Confirmed |
| A7 | Version is stored in four places: `Constants.Version`, csproj (twice), `setup.iss`. They are bumped by hand. | Confirmed |
| A8 | Startup prompts "Restart as administrator?" on every launch. Admin is only needed when the game is elevated, which is knowable after pinning. | Confirmed |
| A9 | The update check hits `youssof20/passthestick`; the installer's URL points at `passthestick/passthestick`. One of them is wrong. The check only shows a balloon with a link; no update is applied. | Confirmed |
| A10 | Overlay disappears whenever the game is not foreground and in exclusive fullscreen; no detection or message. | Confirmed |
| A11 | Emoji in UI strings (`🎮`, `⏸`, `✓`, `✗`, `⚠`) and "!" in several. | Confirmed |
| A12 | `GuestPage.JoinButton_Click` has a `while (true)` that always `break`s. Dead loop. | Confirmed |
| A13 | Guest page reuses the host's relay URL setting with no way to join a LAN or self-hosted room by link. | Confirmed |
| A14 | Local relay is spawned by `RelayProcessManager` from a bundled Node runtime found in `{app}\relay`. `StartRelayWithPortFallback` blocks the UI thread on `.GetAwaiter().GetResult()`. | Confirmed |

## 6. Packaging and release

| # | Finding | Status |
|---|---|---|
| R1 | Self-contained single-file WPF publish on .NET 8 plus a full Node 22 runtime plus `node_modules` bundled for an optional LAN relay. | Confirmed |
| R2 | Inno Setup, `PrivilegesRequired=admin`, installs to Program Files, no delta updates, unsigned binaries. | Confirmed |
| R3 | Release job downloads Node over HTTP from nodejs.org with no checksum. | Confirmed |
| R4 | No CI on pull requests, no tests to run, no lint, no analyzers, warnings not errors. | Confirmed |
| R5 | .NET 8 reaches end of support in November 2026; the brief targets .NET 10 LTS. | Confirmed (re-verify date with context7) |
| R6 | Repo root holds `NEWLOGO.png` (duplicate of `assets/logo.png`, same size). | Confirmed |

## 7. Knowledge worth porting (not necessarily the code)

- **Window tracking**: pin by HWND and PID, rescan by PID when the HWND dies, treat a null foreground window as "keep the last answer for 500 ms" (focus transitions return null briefly). Keep that, but match by HWND-or-top-level-window-of-PID, not bare PID (I12).
- **Elevation**: `TokenElevation` check on the pinned process and the UIPI explanation (SendInput error 5). Only ask for admin when this is true.
- **Focus**: `AttachThreadInput` + `SetForegroundWindow` + `BringWindowToTop`, and refocusing the game ~150 ms after a handoff because our own UI or picker can steal focus.
- **SendInput**: scan-code injection with `KEYEVENTF_SCANCODE` (needed for DirectInput/Raw Input games); `INPUT` union needs explicit layout with the mouse member the largest, and the size passed to `SendInput` must be the real union size on x64.
- **Overlay/picker without stealing focus**: `WS_EX_NOACTIVATE` and tool-window styles (`Win32WindowStyles.cs`); hotkey via `RegisterHotKey` on a message window instead of reading keys.
- **Host safety valve**: "mash three keys in a second to take the stick back" is a good idea and stays, as a hotkey-independent path (but must not rely on a hook doing slow work).
- **Hotkey defaults**: Ctrl+Shift+Right pass, Ctrl+Shift+Left take back; `MOD_*` flags differ from WPF `ModifierKeys` (the old settings code hit this).
- **Log de-duplication**: collapsing repeated lines with a count is useful; port into the structured logger as a sink option.
- **Stick state rules that worked**: guest leaves while holding the stick, so the stick returns to the host; player list drives who holds the stick.

## 8. What gets deleted

`src/PassTheStick.Relay` (Node relay, Dockerfile, fly.toml), `RelayProcessManager`, `RelayConnectionDialog`, the global guest keyboard hook, SharpDX, the Inno Setup script, the bundled Node step in the workflow, `NEWLOGO.png` duplicate, `Messages.cs` string-typed JSON records, `InputDebugLog` as a static bus (replaced by `ILogger` + a bounded in-app log sink).

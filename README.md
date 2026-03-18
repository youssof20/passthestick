# PassTheStick

**Couch co-op energy for single-player games, over the internet.**

Take turns controlling a single-player game on one person's PC. Everyone watches via Discord screenshare. PassTheStick decides whose keyboard or controller is plugged in.

> No screen capture. No video. No voice. Just input routing.

<img width="910" height="555" alt="Main Screenshot" src="https://github.com/user-attachments/assets/7d898973-f994-425d-b2b0-16a3735d0dc1" />

---

## How it works

- The **host** runs the game + PassTheStick
- **Guests** install the lightweight client, enter a 4-character room code, and wait
- The host presses **Ctrl+Shift+→** to pass the stick — whoever has it controls the game
- Everyone else is blocked until it's their turn

---

## Install

1. Download the latest installer from [Releases](../../releases)
2. Run `PassTheStickSetup.exe`
3. On first run: choose **Host** or **Guest**
4. If prompted, restart as administrator (required for some games)

> **Optional:** Enable controller support during install — this adds ViGEmBus and HidHide drivers (requires a one-time reboot)

---

## Quick start

### Host

1. Launch PassTheStick — it lives in your system tray
2. If prompted, restart as administrator
3. Select your game window and click **Pin selected window**
4. Share the **room code** with friends (e.g. `WOLF`)
5. Press **Ctrl+Shift+→** to open the pass-stick menu and choose a guest

### Guest

1. Launch PassTheStick (or run `PassTheStick.exe --guest`)
2. Enter the room code and your display name → **Join**
3. When the host passes you the stick, you'll get a notification
4. Your keyboard or controller now controls the host's game — as long as the game window is focused on their end

---

## Input support

| Guest input | What the host game receives |
|---|---|
| Keyboard | Keyboard (scan-code injected, works with Raw Input + DirectInput) |
| Xbox / XInput controller | Virtual Xbox controller via ViGEmBus |

> **Keyboard layout mismatch handled automatically.** An AZERTY guest pressing W sends the correct key on a QWERTY host.

---

## Overlay

A small always-on-top widget shows whose turn it is. Draggable. Hideable.

> Use **borderless windowed mode** in your game for the overlay to appear. True fullscreen exclusive (D3D) owns the display buffer and the overlay won't be visible.

---

## Relay server

PassTheStick uses a lightweight WebSocket relay to route input between players.

**No setup is required.** A free cloud relay is used automatically by default.

### Use a custom relay URL

Set the environment variable before launching:

```
PTS_RELAY_URL=wss://your-server
```

If you want to self-host (optional), point `PTS_RELAY_URL` at your relay.

---

## Build from source

Requirements: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

```bash
dotnet build PassTheStick.sln
```

To build the installer: publish the Launcher project, copy Host + Guest outputs into the same folder, then run Inno Setup with `installer/setup.iss`.

---

## Game compatibility

| Scenario | Example | What to verify |
|---|---|---|
| Raw Input (elevated) | Elden Ring | UAC + scan code injection |
| DirectInput | Older title / emulator | Keyboard + ViGEm |
| XInput-only | Game that ignores keyboard | ViGEm + HidHide |
| Foreground strict | Game ignores input when unfocused | Foreground PID check |
| Scan code / layout | Non-QWERTY host or guest | MapVirtualKey path |
| Multiple controllers | Game picks "first" controller | Only one ViGEm device visible |
| Borderless fullscreen | Most modern games | Overlay visible, focus correct |

> **Do not use PassTheStick in online or competitive game modes.** It is designed for single-player games only.

---

## Frequently asked questions

**Does the guest need to install anything?**
Yes — the PassTheStick client (~5 MB). No drivers needed for keyboard-only mode. Controller support requires an optional one-time driver install on the host PC.

**Does it work if we're not on the same network?**
Yes. The relay handles NAT traversal. No port forwarding needed.

**Will it get me banned?**
PassTheStick injects input using standard Windows APIs. It is not designed for online or competitive games — use it for single-player games only.

**What about mouse?**
Mouse forwarding is not supported in v1. Controller support covers most use cases (analog stick = mouse equivalent for controller-compatible games).

---

## License

MIT — see [LICENSE](LICENSE)

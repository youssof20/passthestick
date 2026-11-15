# Manual test script

Automated tests cover the logic. These steps cover what needs real hardware, real games and real people. Run them before a public release and write down what you see. Nothing in this list has been run end to end yet; the right-hand column says what should happen.

You need: a Windows 10 or 11 PC (the host), a second device on a different network (a phone on mobile data is ideal), a friend or a second PC, Dolphin with a Wii Sports image, a physical Xbox-style controller, ViGEmBus installed, a small game or tool that shows input (Notepad works for keys).

## 1. Install and update

| Step | Should happen |
|---|---|
| Run the setup file on a clean user account | Installs without an admin prompt, adds a Start menu entry, opens the app. |
| Close and reopen the app | Opens in about a second. Setup does not show again. |
| Publish a newer release, wait for the app to notice | A "Version X is ready" message appears. The update installs when you close the app. |
| Start a session, then trigger the update check | "Restart to update" stays disabled while the session is live. |
| Uninstall from Windows settings | The app, its settings folder and any journal under `%LOCALAPPDATA%\PassTheStick` are gone. ViGEmBus and HidHide are still installed. |

## 2. First session with a keyboard

| Step | Should happen |
|---|---|
| Open Notepad, pick it in the list, press Start a session | Live pill, room code, invite link. |
| Open the link on the phone (mobile data) | Joins in a few seconds. The page shows "Direct" or "Through a relay server" with a round trip time. |
| On the phone press Test my controls, press on-screen buttons | Buttons light in the host's test panel and the phone shows the same. Nothing is typed into Notepad. |
| Pass the stick to the phone with `Ctrl+Shift+Right` | The pill over Notepad names the phone's player. |
| Pair a keyboard (second PC): click the capture box and type | Text appears in Notepad. Typing in Discord or another app on the guest PC does not reach the host. |
| Hold `Esc` for one second on the guest | The capture badge disappears and typing stops arriving. |
| Take the stick back with `Ctrl+Shift+Left` while a key is held | The key is released on the host. |

## 3. Stuck input

| Step | Should happen |
|---|---|
| Guest holds a key in Notepad, host passes the stick away | Repeating characters stop. |
| Guest holds a key, then closes the tab | Characters stop. |
| Guest holds a key, then locks the phone | Characters stop. |
| Guest holds a key, host clicks another window | Characters stop and "not in front" shows on the host. |
| Unplug the host's network for 10 seconds while a guest holds a key | The key is released within a second of the silence. |

## 4. Controllers

| Step | Should happen |
|---|---|
| Choose "Controllers too" in setup with ViGEmBus installed | The wizard says the driver is installed. |
| Plug a controller into the guest PC, open the invite link, press a button | The test panel lights the matching button. A, B, X and Y light the right ones. |
| Pass the stick, open a controller test page on the host | One virtual Xbox pad appears and follows the guest's sticks and triggers. |
| Remove ViGEmBus and start again | The app starts and says controllers need the driver. Keyboards still work. |
| Rumble: use a game or tester that rumbles | The guest's controller or phone vibrates. |

## 5. Phone motion in Dolphin

| Step | Should happen |
|---|---|
| Add the DSU server in Dolphin as the docs describe | Dolphin lists a device for slot 0 when a guest has joined. |
| Phone taps Use phone motion (iPhone: allow the prompt) | Dolphin's motion mapping shows values moving as the phone moves. |
| Pass the stick to the phone and swing in Wii Sports bowling | The ball is thrown. Fix any wording in `docs/dolphin.html` that differs from your Dolphin version. |

## 6. Hard cases

| Step | Should happen |
|---|---|
| Both sides behind strict NAT (a phone hotspot on each) | Falls back to the room server path. The stick holder's input still works. The test says it needs a direct connection. |
| Run a game as administrator, PassTheStick normally | A warning explains that Windows blocks input and tells you to restart as administrator. |
| Run a game in exclusive fullscreen | The overlay is not visible. Note whether the game accepts input. |
| Start two copies of PassTheStick | The second says it is already running. |
| Corrupt `%LOCALAPPDATA%\PassTheStick\settings.json` | Defaults load and a message names the kept copy. |
| Kill the app during a session with a pad hidden by HidHide (once HidHide is wired to a switch) | The next start restores the pad. |
| Join eight guests, then a ninth | The ninth is told the session is full. |

## 7. Anti-cheat

Do not test against online games. Kernel anti-cheat can flag virtual input and your account may be banned. The app says so; confirm the wizard or docs still do.

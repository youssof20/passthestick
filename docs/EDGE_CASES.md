# Edge cases: what is handled, how it is checked, what is not

Status words: **Tested** means an automated test exercises it. **Run by hand** means I ran it against the real app on the development machine. **Written, not run** means the code exists and has never been run end to end. **Gap** means it is not handled.

## Networking

| Case | Status | Detail |
|---|---|---|
| Host loses Wi-Fi for 10 s | Partly | The room client reconnects with backoff and the host token (tested). The room keeps players, holder and epoch for 60 s and a returning host gets them back (Worker test). **Gap:** the WebRTC links are not re-offered after the host reconnects, and ICE restart is not implemented, so guests may need to rejoin. |
| Guest on mobile data with 5% loss | Partly | State frames are latest-wins and unreliable by design, control messages are reliable. The host stack survives dropped, repeated and reordered frames (chaos test, 30 000 steps). **Gap:** no real lossy-link test, because Windows has no netem and I did not build an impairment proxy. |
| Symmetric NAT on both sides | Written, not run | A STUN server is configured, no TURN. When a direct link fails after 8 s the guest uses the room relay, which carries only the stick holder's input. No TURN provider is chosen (needs your decision). |
| Guest joins a full room | Tested | `room_full` after eight guests. |
| Wrong or expired code | Tested | Both give `room_not_found` over the socket. Expiry is tested with a moved clock. |
| Two hosts with the same code | Tested | Creation refuses an existing room and retries; 150 rooms are all distinct; a second host socket gets `host_exists`. |
| Host app restarts mid-session | **Gap** | The host token lives in memory only, so a restarted app cannot reattach and the room ends after 60 s. The token should be stored with the settings, protected by Windows. |
| Signaling server restarts | Written, not run | Durable Object state survives on Cloudflare and clients reconnect with backoff (tested with a fake socket). Not tried against a real restart. |
| Protocol version mismatch | Tested | Both the page and the host show an "update" message and stop retrying. |
| Clock skew | Handled by design | Nothing depends on wall-clock agreement. The only timestamp is a ping echo. |
| Oversized, malformed or flooding client | Tested | Message size, message rate, relay rate and join attempts are capped. A storm of junk from a stranger, a guest and the host leaves the room working. The host decoder is strict and never throws on garbage. |
| Guest spoofing another guest's id | Tested | The room stamps every forwarded message with the sender's real id and ignores client-supplied ones. |

## Input

| Case | Status | Detail |
|---|---|---|
| Stick passed while keys or buttons are held | Tested | Everything is released on the change. Also covered in the chaos test. |
| Guest disconnects mid-press | Tested | The stick returns to the host and everything is released. |
| Game window closed or minimized | Tested (logic) | Input pauses and releases. The window tracker itself is not unit tested because it calls Windows. |
| Game runs elevated, host does not | Run by hand (detection logic only) | Detected and warned about. Not run against a real elevated game. |
| Exclusive fullscreen | **Gap** | The overlay is drawn under such games. No detection or message yet. |
| Multiple monitors and DPI scaling | Run by hand | Overlay placement was wrong by the scale factor and is fixed; positions are in pixels. Tested with negative coordinates. |
| Different keyboard layouts | Tested | Keys travel as physical positions and map to scan codes, so layout differences do not change which key is pressed. Main and numpad Enter, slash and divide, arrows and numpad digits are distinct. |
| Controller unplugged or replugged | Written, not run | The page polls every frame and sends a neutral pad when none is present. |
| Two co-op guests with the same pad model | **Gap** | Only the stick holder's input is accepted. The "everyone at once" co-op mode, with one virtual pad per guest, is not built. |
| Host with no ViGEm or VIIPER, keyboard only | Run by hand | The app starts, runs a session, and says why controllers are unavailable. VIIPER is not supported. |
| HidHide left on after a crash | Tested | A journal is written before any change and undone at the next start and at uninstall. Nothing the user had is removed. HidHide is not yet switched on by any UI. |
| App crash during driver install | Not applicable | The app does not install drivers. |
| Reboot pending | Not applicable | Same reason; the setup text says a restart is needed after installing ViGEmBus. |

## Web guest

| Case | Status | Detail |
|---|---|---|
| iOS Safari motion permission | Written, not run | The permission request is wired to a button. Needs a real iPhone. |
| Phone screen locks or tab is backgrounded | Written, not run | A wake lock is requested, and every held key and button is released when the page is hidden, loses focus, or unloads. |
| Browser without the Gamepad API | Written, not run | The page says so and keyboard and on-screen buttons still work. |
| Controller connected after page load | Written, not run | Controllers are polled each frame, so this works without a reload. |
| Landscape and portrait, very small screens | Run by hand (portrait only) | No horizontal overflow at 375 px. Landscape is not checked. |

## App

| Case | Status | Detail |
|---|---|---|
| First run with no settings | Tested and run by hand | Defaults, then the wizard. |
| Corrupted settings | Tested | Kept as `settings.json.damaged-…`, defaults used, the person is told. |
| Two instances | Written, not run | A named mutex blocks the second and says so. |
| Update applied during a session | Tested | Updates install on exit or on request, and never while a session is live. |
| Uninstall cleanup | Tested (logic) | Removes our settings and journal and undoes our HidHide entries. Drivers are left alone. The hook is wired to the Velopack uninstall callback but was not run through the real uninstaller. |

## Known gaps, in order of importance

1. Host restart cannot resume the room (token not saved).
2. No re-offer or ICE restart after a host network drop.
3. Co-op (all guests at once) and timed round-robin turn modes are not built.
4. No TURN, so two strict NATs fall back to the relay, which only carries the holder.
5. No real lossy-network test.
6. No exclusive-fullscreen detection.

# PassTheStick protocol v1

One spec for the host (C#), the web guest (TypeScript) and the signaling Worker (TypeScript). Types are generated from `protocol/schema.json` into both languages so they cannot drift (phase 4). This file is the source of truth until the schema exists.

## 1. Roles and paths

- **Host**: the Windows app. Authority for stick state and the only thing that injects input.
- **Guest**: a browser page or the native guest mode.
- **Signaling room**: one Durable Object per room code. Carries WebRTC setup, presence, and (only when peer-to-peer fails) relayed traffic.

Path order per guest: direct WebRTC, then WebRTC through TURN, then relay over the room WebSocket. The UI shows which path is live and the measured RTT.

## 2. Room codes and secrets

- Code: 6 characters from `ABCDEFGHJKMNPQRSTUVWXYZ23456789` (no 0 O 1 I L), from a CSPRNG. The Worker checks for a collision before creating the room. About 30^6 = 729 million codes.
- `hostToken`: 128-bit random, issued to the creator at `create`, stored hashed (SHA-256) in the room. Required to attach as host and to reattach after a restart. Never sent to guests.
- `resumeToken` per guest: 128-bit random, issued at `join`, stored hashed. Lets a guest reconnect within the grace window and keep its player id and slot.
- Player ids are random 64-bit values chosen by the room, not by clients. The room stamps every forwarded message with the sender's id; client-supplied sender fields are ignored.

## 3. Signaling WebSocket

URL: `wss://<worker>/v1/room/<CODE>`. First message must be `hello` within 5 s or the socket closes.

All signaling messages are JSON text frames, max 8 KiB.

```
hello      { t:"hello", v:1, role:"host"|"guest", hostToken?, resumeToken?, name? }
welcome    { t:"welcome", v:1, you:<id>, resumeToken?, players:[{id,name,slot}], holder:<id|null>, maxPlayers }
error      { t:"error", code, message }          // codes below
signal     { t:"signal", to:<id>, sdp?|candidate? }  // host <-> guest only; room adds "from"
presence   { t:"presence", players:[{id,name,slot,away?}], holder, epoch, hostAway }
holder     { t:"holder", id:<id|null>, epoch }   // host to room only; epoch may not go backwards
kick       { t:"kick", id }                      // host to room only
request    { t:"request" }                       // guest to room; room tells the host { t:"request", from }
ended      { t:"ended", reason:"host_left"|"idle" }
relay      binary frame, [8-byte player id][payload]; guest frames reach the host only from the current holder
path       { t:"path", id, kind:"direct"|"turn"|"relay", rttMs }
ping/pong  { t:"ping", ts } / { t:"pong", ts }
```

Error codes: `bad_version` (message says "update your app"), `room_not_found`, `room_full`, `bad_token`, `host_exists`, `rate_limited`, `too_large`, `bad_message`.

Limits (enforced in the Worker, tested): 8 players per room default (cap 16); 5 join attempts per IP per minute; 20 signaling messages per second per socket; relay data frames 64 per second per guest unless the guest holds the stick (then 250); host grace window 60 s (room, players and holder are kept while the host is away; after that the room ends and everyone gets `ended`); a room with the host connected but silent for 30 minutes ends as `idle`; a guest who drops keeps its id and slot for 60 s and can resume.

## 4. Peer data channels

Two channels per guest, opened by the host.

**`state`** (unordered, `maxRetransmits: 0`): binary, latest wins. Each frame starts with `u8 kind`, `u16 seq` (wraps; receiver drops anything not newer, using serial-number arithmetic). Little-endian.

| kind | name | body |
|---|---|---|
| 1 | pad | `u16 buttons` (Xbox 360 layout), `i16 lx, ly, rx, ry`, `u8 lt, rt` |
| 2 | motion | `f32 accel[3]` (g), `f32 gyro[3]` (deg/s), `u32 tsMicros` |
| 3 | pointer | `i16 x, y` (0..32767 absolute within the game window) or relative with flag bit, `u8 buttons` |
| 4 | keepalive | empty |

Sent on change plus a keepalive every 250 ms. The host treats 750 ms without any state frame from the holder as "released" and neutralizes everything.

**`control`** (ordered, reliable): JSON text, max 4 KiB.

```
key        { t:"key", code:<KeyboardEvent.code>, down:bool }     // physical position, not character
request    { t:"request" }                                         // guest asks for the stick
holder     { t:"holder", id, epoch }                               // host authority; epoch increments every change
slot       { t:"slot", id, slot }
rumble     { t:"rumble", strong:0-255, weak:0-255, ms }
emote/chat { t:"chat", text<=140 }
```

Key events carry the physical key (`KeyboardEvent.code`, e.g. `KeyW`). The host maps `code` to a scan code plus extended flag from a fixed table, so layout differences between guest and host cannot change which physical key is pressed (fixes audit I6-I8).

## 5. Stick rules (invariants)

1. Input is accepted only from the current holder (or the guest's assigned slot in co-op). The host drops everything else, and the relay drops it too. Both checks exist; neither trusts the other.
2. Every `holder` frame carries an `epoch`. State and key frames carry the epoch they were sent under; frames with an old epoch are dropped.
3. On any holder change, disconnect, focus loss, pause, resume timeout or exit, the host releases every held key and button and centers sticks and triggers. A test asserts this for each trigger.
4. Only the host can change the holder. Guests can `request`.

## 6. Versioning

`v` is an integer in `hello`/`welcome`. Any mismatch gets `bad_version` and the connection closes. Changes that add optional fields keep `v`; anything else bumps it.

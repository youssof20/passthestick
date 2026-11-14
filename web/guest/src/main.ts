import { HostLink, type PathKind } from "./peer";
import { SignalingClient, type ConnectionState, type PlayerInfo, type ServerMessage } from "./signaling";
import { KeyCapture, PanicKey, describeProbe, mergePads, motionFromEvent, type Probe } from "./keys";
import { parseRoomCode, signalingBase } from "./link";
import {
  NEUTRAL_PAD, PadButton, encodeKeepalive, encodeMotion, encodePad, padEquals, padFromGamepad, type PadState,
} from "./wire";

// Replaced at build time (see build.mjs) so a deployment points at its own Worker.
declare const __SIGNALING_URL__: string;
declare const __ICE_SERVERS__: RTCIceServer[];

const $ = <T extends HTMLElement>(id: string): T => document.getElementById(id) as T;

const view = {
  join: $("join"), session: $("session"), form: $<HTMLFormElement>("join-form"), code: $<HTMLInputElement>("code"),
  name: $<HTMLInputElement>("name"), error: $("join-error"), status: $("status"), path: $("path"), holder: $("holder"),
  surface: $("surface"), surfaceHint: $("surface-hint"), request: $<HTMLButtonElement>("request"), touch: $("touch"),
  motion: $<HTMLButtonElement>("motion"), test: $<HTMLButtonElement>("test"), testbox: $("testbox"), echo: $("echo"), echoWarning: $("echo-warning"), leave: $<HTMLButtonElement>("leave"), badge: $("badge"), players: $("players"),
};

let me = "";
let holder = "host";
let epoch = 0;
let players: PlayerInfo[] = [];
let seq = 0;
let lastPad: PadState = NEUTRAL_PAD;
let lastSentAt = 0;
let signaling: SignalingClient | null = null;
let link: HostLink | null = null;
let wakeLock: WakeLockSentinel | null = null;
let motionOn = false;
let testing = false;
let lastEchoAt = 0;
let lastInputAt = 0;

const keys = new KeyCapture();
const panic = new PanicKey(1000);
const virtualPad: PadState = { ...NEUTRAL_PAD };
const iHaveStick = (): boolean => me !== "" && holder === me;
/** Input is sent while holding the stick, or in test mode (the host shows it but never injects it for a non-holder). */
const sending = (): boolean => iHaveStick() || testing;
const nextSeq = (): number => (seq = (seq + 1) & 0xffff);

function say(el: HTMLElement, text: string): void {
  el.textContent = text;
}

function showError(text: string): void {
  view.error.hidden = text === "";
  say(view.error, text);
}

function nameOf(id: string): string {
  return id === "host" ? "The host" : (players.find((p) => p.id === id)?.name ?? "Someone");
}

function renderHolder(): void {
  const mine = iHaveStick();
  say(view.holder, mine ? "You have the stick" : `${nameOf(holder)} has the stick`);
  view.session.classList.toggle("is-holder", mine);
  if (mine && testing) setTesting(false);
  view.surface.hidden = !sending();
  view.touch.hidden = !sending();
  view.request.hidden = mine;
  view.test.hidden = mine;
  view.surfaceHint.hidden = !sending();
  if (!sending()) releaseEverything();
  say(view.players, players.map((p) => p.name + (p.id === holder ? " (stick)" : "")).join(", "));
}

function releaseEverything(): void {
  for (const code of keys.releaseAll()) link?.sendControl({ t: "key", code, down: false, epoch });
  Object.assign(virtualPad, NEUTRAL_PAD);
  if (!padEquals(lastPad, NEUTRAL_PAD)) {
    lastPad = NEUTRAL_PAD;
    link?.sendState(encodePad(nextSeq(), NEUTRAL_PAD));
  }
  view.badge.hidden = true;
}

// ---- keyboard: only while the capture surface has focus, with a visible badge and a panic key ----

view.surface.addEventListener("focus", () => {
  view.badge.hidden = false;
});
view.surface.addEventListener("blur", () => {
  releaseEverything();
});
view.surface.addEventListener("keydown", (e) => {
  if (!sending()) return;
  if (e.code === "Escape") panic.down(performance.now());
  e.preventDefault();
  if (!e.repeat && keys.down(e.code)) {
    lastInputAt = performance.now();
    link?.sendControl({ t: "key", code: e.code, down: true, epoch });
  }
});
view.surface.addEventListener("keyup", (e) => {
  if (e.code === "Escape") panic.up();
  e.preventDefault();
  if (keys.up(e.code)) link?.sendControl({ t: "key", code: e.code, down: false, epoch });
});

// ---- on-screen controls ----

const BUTTONS: Record<string, number> = {
  up: PadButton.DpadUp, down: PadButton.DpadDown, left: PadButton.DpadLeft, right: PadButton.DpadRight,
  a: PadButton.A, b: PadButton.B, x: PadButton.X, y: PadButton.Y, start: PadButton.Start, back: PadButton.Back,
  lb: PadButton.LeftShoulder, rb: PadButton.RightShoulder,
};
view.touch.querySelectorAll<HTMLButtonElement>("button[data-btn]").forEach((b) => {
  const bit = BUTTONS[b.dataset.btn ?? ""] ?? 0;
  const set = (on: boolean): void => {
    virtualPad.buttons = on ? virtualPad.buttons | bit : virtualPad.buttons & ~bit;
    b.classList.toggle("is-down", on);
    if (on && "vibrate" in navigator) navigator.vibrate(8);
  };
  b.addEventListener("pointerdown", (e) => {
    b.setPointerCapture(e.pointerId);
    set(true);
  });
  for (const ev of ["pointerup", "pointercancel", "lostpointercapture"]) b.addEventListener(ev, () => set(false));
});

// ---- 60 Hz loop: gamepad plus on-screen pad, sent on change with a keepalive ----

function frame(now: number): void {
  requestAnimationFrame(frame);
  if (!link || !sending()) return;
  if (testing && lastInputAt > 0 && lastEchoAt < lastInputAt && now - lastInputAt > 3000) view.echoWarning.hidden = false;
  if (panic.fired(now)) {
    panic.up();
    view.surface.blur();
    return;
  }
  const pads = navigator.getGamepads?.() ?? [];
  const physical = pads.find((p): p is Gamepad => p !== null && p.connected);
  const live = physical ? padFromGamepad(physical.buttons, physical.axes) : NEUTRAL_PAD;
  const merged = mergePads(live, virtualPad);
  if (!padEquals(merged, lastPad)) {
    lastPad = merged;
    lastSentAt = now;
    if (!padEquals(merged, NEUTRAL_PAD)) lastInputAt = now;
    link.sendState(encodePad(nextSeq(), merged));
  } else if (now - lastSentAt >= 250) {
    lastSentAt = now;
    link.sendState(encodeKeepalive(nextSeq()));
  }
}
requestAnimationFrame(frame);

// ---- motion (phone gyro), optional ----

let lastMotion = 0;
function onMotion(e: DeviceMotionEvent): void {
  const now = performance.now();
  if (!link || !iHaveStick() || now - lastMotion < 16) return;
  lastMotion = now;
  const m = motionFromEvent(e.accelerationIncludingGravity, e.rotationRate);
  link.sendState(encodeMotion(nextSeq(), { ...m, timestampMicros: Math.round(now * 1000) }));
}

view.motion.addEventListener("click", async () => {
  const dm = DeviceMotionEvent as unknown as { requestPermission?: () => Promise<string> };
  if (typeof dm.requestPermission === "function") {
    const result = await dm.requestPermission().catch(() => "denied");
    if (result !== "granted") {
      say(view.motion, "Motion blocked. Allow it in your browser settings and reload.");
      return;
    }
  }
  motionOn = !motionOn;
  if (motionOn) window.addEventListener("devicemotion", onMotion);
  else window.removeEventListener("devicemotion", onMotion);
  say(view.motion, motionOn ? "Motion on" : "Use phone motion");
});

// ---- connection ----

const pathText: Record<PathKind, string> = { connecting: "Connecting", direct: "Direct", turn: "Through a relay server (TURN)", relay: "Through the room server" };

function connect(code: string, name: string): void {
  showError("");
  const base = signalingBase(location, __SIGNALING_URL__);
  signaling = new SignalingClient(base, code, name, {
    message: (m) => void onMessage(m),
    binary: () => undefined,
    state: onConnectionState,
    fatal: (_code, text) => {
      leave();
      showError(text);
    },
  });
  link = new HostLink(signaling, __ICE_SERVERS__, {
    path: (kind, rtt) => say(view.path, rtt === null ? pathText[kind] : `${pathText[kind]}, ${rtt} ms`),
    control: (m) => {
      if (m.t === "probe") {
        lastEchoAt = performance.now();
        view.echoWarning.hidden = true;
        say(view.echo, describeProbe(m as unknown as Probe) ?? "Nothing pressed.");
      } else if (m.t === "holder" && typeof m.id === "string") {
        holder = m.id;
        epoch = Number(m.epoch) || epoch;
        renderHolder();
      }
    },
    rumble: (strong, weak, ms) => {
      if ("vibrate" in navigator) navigator.vibrate(Math.min(ms, 500));
      const pad = (navigator.getGamepads?.() ?? []).find((p): p is Gamepad => p !== null && p.connected);
      void pad?.vibrationActuator?.playEffect?.("dual-rumble", { duration: Math.min(ms, 500), strongMagnitude: strong / 255, weakMagnitude: weak / 255 });
    },
  });
  view.join.hidden = true;
  view.session.hidden = false;
  signaling.connect();
  void keepAwake();
}

function onConnectionState(s: ConnectionState): void {
  const text: Record<ConnectionState, string> = {
    connecting: "Connecting", open: "Connected", reconnecting: "Connection lost. Reconnecting", closed: "Disconnected",
  };
  say(view.status, text[s]);
  view.session.dataset.state = s;
}

async function onMessage(m: ServerMessage): Promise<void> {
  switch (m.t) {
    case "welcome":
      me = m.you;
      // fall through: a welcome carries the same fields as presence
    case "presence":
      players = m.players;
      holder = m.holder;
      epoch = m.epoch;
      renderHolder();
      say(view.status, m.hostAway ? "The host lost connection. Waiting for them to come back" : "Connected");
      break;
    case "signal":
      await link?.onSignal(m);
      break;
    case "ended":
      leave();
      showError(m.reason === "idle" ? "The session closed after 30 quiet minutes." : "The host left, so the session ended.");
      break;
    default:
      break;
  }
}

function setTesting(on: boolean): void {
  if (testing === on) return;
  testing = on;
  view.test.setAttribute("aria-pressed", String(on));
  view.testbox.hidden = !on;
  view.echoWarning.hidden = true;
  lastInputAt = 0;
  lastEchoAt = 0;
  say(view.echo, "Press a button or a key.");
  link?.sendControl({ t: "test", on });
  view.surface.hidden = !sending();
  view.touch.hidden = !sending();
  view.surfaceHint.hidden = !sending();
  if (!on) releaseEverything();
}

view.test.addEventListener("click", () => {
  if (!testing && link?.usingRelay) {
    say(view.path, "Testing needs a direct connection. You are using the room server, which only passes the stick holder's input.");
    return;
  }
  setTesting(!testing);
});

view.request.addEventListener("click", () => {
  link?.sendControl({ t: "request" });
  say(view.request, "Asked. Waiting for the host");
  setTimeout(() => say(view.request, "Ask for the stick"), 4000);
});

async function keepAwake(): Promise<void> {
  try {
    wakeLock = (await navigator.wakeLock?.request("screen")) ?? null;
  } catch {
    wakeLock = null; // Not supported or refused; the session still works.
  }
}
document.addEventListener("visibilitychange", () => {
  if (document.visibilityState === "visible" && signaling) void keepAwake();
});

function leave(): void {
  releaseEverything();
  link?.close();
  signaling?.close();
  link = null;
  signaling = null;
  void wakeLock?.release();
  window.removeEventListener("devicemotion", onMotion);
  motionOn = false;
  me = "";
  holder = "host";
  view.session.hidden = true;
  view.join.hidden = false;
}
view.leave.addEventListener("click", leave);

view.form.addEventListener("submit", (e) => {
  e.preventDefault();
  const code = parseRoomCode(view.code.value);
  if (!code) {
    showError("That code does not look right. It is six letters and numbers, with no 0, O, 1, I or L.");
    return;
  }
  const name = view.name.value.trim() || "Guest";
  try {
    localStorage.setItem("pts-name", name);
  } catch {
    // Storage can be blocked; the name just will not be remembered.
  }
  connect(code, name);
});

try {
  view.name.value = localStorage.getItem("pts-name") ?? "";
} catch {
  view.name.value = "";
}
function fillFromLink(): void {
  const fromLink = parseRoomCode(location.hash);
  if (fromLink) {
    view.code.value = fromLink;
    view.name.focus();
  }
}
fillFromLink();
window.addEventListener("hashchange", fillFromLink);
if (!("getGamepads" in navigator)) $("no-gamepad").hidden = false;

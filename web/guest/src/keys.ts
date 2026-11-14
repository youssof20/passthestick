import { PadButton, type PadState } from "./wire";

/** Tracks which keys this guest has told the host are down, so every one can be released on any state change. */
export class KeyCapture {
  private held = new Set<string>();

  /** Returns true when a key-down should be sent (first press only; auto-repeat is ignored). */
  down(code: string): boolean {
    if (this.held.has(code)) return false;
    this.held.add(code);
    return true;
  }

  /** Returns true when a key-up should be sent. A stray release for a key we never sent is ignored. */
  up(code: string): boolean {
    return this.held.delete(code);
  }

  /** Everything currently down; the caller sends a key-up for each. Empties the set. */
  releaseAll(): string[] {
    const list = [...this.held];
    this.held.clear();
    return list;
  }

  get count(): number {
    return this.held.size;
  }
}

/** Escape held for `holdMs` releases capture. Pure so it can be tested with a fake clock. */
export class PanicKey {
  private since: number | null = null;

  constructor(private readonly holdMs = 1000) {}

  down(now: number): void {
    this.since ??= now;
  }

  up(): void {
    this.since = null;
  }

  /** True once the key has been held long enough. */
  fired(now: number): boolean {
    return this.since !== null && now - this.since >= this.holdMs;
  }
}

/** OR the buttons and take the larger axis, so a plugged-in pad and the on-screen controls can both be live. */
export function mergePads(a: PadState, b: PadState): PadState {
  const big = (x: number, y: number): number => (Math.abs(x) >= Math.abs(y) ? x : y);
  return {
    ...a,
    buttons: a.buttons | b.buttons,
    lx: big(a.lx, b.lx), ly: big(a.ly, b.ly), rx: big(a.rx, b.rx), ry: big(a.ry, b.ry),
    lt: Math.max(a.lt, b.lt), rt: Math.max(a.rt, b.rt),
  };
}

export const G = 9.80665;

/** Device motion to the units the protocol uses: accelerometer in g, gyro in deg/s as pitch, yaw, roll. */
export function motionFromEvent(accel: { x: number | null; y: number | null; z: number | null } | null, rate: { alpha: number | null; beta: number | null; gamma: number | null } | null): { accel: [number, number, number]; gyro: [number, number, number] } {
  const n = (v: number | null | undefined): number => (typeof v === "number" && Number.isFinite(v) ? v : 0);
  return {
    accel: [n(accel?.x) / G, n(accel?.y) / G, n(accel?.z) / G],
    gyro: [n(rate?.beta), n(rate?.alpha), n(rate?.gamma)],
  };
}

const BUTTON_NAMES: [number, string][] = [
  [PadButton.A, "A"], [PadButton.B, "B"], [PadButton.X, "X"], [PadButton.Y, "Y"],
  [PadButton.LeftShoulder, "Left bumper"], [PadButton.RightShoulder, "Right bumper"],
  [PadButton.Back, "Back"], [PadButton.Start, "Start"], [PadButton.Guide, "Guide"],
  [PadButton.LeftThumb, "Left stick press"], [PadButton.RightThumb, "Right stick press"],
  [PadButton.DpadUp, "D-pad up"], [PadButton.DpadDown, "D-pad down"], [PadButton.DpadLeft, "D-pad left"], [PadButton.DpadRight, "D-pad right"],
];

export interface Probe {
  buttons: number;
  lx: number;
  ly: number;
  rx: number;
  ry: number;
  lt: number;
  rt: number;
  keys: string[];
}

/** Turns a physical key code into what is printed on a typical keyboard: KeyW to W, ArrowLeft to Left arrow. */
export function keyLabel(code: string): string {
  if (/^Key[A-Z]$/.test(code)) return code.slice(3);
  if (/^Digit\d$/.test(code)) return code.slice(5);
  if (code.startsWith("Arrow")) return `${code.slice(5)} arrow`;
  if (code.startsWith("Numpad")) return `Numpad ${code.slice(6)}`;
  return code.replace(/([a-z])([A-Z])/g, "$1 $2");
}

/** One plain sentence for what the host reports receiving, or null when nothing is pressed. */
export function describeProbe(p: Probe, deadzone = 0.3): string | null {
  const parts: string[] = [];
  for (const [bit, name] of BUTTON_NAMES) if ((p.buttons & bit) !== 0) parts.push(name);
  const stick = (label: string, x: number, y: number): void => {
    if (Math.abs(x) / 32767 > deadzone) parts.push(`${label} ${x < 0 ? "left" : "right"}`);
    if (Math.abs(y) / 32767 > deadzone) parts.push(`${label} ${y < 0 ? "down" : "up"}`);
  };
  stick("Left stick", p.lx, p.ly);
  stick("Right stick", p.rx, p.ry);
  if (p.lt / 255 > deadzone) parts.push("Left trigger");
  if (p.rt / 255 > deadzone) parts.push("Right trigger");
  for (const k of p.keys) parts.push(`Key ${keyLabel(k)}`);
  return parts.length === 0 ? null : parts.join(", ");
}

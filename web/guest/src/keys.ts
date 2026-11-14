import type { PadState } from "./wire";

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

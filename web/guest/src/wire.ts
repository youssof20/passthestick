// Binary state frames. Must stay byte-identical to host/PassTheStick.Protocol/StateFrames.cs.
// The golden vector in test/wire.test.ts is also asserted in the C# tests.

export const enum StateKind {
  Pad = 1,
  Motion = 2,
  Mouse = 3,
  Keepalive = 4,
}

export const PadButton = {
  DpadUp: 0x0001,
  DpadDown: 0x0002,
  DpadLeft: 0x0004,
  DpadRight: 0x0008,
  Start: 0x0010,
  Back: 0x0020,
  LeftThumb: 0x0040,
  RightThumb: 0x0080,
  LeftShoulder: 0x0100,
  RightShoulder: 0x0200,
  Guide: 0x0400,
  A: 0x1000,
  B: 0x2000,
  X: 0x4000,
  Y: 0x8000,
} as const;

export interface PadState {
  buttons: number;
  lx: number;
  ly: number;
  rx: number;
  ry: number;
  lt: number;
  rt: number;
}

export const NEUTRAL_PAD: PadState = { buttons: 0, lx: 0, ly: 0, rx: 0, ry: 0, lt: 0, rt: 0 };

export function padEquals(a: PadState, b: PadState): boolean {
  return a.buttons === b.buttons && a.lx === b.lx && a.ly === b.ly && a.rx === b.rx && a.ry === b.ry && a.lt === b.lt && a.rt === b.rt;
}

function header(size: number, kind: StateKind, seq: number): DataView {
  const view = new DataView(new ArrayBuffer(size));
  view.setUint8(0, kind);
  view.setUint16(1, seq & 0xffff, true);
  return view;
}

export function encodePad(seq: number, p: PadState): ArrayBuffer {
  const v = header(15, StateKind.Pad, seq);
  v.setUint16(3, p.buttons & 0xffff, true);
  v.setInt16(5, p.lx, true);
  v.setInt16(7, p.ly, true);
  v.setInt16(9, p.rx, true);
  v.setInt16(11, p.ry, true);
  v.setUint8(13, p.lt);
  v.setUint8(14, p.rt);
  return v.buffer as ArrayBuffer;
}

export interface MotionState {
  accel: [number, number, number]; // g
  gyro: [number, number, number]; // deg/s
  timestampMicros: number;
}

export function encodeMotion(seq: number, m: MotionState): ArrayBuffer {
  const v = header(31, StateKind.Motion, seq);
  [...m.accel, ...m.gyro].forEach((n, i) => v.setFloat32(3 + i * 4, n, true));
  v.setUint32(27, m.timestampMicros >>> 0, true);
  return v.buffer as ArrayBuffer;
}

export function encodeMouse(seq: number, x: number, y: number, relative: boolean, buttons: number): ArrayBuffer {
  const v = header(8, StateKind.Mouse, seq);
  v.setInt16(3, x, true);
  v.setInt16(5, y, true);
  v.setUint8(7, (buttons & 0x7f) | (relative ? 0x80 : 0));
  return v.buffer as ArrayBuffer;
}

export function encodeKeepalive(seq: number): ArrayBuffer {
  return header(3, StateKind.Keepalive, seq).buffer as ArrayBuffer;
}

/** Relay-only key frame (kind 5): [5][seq u16][down u8][len u8][KeyboardEvent.code, ASCII]. Peer channels use JSON on `control` instead. */
export function encodeKeyRelay(seq: number, code: string, down: boolean): ArrayBuffer {
  const text = new TextEncoder().encode(code.slice(0, 32));
  const out = new Uint8Array(5 + text.length);
  const v = new DataView(out.buffer);
  v.setUint8(0, 5);
  v.setUint16(1, seq & 0xffff, true);
  v.setUint8(3, down ? 1 : 0);
  v.setUint8(4, text.length);
  out.set(text, 5);
  return out.buffer;
}

export function clampAxis(value: number): number {
  const v = Math.max(-1, Math.min(1, Number.isFinite(value) ? value : 0));
  return Math.round(v * 32767) || 0; // `|| 0` turns -0 into 0
}

export function clampTrigger(value: number): number {
  const v = Math.max(0, Math.min(1, Number.isFinite(value) ? value : 0));
  return Math.round(v * 255);
}

/** Standard Gamepad API mapping to the Xbox 360 layout. Y axes are inverted: the web reports down as positive. */
export function padFromGamepad(buttons: readonly { pressed: boolean; value: number }[], axes: readonly number[]): PadState {
  const down = (i: number): boolean => buttons[i]?.pressed === true;
  const map: [number, number][] = [
    [0, PadButton.A], [1, PadButton.B], [2, PadButton.X], [3, PadButton.Y],
    [4, PadButton.LeftShoulder], [5, PadButton.RightShoulder], [8, PadButton.Back], [9, PadButton.Start],
    [10, PadButton.LeftThumb], [11, PadButton.RightThumb], [12, PadButton.DpadUp], [13, PadButton.DpadDown],
    [14, PadButton.DpadLeft], [15, PadButton.DpadRight], [16, PadButton.Guide],
  ];
  let mask = 0;
  for (const [index, bit] of map) if (down(index)) mask |= bit;
  return {
    buttons: mask,
    lx: clampAxis(axes[0] ?? 0),
    ly: clampAxis(-(axes[1] ?? 0)),
    rx: clampAxis(axes[2] ?? 0),
    ry: clampAxis(-(axes[3] ?? 0)),
    lt: clampTrigger(buttons[6]?.value ?? 0),
    rt: clampTrigger(buttons[7]?.value ?? 0),
  };
}

/** True when `candidate` is newer than `current` for wrapping 16-bit sequence numbers. */
export function isNewer(candidate: number, current: number): boolean {
  const diff = (candidate - current) & 0xffff;
  return diff !== 0 && diff < 0x8000;
}

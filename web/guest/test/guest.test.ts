import { describe, expect, it } from "vitest";
import { KeyCapture, PanicKey, describeProbe, keyLabel, mergePads, motionFromEvent } from "../src/keys";
import { parseRoomCode, signalingBase } from "../src/link";
import { FATAL_ERRORS, SignalingClient, backoffMs, friendlyError } from "../src/signaling";
import {
  NEUTRAL_PAD, PadButton, clampAxis, clampTrigger, encodeKeepalive, encodeKeyRelay, encodeMotion, encodeMouse, encodePad,
  isNewer, padFromGamepad,
} from "../src/wire";

const hex = (b: ArrayBuffer): string => [...new Uint8Array(b)].map((x) => x.toString(16).padStart(2, "0")).join("");

describe("wire format", () => {
  // The same bytes are asserted in host/PassTheStick.Protocol.Tests so the two codecs cannot drift.
  const GOLDEN_PAD = "01010204" + "10" + "0080" + "ff7f" + "ffff" + "0100" + "ff" + "07";

  it("matches the golden pad frame used by the C# tests", () => {
    const frame = encodePad(513, { buttons: PadButton.A | PadButton.DpadLeft, lx: -32768, ly: 32767, rx: -1, ry: 1, lt: 255, rt: 7 });
    expect(hex(frame)).toBe(GOLDEN_PAD);
  });

  it("encodes keepalive, mouse, motion and relay key frames with the documented layout", () => {
    expect(hex(encodeKeepalive(9))).toBe("040900");
    expect(hex(encodeMouse(2, -5, 300, true, 0b101))).toBe("0302" + "00" + "fbff" + "2c01" + "85");
    expect(encodeMotion(1, { accel: [0, 1, 0], gyro: [90, 0, 0], timestampMicros: 7 }).byteLength).toBe(31);
    expect(hex(encodeKeyRelay(3, "KeyW", true))).toBe("050300" + "01" + "04" + "4b657957");
  });

  it("wraps sequence numbers at 16 bits and compares them serially", () => {
    expect(hex(encodeKeepalive(65537))).toBe("040100");
    expect(isNewer(0, 65535)).toBe(true);
    expect(isNewer(65535, 0)).toBe(false);
    expect(isNewer(5, 5)).toBe(false);
  });

  it("clamps axes and triggers and treats NaN as zero", () => {
    expect(clampAxis(2)).toBe(32767);
    expect(clampAxis(-2)).toBe(-32767);
    expect(clampAxis(Number.NaN)).toBe(0);
    expect(clampTrigger(1.5)).toBe(255);
    expect(clampTrigger(-1)).toBe(0);
  });
});

describe("gamepad mapping", () => {
  const buttons = (pressed: number[], triggers: [number, number] = [0, 0]) =>
    Array.from({ length: 17 }, (_, i) => ({ pressed: pressed.includes(i), value: i === 6 ? triggers[0] : i === 7 ? triggers[1] : pressed.includes(i) ? 1 : 0 }));

  it("maps standard buttons to the Xbox layout", () => {
    const p = padFromGamepad(buttons([0, 1, 2, 3, 4, 5, 8, 9, 10, 11, 12, 13, 14, 15, 16]), [0, 0, 0, 0]);
    const all = Object.values(PadButton).reduce((a, b) => a | b, 0);
    expect(p.buttons).toBe(all);
  });

  it("inverts Y axes because the web reports down as positive", () => {
    const p = padFromGamepad(buttons([]), [0.5, -1, -0.5, 1]);
    expect(p.lx).toBeGreaterThan(0);
    expect(p.ly).toBe(32767);
    expect(p.rx).toBeLessThan(0);
    expect(p.ry).toBe(-32767);
  });

  it("reads analog triggers from button values 6 and 7", () => {
    const p = padFromGamepad(buttons([], [1, 0.5]), [0, 0, 0, 0]);
    expect(p.lt).toBe(255);
    expect(p.rt).toBe(128);
  });

  it("copes with missing buttons and axes", () => {
    expect(padFromGamepad([], [])).toEqual(NEUTRAL_PAD);
  });

  it("merges a physical pad with on-screen buttons", () => {
    const a = { ...NEUTRAL_PAD, buttons: PadButton.A, lx: -20000 };
    const b = { ...NEUTRAL_PAD, buttons: PadButton.B, lx: 100 };
    const m = mergePads(a, b);
    expect(m.buttons).toBe(PadButton.A | PadButton.B);
    expect(m.lx).toBe(-20000);
  });
});

describe("key capture", () => {
  it("sends one down per press, ignores auto-repeat and stray releases", () => {
    const k = new KeyCapture();
    expect(k.down("KeyW")).toBe(true);
    expect(k.down("KeyW")).toBe(false);
    expect(k.up("KeyD")).toBe(false);
    expect(k.up("KeyW")).toBe(true);
  });

  it("releases everything that is down and then holds nothing", () => {
    const k = new KeyCapture();
    k.down("KeyW");
    k.down("Space");
    expect(k.releaseAll().sort()).toEqual(["KeyW", "Space"]);
    expect(k.count).toBe(0);
    expect(k.releaseAll()).toEqual([]);
  });

  it("panic key fires only after the full hold and resets on release", () => {
    const p = new PanicKey(1000);
    p.down(0);
    expect(p.fired(999)).toBe(false);
    expect(p.fired(1000)).toBe(true);
    p.up();
    expect(p.fired(5000)).toBe(false);
    p.down(5000);
    p.down(5600);
    expect(p.fired(5999)).toBe(false);
  });
});

describe("motion", () => {
  it("converts m/s² to g and orders gyro as pitch, yaw, roll", () => {
    const m = motionFromEvent({ x: 9.80665, y: 0, z: -9.80665 }, { alpha: 10, beta: 20, gamma: 30 });
    expect(m.accel[0]).toBeCloseTo(1);
    expect(m.accel[2]).toBeCloseTo(-1);
    expect(m.gyro).toEqual([20, 10, 30]);
  });

  it("treats missing sensors as zero", () => {
    expect(motionFromEvent(null, null)).toEqual({ accel: [0, 0, 0], gyro: [0, 0, 0] });
  });
});

describe("room links", () => {
  it("accepts codes, lowercase, spaces, dashes and full invite links", () => {
    expect(parseRoomCode("abcdef")).toBe("ABCDEF");
    expect(parseRoomCode(" ABC-DEF ")).toBe("ABCDEF");
    expect(parseRoomCode("https://passthestick.example/join#K7M2QX")).toBe("K7M2QX");
  });

  it("rejects look-alike characters, wrong length and empty input", () => {
    for (const bad of ["ABCDE0", "ABCDEI", "ABCDEL", "ABC", "ABCDEFG", ""]) expect(parseRoomCode(bad)).toBeNull();
  });

  it("only accepts a plain ws or wss server override", () => {
    const loc = (search: string) => ({ search, host: "x", protocol: "https:" });
    expect(signalingBase(loc("?server=wss://example.workers.dev"), "wss://default")).toBe("wss://example.workers.dev");
    expect(signalingBase(loc("?server=javascript:alert(1)"), "wss://default")).toBe("wss://default");
    expect(signalingBase(loc("?server=wss://a.com/path?x=1"), "wss://default")).toBe("wss://default");
    expect(signalingBase(loc(""), "wss://default")).toBe("wss://default");
  });
});

describe("signaling client", () => {
  it("backs off from 0.5 s doubling to a cap of 8 s", () => {
    const none = () => 0;
    expect([0, 1, 2, 3, 4, 5, 9].map((n) => backoffMs(n, none))).toEqual([500, 1000, 2000, 4000, 8000, 8000, 8000]);
    expect(backoffMs(0, () => 1)).toBe(600);
  });

  it("has a plain message for every fatal error", () => {
    for (const code of FATAL_ERRORS) {
      const text = friendlyError(code);
      expect(text.length).toBeGreaterThan(20);
      expect(text).not.toMatch(/[!]|seamless|robust/);
    }
    expect(friendlyError("bad_version")).toContain("update");
  });

  class FakeSocket {
    readyState = 0;
    sent: string[] = [];
    private handlers: Record<string, ((e: unknown) => void)[]> = {};
    binaryType = "";
    addEventListener(type: string, fn: (e: unknown) => void): void {
      (this.handlers[type] ??= []).push(fn);
    }
    send(data: string): void {
      this.sent.push(data);
    }
    close(): void {
      this.fire("close", {});
    }
    fire(type: string, e: unknown): void {
      for (const fn of this.handlers[type] ?? []) fn(e);
    }
  }

  it("says hello with the protocol version, keeps the resume token and stops retrying on a fatal error", () => {
    const sockets: FakeSocket[] = [];
    const states: string[] = [];
    const fatals: string[] = [];
    const client = new SignalingClient(
      "wss://x", "ABCDEF", "Ann",
      { message: () => undefined, binary: () => undefined, state: (s) => states.push(s), fatal: (c) => fatals.push(c) },
      () => {
        const s = new FakeSocket();
        sockets.push(s);
        return s as unknown as WebSocket;
      },
    );
    client.connect();
    const first = sockets[0] as FakeSocket;
    first.readyState = 1;
    first.fire("open", {});
    expect(JSON.parse(first.sent[0] as string)).toMatchObject({ t: "hello", v: 1, role: "guest", name: "Ann" });

    first.fire("message", { data: JSON.stringify({ t: "welcome", v: 1, you: "g1", players: [], holder: "host", epoch: 0, hostAway: false, resumeToken: "tok" }) });
    expect(states).toContain("open");

    first.fire("message", { data: JSON.stringify({ t: "error", code: "room_not_found", message: "x" }) });
    expect(fatals).toEqual(["room_not_found"]);
    first.fire("close", {});
    expect(sockets.length).toBe(1);
  });
});

describe("test mode echo", () => {
  const idle = { buttons: 0, lx: 0, ly: 0, rx: 0, ry: 0, lt: 0, rt: 0, keys: [] as string[] };

  it("says nothing when nothing is pressed", () => {
    expect(describeProbe(idle)).toBeNull();
  });

  it("names buttons, sticks past the dead zone, triggers and keys in plain words", () => {
    expect(describeProbe({ ...idle, buttons: PadButton.A | PadButton.DpadLeft })).toBe("A, D-pad left");
    expect(describeProbe({ ...idle, lx: -30000, ly: 32767 })).toBe("Left stick left, Left stick up");
    expect(describeProbe({ ...idle, rx: 9000 })).toBeNull();
    expect(describeProbe({ ...idle, lt: 255, rt: 10 })).toBe("Left trigger");
    expect(describeProbe({ ...idle, keys: ["KeyW", "ArrowLeft"] })).toBe("Key W, Key Left arrow");
  });

  it("turns key codes into what is printed on the key", () => {
    expect(keyLabel("KeyA")).toBe("A");
    expect(keyLabel("Digit5")).toBe("5");
    expect(keyLabel("ArrowUp")).toBe("Up arrow");
    expect(keyLabel("Numpad4")).toBe("Numpad 4");
    expect(keyLabel("ShiftLeft")).toBe("Shift Left");
    expect(keyLabel("Space")).toBe("Space");
  });
});

describe("signal ordering", () => {
  it("handles the offer completely before the candidates that follow it", async () => {
    const calls: string[] = [];
    class FakePc {
      onicecandidate: unknown;
      ondatachannel: unknown;
      onconnectionstatechange: unknown;
      localDescription = { toJSON: () => ({ type: "answer", sdp: "a" }) };
      async setRemoteDescription(): Promise<void> {
        calls.push("remote:start");
        await new Promise((r) => setTimeout(r, 30));
        calls.push("remote:done");
      }
      async createAnswer(): Promise<object> { return { type: "answer", sdp: "a" }; }
      async setLocalDescription(): Promise<void> { calls.push("local"); }
      async addIceCandidate(): Promise<void> { calls.push("candidate"); }
      close(): void { calls.push("close"); }
    }
    const g = globalThis as unknown as { RTCPeerConnection?: unknown };
    const saved = g.RTCPeerConnection;
    g.RTCPeerConnection = FakePc;
    try {
      const { HostLink } = await import("../src/peer");
      const link = new HostLink({ send: () => undefined, sendBinary: () => undefined } as never, [], { path: () => undefined, control: () => undefined, rumble: () => undefined });
      void link.onSignal({ sdp: { type: "offer", sdp: "o" } });
      void link.onSignal({ candidate: { candidate: "c1" } });
      await link.onSignal({ candidate: { candidate: "c2" } });
      expect(calls.slice(0, 5)).toEqual(["remote:start", "remote:done", "local", "candidate", "candidate"]);
      link.close();
    } finally {
      g.RTCPeerConnection = saved;
    }
  });
});

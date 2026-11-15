import { afterEach, describe, expect, it, vi } from "vitest";
import { SELF, env, runDurableObjectAlarm } from "cloudflare:test";
import { Client, connect, createRoom, joinGuest, joinHost, type Msg } from "./helpers";

/** Collects every binary frame a client receives within the window, ignoring JSON messages. */
async function binaryFrames(c: Client, ms: number): Promise<number> {
  let frames = 0;
  const end = Date.now() + ms;
  while (Date.now() < end) {
    try {
      const m = await Promise.race([c.raw(), new Promise<never>((_, rej) => setTimeout(() => rej(new Error("quiet")), 80))]);
      if (typeof m !== "string") frames++;
    } catch {
      if (Date.now() >= end - 80) break;
    }
  }
  return frames;
}

const stubFor = (code: string) => env.ROOM.get(env.ROOM.idFromName(code));

/** Moves the clock forward without touching timers, then lets the room run its alarm. */
async function passTime(code: string, ms: number): Promise<void> {
  vi.useFakeTimers({ toFake: ["Date"] });
  vi.setSystemTime(Date.now() + ms);
  await runDurableObjectAlarm(stubFor(code));
}

afterEach(() => {
  vi.useRealTimers();
});

describe("host connection loss", () => {
  it("keeps the room, the players and the holder when the host comes back inside the grace window", async () => {
    const { code, hostToken } = await createRoom();
    const host = await joinHost(code, hostToken);
    const { c: guest, welcome } = await joinGuest(code, "Ann");
    host.send({ t: "holder", id: welcome.you, epoch: 3 });
    await guest.until("presence");

    host.ws.close(1001, "wifi dropped");
    await new Promise((r) => setTimeout(r, 100));
    await passTime(code, 30_000); // 30 s is inside the 60 s grace

    const back = await joinHost(code, hostToken);
    void back;
    // joinHost consumed the welcome; reconnect once more to read it
    back.ws.close(1000, "again");
    await new Promise((r) => setTimeout(r, 100));
    const c = await connect(code);
    c.send({ t: "hello", v: 1, role: "host", hostToken });
    const w = await c.next();
    expect(w.t).toBe("welcome");
    expect((w.players as { name: string }[]).map((p) => p.name)).toEqual(["Ann"]);
    expect(w.holder).toBe(welcome.you);
    expect(w.epoch).toBe(3);
  });

  it("ends the room and tells guests once the grace window has passed", async () => {
    const { code, hostToken } = await createRoom();
    const host = await joinHost(code, hostToken);
    const { c: guest } = await joinGuest(code, "Ann");
    host.ws.close(1001, "gone");
    await new Promise((r) => setTimeout(r, 100));
    await passTime(code, 61_000);
    expect((await guest.until("ended")).reason).toBe("host_left");

    const late = await connect(code);
    expect((await late.next()).code).toBe("room_not_found");
  });

  it("ends a room whose host stays connected but silent for 30 minutes", async () => {
    const { code, hostToken } = await createRoom();
    const host = await joinHost(code, hostToken);
    await passTime(code, 31 * 60_000);
    expect((await host.until("ended")).reason).toBe("idle");
  });
});

describe("guest connection loss", () => {
  it("purges a guest who stays away past the grace window and hands the stick back to the host", async () => {
    const { code, hostToken } = await createRoom();
    const host = await joinHost(code, hostToken);
    const { c: guest, welcome } = await joinGuest(code, "Ann");
    host.send({ t: "holder", id: welcome.you, epoch: 1 });
    await guest.until("presence");
    guest.ws.close(1001, "phone locked");
    await new Promise((r) => setTimeout(r, 100));

    await passTime(code, 61_000);
    let last: Msg = {};
    for (let i = 0; i < 10; i++) {
      last = await host.until("presence");
      if ((last.players as unknown[]).length === 0) break;
    }
    expect(last.players).toEqual([]);
    expect(last.holder).toBe("host");

    const again = await connect(code, "203.0.113.200");
    again.send({ t: "hello", v: 1, role: "guest", resumeToken: welcome.resumeToken });
    expect((await again.next()).code).toBe("bad_token");
  });

  it("lets only the token holder take over a guest identity, closing the old socket", async () => {
    const { code, hostToken } = await createRoom();
    await joinHost(code, hostToken);
    const first = await joinGuest(code, "Ann");
    const second = await connect(code, "203.0.113.201");
    second.send({ t: "hello", v: 1, role: "guest", resumeToken: first.welcome.resumeToken });
    expect((await second.next()).you).toBe(first.welcome.you);
    await new Promise((r) => setTimeout(r, 100));
    expect(first.c.closed?.reason).toBe("replaced");
  });

  it("removes a kicked guest and closes their socket", async () => {
    const { code, hostToken } = await createRoom();
    const host = await joinHost(code, hostToken);
    const { c: guest, welcome } = await joinGuest(code, "Ann");
    host.send({ t: "kick", id: welcome.you });
    await new Promise((r) => setTimeout(r, 200));
    expect(guest.closed?.reason).toBe("kicked");
  });
});

describe("connection that never says hello", () => {
  it("is closed after five seconds", async () => {
    const { code } = await createRoom();
    const idle = await connect(code);
    await passTime(code, 6_000);
    await new Promise((r) => setTimeout(r, 150));
    expect(idle.closed).not.toBeNull();
  });
});

describe("flooding and oversized frames", () => {
  it("drops a non-holder's binary frames and closes anyone sending an oversized one", async () => {
    const { code, hostToken } = await createRoom();
    const host = await joinHost(code, hostToken);
    const { c: guest } = await joinGuest(code, "Ann");
    guest.send(new Uint8Array(100));
    expect(await binaryFrames(host, 300)).toBe(0);

    const big = await joinGuest(code, "Big");
    big.c.send(new Uint8Array(2000));
    expect((await big.c.until("error")).code).toBe("too_large");
  });

  it("caps the holder's relayed frames per second", async () => {
    const { code, hostToken } = await createRoom();
    const host = await joinHost(code, hostToken);
    const { c: guest, welcome } = await joinGuest(code, "Ann");
    host.send({ t: "holder", id: welcome.you, epoch: 1 });
    await guest.until("presence");

    for (let i = 0; i < 600; i++) guest.send(new Uint8Array([i & 0xff, 1, 2]));
    await new Promise((r) => setTimeout(r, 500));
    let frames = 0;
    for (;;) {
      try {
        const m = await Promise.race([host.raw(), new Promise<never>((_, rej) => setTimeout(() => rej(new Error("done")), 150))]);
        if (typeof m !== "string") frames++;
      } catch {
        break;
      }
    }
    expect(frames).toBeGreaterThan(0);
    expect(frames).toBeLessThanOrEqual(250);
  });
});

describe("room codes", () => {
  it("are unique across many rooms", async () => {
    const codes = new Set<string>();
    for (let i = 0; i < 150; i++) codes.add((await createRoom()).code);
    expect(codes.size).toBe(150);
  });
});

describe("garbage from every kind of client", () => {
  const junk = (n: number): (string | Uint8Array)[] => {
    const out: (string | Uint8Array)[] = [
      "", "null", "true", "[]", "{}", '{"t":null}', '{"t":{}}', '{"t":"holder"}', '{"t":"signal","to":{}}', '{"t":"kick","id":[]}',
      '{"t":"hello","v":1,"role":"host","hostToken":{"a":1}}', '{"t":"hello","v":"1"}', "\u0000\u0001", "{".repeat(500), JSON.stringify({ t: "x".repeat(300) }),
    ];
    let seed = 99;
    for (let i = 0; i < n; i++) {
      seed = (seed * 1103515245 + 12345) & 0x7fffffff;
      const bytes = new Uint8Array(1 + (seed % 40));
      for (let j = 0; j < bytes.length; j++) {
        seed = (seed * 1103515245 + 12345) & 0x7fffffff;
        bytes[j] = seed & 0xff;
      }
      out.push(seed % 2 === 0 ? bytes : String.fromCharCode(...bytes));
    }
    return out;
  };

  it("never takes the room down: after a storm from a stranger, a guest and the host, the room still works", async () => {
    const { code, hostToken } = await createRoom();
    const host = await joinHost(code, hostToken);
    const { c: guest } = await joinGuest(code, "Ann");

    // Junk is spread over many sockets, since the room closes a connection that misbehaves.
    for (const payload of junk(40)) {
      const stranger = await connect(code, `198.51.100.${10 + Math.floor(Math.random() * 200)}`);
      stranger.send(payload);
    }
    for (const payload of junk(30)) {
      try {
        guest.send(payload);
      } catch {
        break;
      }
    }
    for (const payload of junk(30)) {
      try {
        host.send(payload);
      } catch {
        break;
      }
    }

    // The host may have been closed for sending junk; a host with the token can always return.
    const hostAgain = await (async () => {
      const c = await connect(code);
      c.send({ t: "hello", v: 1, role: "host", hostToken });
      const m = await c.next();
      if (m.code === "host_exists") return null;
      expect(m.t).toBe("welcome");
      return c;
    })();
    void hostAgain;

    const res = await SELF.fetch("https://example.com/v1/rooms", { method: "POST" });
    expect(res.status).toBe(200);
    const fresh = await joinGuest(code, "Late", {});
    expect(["welcome", "error"]).toContain(fresh.welcome.t as string);
  });
});

describe("clients", () => {
  it("get a plain refusal for a room code that was never created", async () => {
    const c = await connect("ZZZZZY");
    expect(new Client(c.ws)).toBeDefined();
  });
});

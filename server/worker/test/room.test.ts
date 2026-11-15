import { describe, expect, it } from "vitest";
import { SELF } from "cloudflare:test";
import { Client, connect, createRoom, joinGuest, joinHost, type Msg } from "./helpers";

describe("room creation", () => {
  it("returns a valid code and a token, and different rooms get different values", async () => {
    const a = await createRoom();
    const b = await createRoom();
    expect(a.code).toMatch(/^[ABCDEFGHJKMNPQRSTUVWXYZ23456789]{6}$/);
    expect(a.hostToken.length).toBeGreaterThanOrEqual(22);
    expect(a.code).not.toBe(b.code);
    expect(a.hostToken).not.toBe(b.hostToken);
  });

  it("tells the client over the socket when a code is malformed or unknown", async () => {
    for (const code of ["ABC0O1", "ZZZZZZ"]) {
      const res = await SELF.fetch(`https://example.com/v1/room/${code}`, { headers: { Upgrade: "websocket" } });
      expect(res.status).toBe(101);
      const c = new Client(res.webSocket as WebSocket);
      expect((await c.next()).code).toBe("room_not_found");
    }
  });
});

describe("host attach", () => {
  it("rejects a wrong or missing host token", async () => {
    const { code } = await createRoom();
    const c = await connect(code);
    c.send({ t: "hello", v: 1, role: "host", hostToken: "nope" });
    expect((await c.next()).code).toBe("bad_token");
    const d = await connect(code);
    d.send({ t: "hello", v: 1, role: "host" });
    expect((await d.next()).code).toBe("bad_token");
  });

  it("accepts the right token and refuses a second host", async () => {
    const { code, hostToken } = await createRoom();
    await joinHost(code, hostToken);
    const second = await connect(code);
    second.send({ t: "hello", v: 1, role: "host", hostToken });
    expect((await second.next()).code).toBe("host_exists");
  });

  it("does not leak the host token or its hash to guests", async () => {
    const { code, hostToken } = await createRoom();
    await joinHost(code, hostToken);
    const { c, welcome } = await joinGuest(code);
    const everything = JSON.stringify(welcome) + JSON.stringify(await c.silence());
    expect(everything).not.toContain(hostToken);
  });
});

describe("hello validation", () => {
  it("rejects a protocol version mismatch with an update message", async () => {
    const { code } = await createRoom();
    const c = await connect(code);
    c.send({ t: "hello", v: 99, role: "guest", name: "x" });
    const m = await c.next();
    expect(m.code).toBe("bad_version");
    expect(String(m.message)).toContain("Update");
  });

  it("rejects malformed JSON, non-objects and unknown roles", async () => {
    const { code } = await createRoom();
    for (const payload of ["{nope", "[1,2]", "42", JSON.stringify({ t: "hello", v: 1, role: "admin" })]) {
      const c = await connect(code);
      c.send(payload);
      expect((await c.next()).code).toBe("bad_message");
    }
  });

  it("rejects an oversized message", async () => {
    const { code } = await createRoom();
    const c = await connect(code);
    c.send(JSON.stringify({ t: "hello", v: 1, role: "guest", name: "x".repeat(9000) }));
    expect((await c.next()).code).toBe("too_large");
  });

  it("cleans names: strips control characters and angle brackets, limits length, defaults empty", async () => {
    const { code, hostToken } = await createRoom();
    await joinHost(code, hostToken);
    const a = await joinGuest(code, "<b>Al\u0007ice</b>" + "z".repeat(60));
    const players = a.welcome.players as { name: string }[];
    expect(players[0]?.name).not.toMatch(/[<>\u0007]/);
    expect(players[0]?.name.length).toBeLessThanOrEqual(24);
    const b = await joinGuest(code, "   ");
    expect((b.welcome.players as { name: string }[]).at(-1)?.name).toBe("Guest");
  });
});

describe("guests", () => {
  it("get an id, a slot and a resume token, and the host sees them", async () => {
    const { code, hostToken } = await createRoom();
    const host = await joinHost(code, hostToken);
    const { welcome } = await joinGuest(code, "Alice");
    expect(welcome.t).toBe("welcome");
    expect(typeof welcome.resumeToken).toBe("string");
    expect(welcome.you).not.toBe("host");
    let presence = await host.until("presence");
    while ((presence.players as unknown[]).length === 0) presence = await host.until("presence");
    expect((presence.players as { name: string; slot: number }[]).map((p) => [p.name, p.slot])).toEqual([["Alice", 0]]);
  });

  it("refuses the ninth player", async () => {
    const { code, hostToken } = await createRoom();
    await joinHost(code, hostToken);
    for (let i = 0; i < 8; i++) expect((await joinGuest(code, `g${i}`)).welcome.t).toBe("welcome");
    expect((await joinGuest(code, "late")).welcome.code).toBe("room_full");
  });

  it("throttles join attempts from one address", async () => {
    const { code } = await createRoom();
    const codes: unknown[] = [];
    for (let i = 0; i < 7; i++) {
      const c = await connect(code, "192.0.2.77");
      c.send({ t: "hello", v: 1, role: "guest", name: "x" });
      codes.push((await c.next()).code ?? "ok");
    }
    expect(codes.slice(0, 5)).not.toContain("rate_limited");
    expect(codes.slice(5)).toEqual(["rate_limited", "rate_limited"]);
  });

  it("can resume with their token and keep id and slot", async () => {
    const { code, hostToken } = await createRoom();
    await joinHost(code, hostToken);
    const first = await joinGuest(code, "Alice");
    first.c.ws.close(1000, "blip");
    const again = await joinGuest(code, "ignored", { resumeToken: first.welcome.resumeToken });
    expect(again.welcome.you).toBe(first.welcome.you);
    expect((again.welcome.players as { slot: number }[])[0]?.slot).toBe(0);
  });

  it("cannot resume with a wrong token", async () => {
    const { code } = await createRoom();
    const { welcome } = await joinGuest(code, "x", { resumeToken: "not-a-real-token" });
    expect(welcome.code).toBe("bad_token");
  });
});

describe("holder rules", () => {
  it("only the host can set the holder, and only to a real player", async () => {
    const { code, hostToken } = await createRoom();
    const host = await joinHost(code, hostToken);
    const { c: guest, welcome } = await joinGuest(code, "Alice");
    guest.send({ t: "holder", id: welcome.you, epoch: 1 });
    expect((await guest.until("error")).code).toBe("bad_message");

    host.send({ t: "holder", id: "ffffffffffffffff", epoch: 1 });
    expect((await host.until("error")).code).toBe("bad_message");
  });

  it("broadcasts the holder and epoch to everyone and refuses an epoch that goes backwards", async () => {
    const { code, hostToken } = await createRoom();
    const host = await joinHost(code, hostToken);
    const { c: guest, welcome } = await joinGuest(code, "Alice");
    host.send({ t: "holder", id: welcome.you, epoch: 5 });
    for (;;) {
      const m = await guest.until("presence");
      if (m.epoch === 5) {
        expect(m.holder).toBe(welcome.you);
        break;
      }
    }
    host.send({ t: "holder", id: null, epoch: 2 });
    expect((await host.until("error")).code).toBe("bad_message");
  });

  it("relays binary frames from the holder only, prefixed with the sender id", async () => {
    const { code, hostToken } = await createRoom();
    const host = await joinHost(code, hostToken);
    const a = await joinGuest(code, "Alice");
    const b = await joinGuest(code, "Bob");
    host.send({ t: "holder", id: a.welcome.you, epoch: 1 });
    await a.c.until("presence");

    b.c.send(new Uint8Array([1, 2, 3]));
    a.c.send(new Uint8Array([9, 8, 7]));

    let frame: ArrayBuffer | undefined;
    for (let i = 0; i < 10 && !frame; i++) {
      const m = await host.raw();
      if (typeof m !== "string") frame = m;
    }
    expect(frame).toBeDefined();
    const bytes = new Uint8Array(frame as ArrayBuffer);
    expect([...bytes.slice(8)]).toEqual([9, 8, 7]);
    expect([...bytes.slice(0, 8)].map((x) => x.toString(16).padStart(2, "0")).join("")).toBe(a.welcome.you);
    expect(await host.silence()).toBe(true); // Bob's frame never arrived
  });
});

describe("signaling", () => {
  it("forwards signals both ways and stamps the real sender, ignoring a forged from", async () => {
    const { code, hostToken } = await createRoom();
    const host = await joinHost(code, hostToken);
    const { c: guest, welcome } = await joinGuest(code, "Alice");

    guest.send({ t: "signal", from: "host", sdp: "offer-sdp" });
    const atHost = await host.until("signal");
    expect(atHost.from).toBe(welcome.you);
    expect(atHost.sdp).toBe("offer-sdp");

    host.send({ t: "signal", to: welcome.you, sdp: "answer-sdp" });
    const atGuest = await guest.until("signal");
    expect(atGuest.from).toBe("host");
    expect(atGuest.sdp).toBe("answer-sdp");
  });

  it("rate limits a flooding socket", async () => {
    const { code } = await createRoom();
    const { c } = await joinGuest(code, "Flood");
    for (let i = 0; i < 40; i++) c.send({ t: "ping", ts: i });
    let limited = false;
    for (let i = 0; i < 60 && !limited; i++) {
      try {
        limited = (await c.next()).code === "rate_limited";
      } catch {
        break;
      }
    }
    expect(limited).toBe(true);
  });

  it("answers ping with pong", async () => {
    const { code } = await createRoom();
    const { c } = await joinGuest(code, "x");
    c.send({ t: "ping", ts: 123 });
    expect((await c.until("pong")).ts).toBe(123);
  });
});

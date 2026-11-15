import { SELF } from "cloudflare:test";
import { expect } from "vitest";

export type Msg = Record<string, unknown>;

export class Client {
  private queue: (string | ArrayBuffer)[] = [];
  private waiters: ((m: string | ArrayBuffer) => void)[] = [];
  closed: { code: number; reason: string } | null = null;

  constructor(readonly ws: WebSocket) {
    ws.binaryType = "arraybuffer";
    ws.accept();
    ws.addEventListener("message", (e) => {
      const data = e.data as string | ArrayBuffer;
      const w = this.waiters.shift();
      if (w) w(data);
      else this.queue.push(data);
    });
    ws.addEventListener("close", (e) => {
      this.closed = { code: e.code, reason: e.reason };
    });
  }

  send(m: Msg | string | ArrayBuffer | Uint8Array): void {
    const binary = typeof m === "object" && (ArrayBuffer.isView(m) || Object.prototype.toString.call(m) === "[object ArrayBuffer]");
    this.ws.send(typeof m === "string" || binary ? (m as string) : JSON.stringify(m));
  }

  raw(): Promise<string | ArrayBuffer> {
    const q = this.queue.shift();
    if (q !== undefined) return Promise.resolve(q);
    return new Promise((res, rej) => {
      const t = setTimeout(() => rej(new Error("timed out waiting for message")), 2000);
      this.waiters.push((m) => {
        clearTimeout(t);
        res(m);
      });
    });
  }

  async next(): Promise<Msg> {
    const m = await this.raw();
    return JSON.parse(m as string) as Msg;
  }

  /** Skips presence frames until a message of the given type arrives. */
  async until(type: string): Promise<Msg> {
    for (let i = 0; i < 20; i++) {
      const m = await this.next();
      if (m.t === type) return m;
    }
    throw new Error(`no ${type} message arrived`);
  }

  /** Waits, then returns every JSON message that arrived meanwhile, without leaving a pending waiter behind. */
  async drain(ms = 250): Promise<Msg[]> {
    await new Promise((r) => setTimeout(r, ms));
    const out: Msg[] = [];
    while (this.queue.length > 0) {
      const m = this.queue.shift();
      if (typeof m === "string") out.push(JSON.parse(m) as Msg);
    }
    return out;
  }

  async silence(ms = 150): Promise<boolean> {
    if (this.queue.length > 0) return false;
    await new Promise((r) => setTimeout(r, ms));
    return this.queue.length === 0;
  }
}

export async function createRoom(): Promise<{ code: string; hostToken: string }> {
  const res = await SELF.fetch("https://example.com/v1/rooms", { method: "POST" });
  expect(res.status).toBe(200);
  return (await res.json()) as { code: string; hostToken: string };
}

export async function connect(code: string, ip = "198.51.100.1"): Promise<Client> {
  const res = await SELF.fetch(`https://example.com/v1/room/${code}`, { headers: { Upgrade: "websocket", "cf-connecting-ip": ip } });
  expect(res.status).toBe(101);
  return new Client(res.webSocket as WebSocket);
}

export async function joinHost(code: string, hostToken: string): Promise<Client> {
  const c = await connect(code);
  c.send({ t: "hello", v: 1, role: "host", hostToken });
  const w = await c.next();
  expect(w.t).toBe("welcome");
  return c;
}

let ipCounter = 10;
export async function joinGuest(code: string, name = "Guest", extra: Msg = {}): Promise<{ c: Client; welcome: Msg }> {
  const c = await connect(code, `203.0.113.${ipCounter++}`);
  c.send({ t: "hello", v: 1, role: "guest", name, ...extra });
  return { c, welcome: await c.next() };
}


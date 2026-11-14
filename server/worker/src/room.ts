import { DurableObject } from "cloudflare:workers";
import {
  LIMITS,
  PROTOCOL_VERSION,
  constantTimeEqual,
  randomId,
  randomToken,
  sha256,
  type ErrorCode,
  type PlayerInfo,
} from "./protocol";

interface StoredPlayer {
  name: string;
  slot: number;
  resumeHash: string;
  awaySince: number | null;
}

interface Meta {
  hostHash: string;
  hostGoneAt: number | null;
  lastActivity: number;
  holder: string | null; // null means the host
  epoch: number;
  players: Record<string, StoredPlayer>;
}

interface Attachment {
  id: string | null; // null until hello succeeds
  role: "host" | "guest" | null;
  connectedAt: number;
  ip: string;
  winStart: number;
  winCount: number;
  relayWinStart: number;
  relayWinCount: number;
}

const HOST_ID = "host";
const ID_BYTES = 8;

function sanitizeName(raw: unknown): string {
  if (typeof raw !== "string") return "Guest";
  // eslint-disable-next-line no-control-regex
  const cleaned = raw.replace(/[\u0000-\u001f\u007f<>]/g, "").trim().slice(0, 24);
  return cleaned.length > 0 ? cleaned : "Guest";
}

function hexToBytes(hex: string): Uint8Array {
  const out = new Uint8Array(hex.length / 2);
  for (let i = 0; i < out.length; i++) out[i] = parseInt(hex.slice(i * 2, i * 2 + 2), 16);
  return out;
}

function bytesToHex(b: Uint8Array): string {
  return [...b].map((x) => x.toString(16).padStart(2, "0")).join("");
}

export class Room extends DurableObject {
  // Join throttling is best effort: it lives in memory and resets if the object is evicted.
  private joinAttempts = new Map<string, number[]>();

  override async fetch(req: Request): Promise<Response> {
    const url = new URL(req.url);

    if (url.pathname === "/init" && req.method === "POST") {
      if (await this.ctx.storage.get("meta")) return new Response("exists", { status: 409 });
      const meta: Meta = {
        hostHash: await req.text(),
        hostGoneAt: Date.now(),
        lastActivity: Date.now(),
        holder: null,
        epoch: 0,
        players: {},
      };
      await this.ctx.storage.put("meta", meta);
      await this.reschedule(meta);
      return new Response("created", { status: 201 });
    }

    if (url.pathname === "/ws") {
      if (!(await this.ctx.storage.get("meta"))) return new Response("room_not_found", { status: 404 });
      const pair = new WebSocketPair();
      this.ctx.acceptWebSocket(pair[1]);
      const att: Attachment = {
        id: null, role: null, connectedAt: Date.now(), ip: req.headers.get("x-pts-client") ?? "unknown",
        winStart: 0, winCount: 0, relayWinStart: 0, relayWinCount: 0,
      };
      pair[1].serializeAttachment(att);
      const meta = (await this.ctx.storage.get<Meta>("meta")) as Meta;
      await this.reschedule(meta);
      return new Response(null, { status: 101, webSocket: pair[0] });
    }

    return new Response("not found", { status: 404 });
  }

  override async webSocketMessage(ws: WebSocket, data: string | ArrayBuffer): Promise<void> {
    const att = ws.deserializeAttachment() as Attachment;
    const now = Date.now();

    if (typeof data !== "string") {
      await this.onBinary(ws, att, data, now);
      return;
    }

    if (data.length > LIMITS.maxSignalingBytes) {
      this.fail(ws, "too_large");
      return;
    }
    if (this.overRate(ws, att, now)) {
      this.fail(ws, "rate_limited");
      return;
    }

    let msg: Record<string, unknown>;
    try {
      const parsed: unknown = JSON.parse(data);
      if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) throw new Error("not an object");
      msg = parsed as Record<string, unknown>;
    } catch {
      this.fail(ws, "bad_message");
      return;
    }

    const meta = await this.loadMeta();
    if (!meta) {
      ws.close(1008, "room_not_found");
      return;
    }

    if (att.id === null) {
      await this.onHello(ws, att, msg, meta, now);
      return;
    }

    if (msg.t === "ping") {
      this.send(ws, { t: "pong", ts: msg.ts });
      return;
    }

    if (att.role === "host") await this.onHostMessage(ws, msg, meta, now);
    else await this.onGuestMessage(ws, att, msg, meta);
  }

  override async webSocketClose(ws: WebSocket): Promise<void> {
    await this.onGone(ws);
  }

  override async webSocketError(ws: WebSocket): Promise<void> {
    await this.onGone(ws);
  }

  override async alarm(): Promise<void> {
    const meta = await this.loadMeta();
    if (!meta) return;
    const now = Date.now();

    for (const ws of this.ctx.getWebSockets()) {
      const att = ws.deserializeAttachment() as Attachment;
      if (att.id === null && now - att.connectedAt >= LIMITS.helloTimeoutMs) ws.close(1008, "hello_timeout");
    }

    const hostAbsent = meta.hostGoneAt !== null && now - meta.hostGoneAt >= LIMITS.hostGraceMs;
    const idle = meta.hostGoneAt === null && now - meta.lastActivity >= LIMITS.roomIdleMs;
    if (hostAbsent || idle) {
      await this.endRoom(idle ? "idle" : "host_left");
      return;
    }

    let changed = false;
    for (const [id, p] of Object.entries(meta.players)) {
      if (p.awaySince !== null && now - p.awaySince >= LIMITS.hostGraceMs) {
        delete meta.players[id];
        if (meta.holder === id) {
          meta.holder = null;
          meta.epoch++;
        }
        changed = true;
      }
    }
    if (changed) {
      await this.ctx.storage.put("meta", meta);
      this.broadcastPresence(meta);
    }
    await this.reschedule(meta);
  }

  // ---- hello ----

  private async onHello(ws: WebSocket, att: Attachment, msg: Record<string, unknown>, meta: Meta, now: number): Promise<void> {
    if (msg.t !== "hello") return this.fail(ws, "bad_message");
    if (msg.v !== PROTOCOL_VERSION) return this.fail(ws, "bad_version", "Update PassTheStick to the latest version.");

    if (msg.role === "host") {
      if (typeof msg.hostToken !== "string") return this.fail(ws, "bad_token");
      const hash = await sha256(msg.hostToken);
      if (!constantTimeEqual(hash, meta.hostHash)) return this.fail(ws, "bad_token");
      const existing = this.ctx.getWebSockets().find((s) => s !== ws && (s.deserializeAttachment() as Attachment).role === "host");
      if (existing) return this.fail(ws, "host_exists");

      att.id = HOST_ID;
      att.role = "host";
      ws.serializeAttachment(att);
      meta.hostGoneAt = null;
      meta.lastActivity = now;
      await this.ctx.storage.put("meta", meta);
      this.send(ws, this.welcome(meta, HOST_ID));
      this.broadcastPresence(meta);
      await this.reschedule(meta);
      return;
    }

    if (msg.role !== "guest") return this.fail(ws, "bad_message");
    if (this.throttledJoin(att.ip, now)) return this.fail(ws, "rate_limited");

    let id: string | null = null;
    let resumeToken: string | undefined;
    if (typeof msg.resumeToken === "string") {
      const hash = await sha256(msg.resumeToken);
      id = Object.keys(meta.players).find((k) => constantTimeEqual((meta.players[k] as StoredPlayer).resumeHash, hash)) ?? null;
      if (id === null) return this.fail(ws, "bad_token");
      for (const other of this.ctx.getWebSockets()) {
        if (other !== ws && (other.deserializeAttachment() as Attachment).id === id) other.close(1000, "replaced");
      }
      (meta.players[id] as StoredPlayer).awaySince = null;
    } else {
      const max = LIMITS.maxPlayers;
      if (Object.keys(meta.players).length >= max) return this.fail(ws, "room_full");
      id = randomId();
      resumeToken = randomToken();
      const used = new Set(Object.values(meta.players).map((p) => p.slot));
      let slot = 0;
      while (used.has(slot)) slot++;
      meta.players[id] = { name: sanitizeName(msg.name), slot, resumeHash: await sha256(resumeToken), awaySince: null };
    }

    att.id = id;
    att.role = "guest";
    ws.serializeAttachment(att);
    await this.ctx.storage.put("meta", meta);
    this.send(ws, { ...this.welcome(meta, id), resumeToken });
    this.broadcastPresence(meta);
    await this.reschedule(meta);
  }

  // ---- host and guest traffic ----

  private async onHostMessage(ws: WebSocket, msg: Record<string, unknown>, meta: Meta, now: number): Promise<void> {
    meta.lastActivity = now;
    switch (msg.t) {
      case "signal": {
        const target = this.socketFor(msg.to);
        if (target) this.send(target, { ...msg, from: HOST_ID, to: undefined });
        break;
      }
      case "holder": {
        const id = msg.id === null || msg.id === HOST_ID ? null : msg.id;
        if (id !== null && (typeof id !== "string" || !meta.players[id])) return this.fail(ws, "bad_message");
        if (typeof msg.epoch !== "number" || !Number.isInteger(msg.epoch) || msg.epoch < meta.epoch) return this.fail(ws, "bad_message");
        meta.holder = id;
        meta.epoch = msg.epoch;
        await this.ctx.storage.put("meta", meta);
        this.broadcastPresence(meta);
        break;
      }
      case "kick": {
        const id = typeof msg.id === "string" ? msg.id : "";
        if (meta.players[id]) {
          delete meta.players[id];
          if (meta.holder === id) {
            meta.holder = null;
            meta.epoch++;
          }
          this.socketFor(id)?.close(1000, "kicked");
          await this.ctx.storage.put("meta", meta);
          this.broadcastPresence(meta);
        }
        break;
      }
      default:
        this.fail(ws, "bad_message");
    }
  }

  private async onGuestMessage(ws: WebSocket, att: Attachment, msg: Record<string, unknown>, meta: Meta): Promise<void> {
    const host = this.hostSocket();
    switch (msg.t) {
      case "signal":
        if (host) this.send(host, { ...msg, from: att.id, to: undefined });
        break;
      case "request":
        if (host && meta.holder !== att.id) this.send(host, { t: "request", from: att.id });
        break;
      case "chat": {
        const text = typeof msg.text === "string" ? msg.text.slice(0, 140) : "";
        if (host && text) this.send(host, { t: "chat", from: att.id, text });
        break;
      }
      default:
        this.fail(ws, "bad_message");
    }
  }

  /** Binary relay: only used when peer-to-peer fails. Frame layout is [8-byte player id][payload]. */
  private async onBinary(ws: WebSocket, att: Attachment, data: ArrayBuffer, now: number): Promise<void> {
    if (att.id === null || data.byteLength > 1024) {
      this.fail(ws, "too_large");
      return;
    }
    const meta = await this.loadMeta();
    if (!meta) return;

    if (att.role === "guest") {
      const holder = meta.holder === att.id;
      const cap = holder ? LIMITS.relayFramesPerSecondHolder : LIMITS.relayFramesPerSecond;
      if (now - att.relayWinStart >= 1000) {
        att.relayWinStart = now;
        att.relayWinCount = 0;
      }
      att.relayWinCount++;
      ws.serializeAttachment(att);
      if (!holder || att.relayWinCount > cap) return; // non-holder frames never reach the host
      const host = this.hostSocket();
      if (!host) return;
      const out = new Uint8Array(ID_BYTES + data.byteLength);
      out.set(hexToBytes(att.id), 0);
      out.set(new Uint8Array(data), ID_BYTES);
      host.send(out);
      return;
    }

    if (data.byteLength <= ID_BYTES) return;
    const target = this.socketFor(bytesToHex(new Uint8Array(data, 0, ID_BYTES)));
    if (target) target.send(data.slice(ID_BYTES));
  }

  // ---- lifecycle ----

  private async onGone(ws: WebSocket): Promise<void> {
    const att = ws.deserializeAttachment() as Attachment | null;
    const meta = await this.loadMeta();
    if (!att || att.id === null || !meta) return;
    const now = Date.now();

    if (att.role === "host") {
      const stillHere = this.ctx.getWebSockets().some((s) => s !== ws && (s.deserializeAttachment() as Attachment).role === "host");
      if (stillHere) return;
      meta.hostGoneAt = now;
    } else {
      const replacement = this.ctx.getWebSockets().some((s) => s !== ws && (s.deserializeAttachment() as Attachment).id === att.id);
      const player = meta.players[att.id];
      if (!player || replacement) return;
      player.awaySince = now;
    }
    await this.ctx.storage.put("meta", meta);
    this.broadcastPresence(meta);
    await this.reschedule(meta);
  }

  private async endRoom(reason: string): Promise<void> {
    for (const ws of this.ctx.getWebSockets()) {
      this.send(ws, { t: "ended", reason });
      ws.close(1000, reason);
    }
    await this.ctx.storage.deleteAlarm();
    await this.ctx.storage.deleteAll();
  }

  private async reschedule(meta: Meta): Promise<void> {
    const now = Date.now();
    const deadlines: number[] = [];
    if (meta.hostGoneAt !== null) deadlines.push(meta.hostGoneAt + LIMITS.hostGraceMs);
    else deadlines.push(meta.lastActivity + LIMITS.roomIdleMs);
    for (const p of Object.values(meta.players)) if (p.awaySince !== null) deadlines.push(p.awaySince + LIMITS.hostGraceMs);
    for (const ws of this.ctx.getWebSockets()) {
      const att = ws.deserializeAttachment() as Attachment;
      if (att.id === null) deadlines.push(att.connectedAt + LIMITS.helloTimeoutMs);
    }
    await this.ctx.storage.setAlarm(Math.max(now + 100, Math.min(...deadlines)));
  }

  // ---- helpers ----

  private async loadMeta(): Promise<Meta | undefined> {
    return this.ctx.storage.get<Meta>("meta");
  }

  private hostSocket(): WebSocket | undefined {
    return this.ctx.getWebSockets().find((s) => (s.deserializeAttachment() as Attachment).role === "host");
  }

  private socketFor(id: unknown): WebSocket | undefined {
    if (typeof id !== "string") return undefined;
    return this.ctx.getWebSockets().find((s) => (s.deserializeAttachment() as Attachment).id === id);
  }

  private players(meta: Meta): (PlayerInfo & { away?: boolean })[] {
    return Object.entries(meta.players)
      .map(([id, p]) => ({ id, name: p.name, slot: p.slot, ...(p.awaySince !== null ? { away: true } : {}) }))
      .sort((a, b) => a.slot - b.slot);
  }

  private welcome(meta: Meta, you: string): Record<string, unknown> {
    return {
      t: "welcome", v: PROTOCOL_VERSION, you, players: this.players(meta),
      holder: meta.holder ?? HOST_ID, epoch: meta.epoch, hostAway: meta.hostGoneAt !== null, maxPlayers: LIMITS.maxPlayers,
    };
  }

  private broadcastPresence(meta: Meta): void {
    const frame = {
      t: "presence", players: this.players(meta), holder: meta.holder ?? HOST_ID, epoch: meta.epoch, hostAway: meta.hostGoneAt !== null,
    };
    for (const ws of this.ctx.getWebSockets()) if ((ws.deserializeAttachment() as Attachment).id !== null) this.send(ws, frame);
  }

  private overRate(ws: WebSocket, att: Attachment, now: number): boolean {
    if (now - att.winStart >= 1000) {
      att.winStart = now;
      att.winCount = 0;
    }
    att.winCount++;
    ws.serializeAttachment(att);
    return att.winCount > LIMITS.messagesPerSecond;
  }

  private throttledJoin(ip: string, now: number): boolean {
    const recent = (this.joinAttempts.get(ip) ?? []).filter((t) => now - t < 60_000);
    recent.push(now);
    this.joinAttempts.set(ip, recent);
    return recent.length > LIMITS.joinAttemptsPerMinute;
  }

  private send(ws: WebSocket, body: unknown): void {
    try {
      ws.send(JSON.stringify(body));
    } catch {
      // The peer is gone; the close handler cleans up.
    }
  }

  private fail(ws: WebSocket, code: ErrorCode, message?: string): void {
    this.send(ws, { t: "error", code, message: message ?? code.replace(/_/g, " ") });
    ws.close(1008, code);
  }
}

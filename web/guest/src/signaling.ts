export const PROTOCOL_VERSION = 1;

export interface PlayerInfo {
  id: string;
  name: string;
  slot: number;
  away?: boolean;
}

export type ServerMessage =
  | { t: "welcome"; v: number; you: string; players: PlayerInfo[]; holder: string; epoch: number; hostAway: boolean; resumeToken?: string }
  | { t: "presence"; players: PlayerInfo[]; holder: string; epoch: number; hostAway: boolean }
  | { t: "signal"; from: string; sdp?: RTCSessionDescriptionInit; candidate?: RTCIceCandidateInit }
  | { t: "chat"; from: string; text: string }
  | { t: "ended"; reason: string }
  | { t: "error"; code: string; message: string }
  | { t: "pong"; ts: number };

export type ConnectionState = "connecting" | "open" | "reconnecting" | "closed";

/** Delay before reconnect attempt n (0-based): 0.5 s doubling to 8 s, plus up to 20% jitter. */
export function backoffMs(attempt: number, random: () => number = Math.random): number {
  const base = Math.min(8000, 500 * 2 ** attempt);
  return Math.round(base * (1 + random() * 0.2));
}

/** Errors that mean retrying will not help. */
export const FATAL_ERRORS = new Set(["bad_version", "room_not_found", "room_full", "bad_token", "rate_limited"]);

export function friendlyError(code: string): string {
  switch (code) {
    case "bad_version":
      return "This page and the host run different versions. Ask the host to update PassTheStick, then reload.";
    case "room_not_found":
      return "That code is wrong or the session has ended. Ask the host for a new code.";
    case "room_full":
      return "This session is full. Ask the host to make room.";
    case "rate_limited":
      return "Too many attempts. Wait a minute and try again.";
    case "bad_token":
      return "Your place in the session expired. Join again with the code.";
    default:
      return "Something went wrong with the connection. Reload the page and try again.";
  }
}

export interface SignalingEvents {
  message: (m: ServerMessage) => void;
  binary: (data: ArrayBuffer) => void;
  state: (s: ConnectionState) => void;
  fatal: (code: string, text: string) => void;
}

export class SignalingClient {
  private ws: WebSocket | null = null;
  private attempt = 0;
  private closedByUs = false;
  private resumeToken: string | undefined;
  private timer: ReturnType<typeof setTimeout> | undefined;

  constructor(
    private readonly baseUrl: string,
    private readonly code: string,
    private readonly name: string,
    private readonly events: SignalingEvents,
    private readonly makeSocket: (url: string) => WebSocket = (u) => new WebSocket(u),
  ) {}

  connect(): void {
    this.closedByUs = false;
    this.events.state(this.attempt === 0 ? "connecting" : "reconnecting");
    const ws = this.makeSocket(`${this.baseUrl}/v1/room/${this.code}`);
    ws.binaryType = "arraybuffer";
    this.ws = ws;

    ws.addEventListener("open", () => {
      this.send({ t: "hello", v: PROTOCOL_VERSION, role: "guest", name: this.name, resumeToken: this.resumeToken });
    });
    ws.addEventListener("message", (e: MessageEvent) => {
      if (typeof e.data !== "string") {
        this.events.binary(e.data as ArrayBuffer);
        return;
      }
      let m: ServerMessage;
      try {
        m = JSON.parse(e.data) as ServerMessage;
      } catch {
        return;
      }
      if (m.t === "welcome") {
        this.attempt = 0;
        if (m.resumeToken) this.resumeToken = m.resumeToken;
        this.events.state("open");
      } else if (m.t === "error" && FATAL_ERRORS.has(m.code)) {
        this.closedByUs = true;
        this.events.state("closed");
        this.events.fatal(m.code, friendlyError(m.code));
        return;
      } else if (m.t === "ended") {
        this.closedByUs = true;
        this.events.state("closed");
      }
      this.events.message(m);
    });
    ws.addEventListener("close", () => {
      if (this.closedByUs) return;
      this.events.state("reconnecting");
      this.timer = setTimeout(() => this.connect(), backoffMs(this.attempt++));
    });
  }

  send(body: unknown): void {
    if (this.ws?.readyState === 1) this.ws.send(JSON.stringify(body));
  }

  sendBinary(data: ArrayBuffer): void {
    if (this.ws?.readyState === 1) this.ws.send(data);
  }

  close(): void {
    this.closedByUs = true;
    if (this.timer) clearTimeout(this.timer);
    this.ws?.close(1000, "leave");
    this.events.state("closed");
  }
}

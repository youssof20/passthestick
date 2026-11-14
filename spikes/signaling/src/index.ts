import { DurableObject } from "cloudflare:workers";
interface Env { ROOM: DurableObjectNamespace<Room>; }

export default {
  async fetch(req: Request, env: Env): Promise<Response> {
    const url = new URL(req.url);
    const m = url.pathname.match(/^\/room\/([A-Z2-9]{6})$/);
    if (!m) return new Response("not found", { status: 404 });
    return env.ROOM.get(env.ROOM.idFromName(m[1])).fetch(req);
  },
};

type Tag = { role: "host" | "guest"; id: string };

export class Room extends DurableObject {
  async fetch(req: Request): Promise<Response> {
    if (req.headers.get("Upgrade") !== "websocket") return new Response("expected websocket", { status: 426 });
    const role = new URL(req.url).searchParams.get("role") === "host" ? "host" : "guest";
    if (role === "host" && this.ctx.getWebSockets("host").length > 0) return new Response("host exists", { status: 409 });
    const id = crypto.randomUUID().slice(0, 8);
    const pair = new WebSocketPair();
    this.ctx.acceptWebSocket(pair[1], [role, id]);
    pair[1].serializeAttachment({ role, id } satisfies Tag);
    return new Response(null, { status: 101, webSocket: pair[0] });
  }
  webSocketMessage(ws: WebSocket, data: string | ArrayBuffer) {
    const me = ws.deserializeAttachment() as Tag;
    if (me.role === "guest") {
      for (const h of this.ctx.getWebSockets("host")) h.send(typeof data === "string" ? JSON.stringify({ from: me.id, d: data }) : data);
    } else if (typeof data === "string") {
      const { to, d } = JSON.parse(data);
      for (const g of this.ctx.getWebSockets(to)) g.send(d);
    }
  }
  webSocketClose(ws: WebSocket, code: number) { ws.close(code, "bye"); }
}

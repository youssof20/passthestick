import { Room } from "./room";
import { isValidRoomCode, randomRoomCode, randomToken, sha256 } from "./protocol";

export { Room };

export interface Env {
  ROOM: DurableObjectNamespace<Room>;
}

const json = (body: unknown, status = 200): Response =>
  new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json", "cache-control": "no-store" } });

export default {
  async fetch(req: Request, env: Env): Promise<Response> {
    const url = new URL(req.url);

    if (req.method === "POST" && url.pathname === "/v1/rooms") {
      // A collision is detected by the Room itself (init refuses an existing room), so retry with a new code.
      for (let attempt = 0; attempt < 8; attempt++) {
        const code = randomRoomCode();
        const hostToken = randomToken();
        const stub = env.ROOM.get(env.ROOM.idFromName(code));
        const res = await stub.fetch("https://room/init", { method: "POST", body: await sha256(hostToken) });
        if (res.status === 201) return json({ code, hostToken });
        if (res.status !== 409) return json({ error: "internal" }, 500);
      }
      return json({ error: "internal" }, 500);
    }

    const m = url.pathname.match(/^\/v1\/room\/([A-Za-z0-9]+)$/);
    if (m) {
      const code = (m[1] as string).toUpperCase();
      if (!isValidRoomCode(code)) return json({ error: "room_not_found" }, 404);
      if (req.headers.get("Upgrade") !== "websocket") return json({ error: "expected_websocket" }, 426);
      const stub = env.ROOM.get(env.ROOM.idFromName(code));
      const headers = new Headers(req.headers);
      headers.set("x-pts-client", req.headers.get("cf-connecting-ip") ?? "unknown");
      return stub.fetch(new Request("https://room/ws", { headers }));
    }

    return json({ error: "not_found" }, 404);
  },
} satisfies ExportedHandler<Env>;

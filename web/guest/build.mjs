// Bundles the guest client into site/join/. Deployment settings come from the environment:
//   PTS_SIGNALING_URL  wss URL of the Worker (default: local wrangler dev)
//   PTS_ICE_SERVERS    JSON array of RTCIceServer (default: a public STUN server)
import { build } from "esbuild";
import { copyFileSync, mkdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const out = join(here, "../../site/join");
mkdirSync(out, { recursive: true });

const signaling = process.env.PTS_SIGNALING_URL ?? "ws://127.0.0.1:8787";
const ice = process.env.PTS_ICE_SERVERS ?? JSON.stringify([{ urls: "stun:stun.cloudflare.com:3478" }]);
JSON.parse(ice);

await build({
  entryPoints: [join(here, "src/main.ts")],
  outfile: join(out, "app.js"),
  bundle: true,
  minify: true,
  target: "es2022",
  sourcemap: false,
  define: { __SIGNALING_URL__: JSON.stringify(signaling), __ICE_SERVERS__: ice },
});
copyFileSync(join(here, "index.html"), join(out, "index.html"));
copyFileSync(join(here, "join.css"), join(out, "join.css"));
console.log(`built site/join (signaling ${signaling})`);

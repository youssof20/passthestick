import WebSocket from "ws";
const base = process.argv[2];
const open = (role) => new Promise((res, rej) => { const w = new WebSocket(`${base}/room/ABCDEF?role=${role}`); w.on("open", () => res(w)); w.on("error", rej); });
const host = await open("host"); const guest = await open("guest");
const sink = new Promise(r => host.on("message", m => r(JSON.parse(m.toString()))));
guest.send("hello");
console.log("host got", await sink);
const rtts = [];
host.on("message", m => { const { from, d } = JSON.parse(m.toString()); host.send(JSON.stringify({ to: from, d })); });
for (let i = 0; i < 200; i++) { const t = performance.now(); const p = new Promise(r => guest.once("message", r)); guest.send("p" + i); await p; rtts.push(performance.now() - t); await new Promise(r => setTimeout(r, 16)); }
rtts.sort((a, b) => a - b);
console.log("relay rtt ms p50", rtts[100].toFixed(2), "p95", rtts[190].toFixed(2), "max", rtts[199].toFixed(2));
let dup; try { dup = await open("host"); } catch (e) { dup = e.message; } console.log("second host:", typeof dup === "string" ? dup : "ACCEPTED (bad)");
process.exit(0);

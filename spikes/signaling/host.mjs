import WebSocket from "ws";
const w = new WebSocket("ws://127.0.0.1:8787/v1/room/C4N5GT");
w.on("open", () => w.send(JSON.stringify({ t: "hello", v: 1, role: "host", hostToken: "4g3_BbmcgvF5b4jreE8cig" })));
let given = false;
w.on("message", (m, bin) => {
  if (bin) { console.log("binary", m.length); return; }
  const j = JSON.parse(m.toString());
  console.log("host got", JSON.stringify(j).slice(0, 140));
  const g = (j.players || []).find(p => p.name === "Browser");
  if (g && !given) { given = true; setTimeout(() => w.send(JSON.stringify({ t: "holder", id: g.id, epoch: 1 })), 500); }
});
setTimeout(() => process.exit(0), 12000);

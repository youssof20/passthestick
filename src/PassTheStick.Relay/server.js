const WebSocket = require('ws');
const wss = new WebSocket.Server({ port: process.env.PORT || 8080 });

const rooms = new Map(); // roomCode -> { host, guests, players, idleTimeout }

function randomCode() {
  return Math.random().toString(36).substring(2, 6).toUpperCase();
}

const IDLE_MS = 30 * 60 * 1000; // 30 minutes

function scheduleRoomExpiry(roomCode) {
  const room = rooms.get(roomCode);
  if (!room) return;
  if (room.idleTimeout) clearTimeout(room.idleTimeout);
  room.idleTimeout = setTimeout(() => {
    rooms.delete(roomCode);
  }, IDLE_MS);
}

function resetRoomExpiry(roomCode) {
  const room = rooms.get(roomCode);
  if (!room) return;
  if (room.idleTimeout) clearTimeout(room.idleTimeout);
  room.idleTimeout = setTimeout(() => {
    rooms.delete(roomCode);
  }, IDLE_MS);
}

wss.on('connection', (ws) => {
  ws.id = Math.random().toString(36).substring(2, 10);
  ws.roomCode = null;

  ws.on('message', (raw) => {
    let msg;
    try { msg = JSON.parse(raw); } catch { return; }
    const roomCode = ws.roomCode;
    if (roomCode) resetRoomExpiry(roomCode);

    if (msg.type === 'CREATE') {
      const code = randomCode();
      rooms.set(code, { host: ws, guests: new Map(), players: new Map(), idleTimeout: null });
      ws.roomCode = code;
      ws.isHost = true;
      scheduleRoomExpiry(code);
      ws.send(JSON.stringify({ type: 'CREATED', roomCode: code, id: ws.id }));
    }

    else if (msg.type === 'JOIN') {
      const room = rooms.get(msg.roomCode);
      if (!room) { ws.send(JSON.stringify({ type: 'ERROR', msg: 'Room not found' })); return; }
      room.guests.set(ws.id, ws);
      room.players.set(ws.id, { name: msg.name });
      ws.roomCode = msg.roomCode;
      ws.isHost = false;
      ws.send(JSON.stringify({ type: 'JOINED', id: ws.id }));
      broadcastPlayerList(room);
    }

    else if (msg.type === 'PASS_STICK') {
      const room = rooms.get(ws.roomCode);
      if (!room || !ws.isHost) return;
      const target = room.guests.get(msg.toId);
      if (target) target.send(JSON.stringify({ type: 'YOU_HAVE_IT' }));
      broadcast(room, JSON.stringify({ type: 'PASS_STICK', toId: msg.toId }));
    }

    else if (msg.type === 'KEY_EVENT' || msg.type === 'PAD_STATE') {
      const room = rooms.get(ws.roomCode);
      if (!room || ws.isHost) return;
      if (room.host.readyState === WebSocket.OPEN)
        room.host.send(JSON.stringify(msg));
    }

    else if (msg.type === 'PING') {
      ws.send(JSON.stringify({ type: 'PONG', ts: msg.ts }));
    }
  });

  ws.on('close', () => {
    if (!ws.roomCode) return;
    const room = rooms.get(ws.roomCode);
    if (!room) return;
    if (room.idleTimeout) clearTimeout(room.idleTimeout);
    if (ws.isHost) {
      rooms.delete(ws.roomCode);
    } else {
      room.guests.delete(ws.id);
      room.players.delete(ws.id);
      broadcastPlayerList(room);
    }
  });
});

function broadcast(room, data) {
  if (room.host?.readyState === WebSocket.OPEN) room.host.send(data);
  room.guests.forEach(g => { if (g.readyState === WebSocket.OPEN) g.send(data); });
}

function broadcastPlayerList(room) {
  const players = [...room.players.entries()].map(([id, p]) => ({ id, name: p.name }));
  broadcast(room, JSON.stringify({ type: 'PLAYER_LIST', players }));
}

console.log('PassTheStick relay running');

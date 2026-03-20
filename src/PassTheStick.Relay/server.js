const WebSocket = require('ws');
const DEFAULT_RELAY_PORT = 8080;
const wss = new WebSocket.Server({ port: parseInt(process.env.PORT || '', 10) || DEFAULT_RELAY_PORT });

const rooms = new Map(); // roomCode -> { host, guests, players, activePlayerId, idleTimeout }

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
      rooms.set(code, { host: ws, guests: new Map(), players: new Map(), activePlayerId: ws.id, idleTimeout: null });
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

      // If the active player already holds the stick, notify this guest.
      if (room.activePlayerId && room.activePlayerId === ws.id) {
        ws.send(JSON.stringify({ type: 'YOU_HAVE_IT' }));
      }
    }

    else if (msg.type === 'PASS_STICK') {
      const room = rooms.get(ws.roomCode);
      if (!room || !ws.isHost) return;
      const target = room.guests.get(msg.toId);
      if (target) target.send(JSON.stringify({ type: 'YOU_HAVE_IT' }));
      room.activePlayerId = msg.toId;
      broadcast(room, JSON.stringify({ type: 'PASS_STICK', toId: msg.toId }));
    }

    else if (msg.type === 'CLOSE_ROOM') {
      const room = rooms.get(ws.roomCode);
      if (!room || !ws.isHost) return;
      const reason = (msg.reason || 'Host ended the session').toString();
      room.guests.forEach(g => {
        if (g.readyState === WebSocket.OPEN) {
          g.send(JSON.stringify({ type: 'SESSION_ENDED', reason }));
        }
      });
      rooms.delete(ws.roomCode);
    }

    else if (msg.type === 'HOST_REJOIN') {
      const code = (msg.roomCode || '').toString().toUpperCase().trim();
      if (!code) { ws.send(JSON.stringify({ type: 'ERROR', msg: 'Invalid room code' })); return; }

      let room = rooms.get(code);
      if (!room) {
        // Host disconnected earlier and the room was deleted — recreate with the same code so guests can rejoin.
        room = { host: ws, guests: new Map(), players: new Map(), activePlayerId: ws.id, idleTimeout: null };
        rooms.set(code, room);
        ws.roomCode = code;
        ws.isHost = true;
        scheduleRoomExpiry(code);
        ws.send(JSON.stringify({ type: 'REJOINED', roomCode: code, id: ws.id }));
        broadcastPlayerList(room);
        return;
      }

      room.host = ws;
      ws.roomCode = code;
      ws.isHost = true;
      scheduleRoomExpiry(code);

      ws.send(JSON.stringify({ type: 'REJOINED', roomCode: code, id: ws.id }));

      // If the stick was held by the previous host instance, update it to this host id.
      // Guests still reference their own ids, so only rewrite when the activeId isn't a guest.
      if (room.activePlayerId && room.activePlayerId !== ws.id && !room.guests.has(room.activePlayerId)) {
        room.activePlayerId = ws.id;
      }

      // Ensure everyone knows who currently has the stick.
      const activeId = room.activePlayerId || ws.id;
      broadcast(room, JSON.stringify({ type: 'PASS_STICK', toId: activeId }));
      broadcastPlayerList(room);
    }

    else if (msg.type === 'KEY_EVENT' || msg.type === 'PAD_STATE') {
      const room = rooms.get(ws.roomCode);
      if (!room || ws.isHost) return;
      if (room.host.readyState === WebSocket.OPEN)
        room.host.send(JSON.stringify({ ...msg, fromId: ws.id }));
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
      // Notify guests before deleting the room so they can reset UI and rejoin.
      room.guests.forEach(g => {
        if (g.readyState === WebSocket.OPEN) {
          g.send(JSON.stringify({ type: 'SESSION_ENDED', reason: 'Host disconnected' }));
        }
      });
      rooms.delete(ws.roomCode);
    } else {
      room.guests.delete(ws.id);
      room.players.delete(ws.id);

      // If the departing guest had the stick, reclaim to host immediately.
      if (room.activePlayerId === ws.id) {
        room.activePlayerId = room.host?.id;
        broadcast(room, JSON.stringify({ type: 'PASS_STICK', toId: room.activePlayerId }));
      }

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

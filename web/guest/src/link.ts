const CODE_RE = /^[ABCDEFGHJKMNPQRSTUVWXYZ23456789]{6}$/;

/** Turns whatever a person typed or pasted into a room code, or null. Accepts a full invite link too. */
export function parseRoomCode(input: string): string | null {
  let text = input.trim();
  const hash = text.lastIndexOf("#");
  if (hash >= 0) text = text.slice(hash + 1);
  text = text.toUpperCase().replace(/[\s-]/g, "");
  return CODE_RE.test(text) ? text : null;
}

/** Signaling endpoint. `?server=` lets a self-hosted deployment be tried without a rebuild. */
export function signalingBase(location: { search: string; host: string; protocol: string }, fallback: string): string {
  const custom = new URLSearchParams(location.search).get("server");
  if (custom && /^wss?:\/\/[A-Za-z0-9.\-:]+$/.test(custom)) return custom;
  return fallback;
}

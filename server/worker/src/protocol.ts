// Mirrors docs/protocol.md section 3. Changing a limit here means changing the doc and the tests.

export const PROTOCOL_VERSION = 1;
export const ROOM_CODE_ALPHABET = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
export const ROOM_CODE_LENGTH = 6;

export const LIMITS = {
  maxSignalingBytes: 8192,
  maxPlayers: 8,
  maxPlayersCap: 16,
  helloTimeoutMs: 5000,
  hostGraceMs: 60_000,
  roomIdleMs: 30 * 60_000,
  messagesPerSecond: 20,
  // The host sends an offer and several candidates per guest, so a reconnect to a full room is a burst.
  hostMessagesPerSecond: 300,
  relayFramesPerSecond: 64,
  relayFramesPerSecondHolder: 250,
  joinAttemptsPerMinute: 5,
} as const;

export type ErrorCode =
  | "bad_version"
  | "room_not_found"
  | "room_full"
  | "bad_token"
  | "host_exists"
  | "rate_limited"
  | "too_large"
  | "bad_message";

export interface PlayerInfo {
  id: string;
  name: string;
  slot: number;
}

export function isValidRoomCode(code: string): boolean {
  if (code.length !== ROOM_CODE_LENGTH) return false;
  for (const c of code) if (!ROOM_CODE_ALPHABET.includes(c)) return false;
  return true;
}

export function randomRoomCode(): string {
  const bytes = new Uint8Array(ROOM_CODE_LENGTH);
  const out: string[] = [];
  // Rejection sampling: 256 % 31 !== 0, so discard values that would skew the distribution.
  const limit = 256 - (256 % ROOM_CODE_ALPHABET.length);
  while (out.length < ROOM_CODE_LENGTH) {
    crypto.getRandomValues(bytes);
    for (const b of bytes) {
      if (b < limit && out.length < ROOM_CODE_LENGTH) out.push(ROOM_CODE_ALPHABET[b % ROOM_CODE_ALPHABET.length] as string);
    }
  }
  return out.join("");
}

export function randomToken(): string {
  const b = crypto.getRandomValues(new Uint8Array(16));
  return btoa(String.fromCharCode(...b)).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

export function randomId(): string {
  const b = crypto.getRandomValues(new Uint8Array(8));
  return [...b].map((x) => x.toString(16).padStart(2, "0")).join("");
}

export async function sha256(text: string): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text));
  return [...new Uint8Array(digest)].map((x) => x.toString(16).padStart(2, "0")).join("");
}

export function constantTimeEqual(a: string, b: string): boolean {
  if (a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}

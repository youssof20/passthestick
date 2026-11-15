import type { SignalingClient } from "./signaling";
import { encodeKeyRelay } from "./wire";

export type PathKind = "connecting" | "direct" | "turn" | "relay";

export interface PeerEvents {
  path: (kind: PathKind, rttMs: number | null) => void;
  control: (message: Record<string, unknown>) => void;
  rumble: (strong: number, weak: number, ms: number) => void;
}

export const RELAY_FALLBACK_MS = 8000;

/** WebRTC link to the host with the room WebSocket as the last resort. The host creates the offer and both channels. */
export class HostLink {
  private pc: RTCPeerConnection | null = null;
  private stateChannel: RTCDataChannel | null = null;
  private controlChannel: RTCDataChannel | null = null;
  private relay = false;
  private fallbackTimer: ReturnType<typeof setTimeout> | undefined;
  private statsTimer: ReturnType<typeof setInterval> | undefined;
  private signalChain: Promise<void> = Promise.resolve();

  constructor(
    private readonly signaling: SignalingClient,
    private readonly iceServers: RTCIceServer[],
    private readonly events: PeerEvents,
  ) {}

  get usingRelay(): boolean {
    return this.relay;
  }

  /**
   * Signals are handled one at a time, in arrival order. The host sends an offer and its candidates back to back; handled
   * concurrently, a candidate could reach addIceCandidate before setRemoteDescription finished and be silently lost.
   */
  onSignal(msg: { sdp?: RTCSessionDescriptionInit; candidate?: RTCIceCandidateInit }): Promise<void> {
    this.signalChain = this.signalChain.then(() => this.handleSignal(msg)).catch(() => undefined);
    return this.signalChain;
  }

  private async handleSignal(msg: { sdp?: RTCSessionDescriptionInit; candidate?: RTCIceCandidateInit }): Promise<void> {
    if (msg.sdp?.type === "offer") {
      this.reset();
      const pc = new RTCPeerConnection({ iceServers: this.iceServers });
      this.pc = pc;
      pc.onicecandidate = (e) => {
        if (e.candidate) this.signaling.send({ t: "signal", candidate: e.candidate.toJSON() });
      };
      pc.ondatachannel = (e) => this.adopt(e.channel);
      pc.onconnectionstatechange = () => {
        if (pc.connectionState === "failed") this.fallBackToRelay();
      };
      await pc.setRemoteDescription(msg.sdp);
      const answer = await pc.createAnswer();
      await pc.setLocalDescription(answer);
      this.signaling.send({ t: "signal", sdp: pc.localDescription?.toJSON() });
      this.events.path("connecting", null);
      this.fallbackTimer = setTimeout(() => {
        if (!this.channelsOpen()) this.fallBackToRelay();
      }, RELAY_FALLBACK_MS);
    } else if (msg.candidate && this.pc) {
      await this.pc.addIceCandidate(msg.candidate).catch(() => undefined);
    }
  }

  /** Latest-wins state frame. Dropped, not queued, when the link is busy: stale input is worse than none. */
  sendState(frame: ArrayBuffer): void {
    if (this.stateChannel?.readyState === "open") {
      if (this.stateChannel.bufferedAmount < 4096) this.stateChannel.send(frame);
    } else if (this.relay) {
      this.signaling.sendBinary(frame);
    }
  }

  sendControl(message: Record<string, unknown>): void {
    if (this.controlChannel?.readyState === "open") this.controlChannel.send(JSON.stringify(message));
    else if (this.relay && message.t === "request") this.signaling.send(message);
    else if (this.relay && message.t === "key") this.signaling.sendBinary(encodeKeyRelay(0, String(message.code), message.down === true));
  }

  close(): void {
    this.reset();
  }

  private channelsOpen(): boolean {
    return this.stateChannel?.readyState === "open" && this.controlChannel?.readyState === "open";
  }

  private adopt(ch: RTCDataChannel): void {
    ch.binaryType = "arraybuffer";
    if (ch.label === "state") this.stateChannel = ch;
    else if (ch.label === "control") {
      this.controlChannel = ch;
      ch.onmessage = (e) => {
        if (typeof e.data !== "string") return;
        try {
          const m = JSON.parse(e.data) as Record<string, unknown>;
          if (m.t === "rumble") this.events.rumble(Number(m.strong) || 0, Number(m.weak) || 0, Number(m.ms) || 0);
          else this.events.control(m);
        } catch {
          // Ignore malformed control messages.
        }
      };
    }
    ch.onopen = () => {
      if (this.channelsOpen()) this.onOpen();
    };
  }

  private onOpen(): void {
    clearTimeout(this.fallbackTimer);
    this.relay = false;
    void this.pollStats();
    this.statsTimer = setInterval(() => void this.pollStats(), 2000);
  }

  private async pollStats(): Promise<void> {
    if (!this.pc) return;
    const report = await this.pc.getStats();
    let pair: Record<string, unknown> | undefined;
    const candidates = new Map<string, Record<string, unknown>>();
    report.forEach((s: Record<string, unknown>) => {
      if (s.type === "candidate-pair" && (s.state === "succeeded" && (s.nominated === true || s.selected === true))) pair = s;
      if (s.type === "local-candidate" || s.type === "remote-candidate") candidates.set(String(s.id), s);
    });
    if (!pair) return;
    const local = candidates.get(String(pair.localCandidateId));
    const remote = candidates.get(String(pair.remoteCandidateId));
    const viaTurn = local?.candidateType === "relay" || remote?.candidateType === "relay";
    const rtt = typeof pair.currentRoundTripTime === "number" ? Math.round(pair.currentRoundTripTime * 1000) : null;
    this.events.path(viaTurn ? "turn" : "direct", rtt);
  }

  private fallBackToRelay(): void {
    if (this.relay) return;
    this.relay = true;
    this.events.path("relay", null);
  }

  private reset(): void {
    clearTimeout(this.fallbackTimer);
    clearInterval(this.statsTimer);
    this.pc?.close();
    this.pc = null;
    this.stateChannel = null;
    this.controlChannel = null;
    this.relay = false;
  }
}

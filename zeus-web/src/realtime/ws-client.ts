import { decodeDisplayFrame, FrameDecodeError, MSG_TYPE_DISPLAY_FRAME } from './frame';
import { AudioFrameDecodeError, MSG_TYPE_AUDIO_PCM, decodeAudioFrame } from '../audio/frame';
import { getAudioClient } from '../audio/audio-client';
import { useConnectionStore, type WisdomPhase } from '../state/connection-store';
import { useDisplayStore } from '../state/display-store';
import { useTxStore } from '../state/tx-store';
import { warnOnce } from '../util/logger';

const INITIAL_BACKOFF_MS = 1000;
const MAX_BACKOFF_MS = 8000;

// Binary WS frame type for TX meters: 1 type byte + 9 × f32 LE
// (fwdWatts, refWatts, swr, micDbfs, eqPk, lvlrPk, alcPk, alcGr, outPk).
// Contract locked with the server team in Zeus.Contracts/TxMetersFrame.cs —
// the last 5 floats are TXA per-stage peak readings added for the TX-quality
// diagnostic strip; they read −∞ / 0 while idle.
export const MSG_TYPE_TX_METERS = 0x11;
const TX_METERS_BYTES = 1 + 4 * 9;

// RX S-meter: 1 type byte + 1 × f32 LE (dBm). Broadcast at ~5 Hz from
// DspPipelineService; server clamps floor to −160 dBm before send.
export const MSG_TYPE_RX_METER = 0x14;
const RX_METER_BYTES = 1 + 4;

// Alert frame: 1 type byte + 1 kind byte + UTF-8 message (variable length).
// Server emits when SWR > 2.5 sustained ≥500 ms (PRD FR-6). Kind 0 = SWR trip.
export const MSG_TYPE_ALERT = 0x13;

// WDSP wisdom status: 1 type byte + 1 phase byte (0=idle, 1=building, 2=ready).
// Pushed once on WS attach and again on every transition. The UI disables the
// Connect button and pulses while phase=building so the user doesn't try to
// talk to the radio while FFTW is still planning.
export const MSG_TYPE_WISDOM_STATUS = 0x15;
const WISDOM_STATUS_BYTES = 1 + 1;

// Mic uplink (client → server). Payload: 960 × f32le = 3840 bytes preceded by
// the 1-byte type, total 3841 bytes. 960 samples = 20 ms @ 48 kHz mono.
// Contract: PRD FR-2, server TxAudioIngest handler.
export const MSG_TYPE_MIC_PCM = 0x20;
const MIC_PCM_SAMPLES = 960;
const MIC_PCM_BYTES = 1 + MIC_PCM_SAMPLES * 4;

// Shared by startRealtime / sendMicPcm. Single WS instance at a time; writes
// are no-ops when the socket isn't open.
let activeWs: WebSocket | null = null;

function wsUrl(path: string): string {
  if (typeof window === 'undefined') return path;
  const proto = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
  return `${proto}//${window.location.host}${path}`;
}

/**
 * Send a 960-sample mic PCM block to the server as a binary WS frame.
 * No-op when disconnected, so callers can blast blocks at 50 Hz without
 * needing to gate on connection state.
 */
export function sendMicPcm(samples: Float32Array): void {
  const ws = activeWs;
  if (!ws || ws.readyState !== WebSocket.OPEN) return;
  if (samples.length !== MIC_PCM_SAMPLES) {
    warnOnce(
      'ws-mic-pcm-size',
      `mic block must be ${MIC_PCM_SAMPLES} samples; got ${samples.length}`,
    );
    return;
  }
  const buf = new ArrayBuffer(MIC_PCM_BYTES);
  const view = new DataView(buf);
  view.setUint8(0, MSG_TYPE_MIC_PCM);
  // Copy the block payload; DataView writes are host-endian-agnostic and
  // match the server's BitConverter.ToSingle on little-endian hosts. Use
  // setFloat32 per-sample with explicit LE for a portable wire format.
  for (let i = 0; i < MIC_PCM_SAMPLES; i++) {
    view.setFloat32(1 + i * 4, samples[i] ?? 0, true);
  }
  try {
    ws.send(buf);
  } catch (err) {
    warnOnce('ws-mic-send', 'mic send failed', err);
  }
}

export function startRealtime(path = '/ws'): () => void {
  let ws: WebSocket | null = null;
  let backoff = INITIAL_BACKOFF_MS;
  let timer: ReturnType<typeof setTimeout> | null = null;
  let stopped = false;

  const { pushFrame, setConnected } = useDisplayStore.getState();

  const connect = () => {
    if (stopped) return;
    try {
      ws = new WebSocket(wsUrl(path));
    } catch (err) {
      warnOnce('ws-construct-failed', 'WebSocket construction failed', err);
      schedule();
      return;
    }
    ws.binaryType = 'arraybuffer';
    activeWs = ws;

    ws.onopen = () => {
      backoff = INITIAL_BACKOFF_MS;
      setConnected(true);
    };
    ws.onclose = () => {
      setConnected(false);
      // PRD FR-6: if the WS drops while keyed, the UI must not keep showing TX.
      // Server-side, StreamingHub drops MOX on its end — this is the paired
      // client-side cleanup so the MOX button reverts to RX even if we can't
      // round-trip a POST (the HTTP path may be down too).
      if (useTxStore.getState().moxOn) useTxStore.getState().setMoxOn(false);
      if (activeWs === ws) activeWs = null;
      ws = null;
      schedule();
    };
    ws.onerror = () => {
      /* onclose will fire next */
    };
    ws.onmessage = (ev) => {
      if (!(ev.data instanceof ArrayBuffer)) return;
      try {
        const peekType = new DataView(ev.data).getUint8(0);
        if (peekType === MSG_TYPE_DISPLAY_FRAME) {
          const frame = decodeDisplayFrame(ev.data);
          pushFrame(frame);
          return;
        }
        if (peekType === MSG_TYPE_AUDIO_PCM) {
          const audio = decodeAudioFrame(ev.data);
          getAudioClient().push(audio);
          return;
        }
        if (peekType === MSG_TYPE_TX_METERS) {
          if (ev.data.byteLength < TX_METERS_BYTES) {
            warnOnce(
              'ws-tx-meters-short',
              `tx meters frame too short: ${ev.data.byteLength}`,
            );
            return;
          }
          const dv = new DataView(ev.data);
          useTxStore.getState().setMeters({
            fwdWatts: dv.getFloat32(1, true),
            refWatts: dv.getFloat32(5, true),
            swr: dv.getFloat32(9, true),
            micDbfs: dv.getFloat32(13, true),
            eqPk: dv.getFloat32(17, true),
            lvlrPk: dv.getFloat32(21, true),
            alcPk: dv.getFloat32(25, true),
            alcGr: dv.getFloat32(29, true),
            outPk: dv.getFloat32(33, true),
          });
          return;
        }
        if (peekType === MSG_TYPE_RX_METER) {
          if (ev.data.byteLength < RX_METER_BYTES) {
            warnOnce(
              'ws-rx-meter-short',
              `rx meter frame too short: ${ev.data.byteLength}`,
            );
            return;
          }
          const dbm = new DataView(ev.data).getFloat32(1, true);
          useTxStore.getState().setRxDbm(dbm);
          return;
        }
        if (peekType === MSG_TYPE_WISDOM_STATUS) {
          if (ev.data.byteLength < WISDOM_STATUS_BYTES) {
            warnOnce(
              'ws-wisdom-short',
              `wisdom frame too short: ${ev.data.byteLength}`,
            );
            return;
          }
          const raw = new DataView(ev.data).getUint8(1);
          const phase: WisdomPhase =
            raw === 1 ? 'building' : raw === 2 ? 'ready' : 'idle';
          useConnectionStore.getState().setWisdomPhase(phase);
          return;
        }
        if (peekType === MSG_TYPE_ALERT) {
          if (ev.data.byteLength < 2) {
            warnOnce('ws-alert-short', `alert frame too short: ${ev.data.byteLength}`);
            return;
          }
          const dv = new DataView(ev.data);
          const kind = dv.getUint8(1);
          const msgBytes = new Uint8Array(ev.data, 2);
          const message = new TextDecoder('utf-8').decode(msgBytes);
          useTxStore.getState().setAlert({ kind, message });
          return;
        }
        warnOnce(
          `ws-msgtype-${peekType}`,
          `ignoring msgType 0x${peekType.toString(16)}`,
        );
      } catch (err) {
        if (err instanceof FrameDecodeError || err instanceof AudioFrameDecodeError) {
          warnOnce(`ws-decode-${err.message.slice(0, 32)}`, err.message);
        } else {
          warnOnce('ws-decode-unknown', 'frame decode failed', err);
        }
      }
    };
  };

  const schedule = () => {
    if (stopped) return;
    timer = setTimeout(connect, backoff);
    backoff = Math.min(backoff * 2, MAX_BACKOFF_MS);
  };

  connect();

  return () => {
    stopped = true;
    if (timer != null) clearTimeout(timer);
    if (ws) {
      ws.onopen = null;
      ws.onclose = null;
      ws.onerror = null;
      ws.onmessage = null;
      ws.close();
      if (activeWs === ws) activeWs = null;
      ws = null;
    }
    setConnected(false);
  };
}

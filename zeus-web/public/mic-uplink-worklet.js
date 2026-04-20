// AudioWorkletProcessor that accumulates mono mic samples into 960-sample
// (20 ms @ 48 kHz) blocks and transfers each block to the main thread over
// this.port. Matches the server-side MSG_TYPE_MIC_PCM = 0x20 contract.
//
// Lives in public/ (not src/) so Vite serves it verbatim at /mic-uplink-worklet.js
// without ES-module transpilation. AudioWorklet loader (addModule) needs a URL
// to a standalone JS file with no imports; a bundled worker chunk won't work.
//
// Sample rate: we rely on the caller creating the AudioContext with
// sampleRate: 48000 so the implicit `sampleRate` global inside this processor
// equals 48000. If that contract breaks we'd need a resampler here.

const BLOCK_SAMPLES = 960;

class MicUplinkProcessor extends AudioWorkletProcessor {
  constructor() {
    super();
    this._buf = new Float32Array(BLOCK_SAMPLES);
    this._fill = 0;
  }

  process(inputs) {
    const input = inputs[0];
    if (!input || input.length === 0) return true;
    const ch = input[0];
    if (!ch) return true;

    let srcIdx = 0;
    while (srcIdx < ch.length) {
      const room = BLOCK_SAMPLES - this._fill;
      const take = Math.min(room, ch.length - srcIdx);
      this._buf.set(ch.subarray(srcIdx, srcIdx + take), this._fill);
      this._fill += take;
      srcIdx += take;

      if (this._fill === BLOCK_SAMPLES) {
        // Transfer the buffer to the main thread (zero-copy) and allocate
        // a fresh one. New-alloc cost is ~192 KB/s at 50 Hz, acceptable.
        // Peak-level computation runs here (once per 20 ms block) so the
        // main thread can drive a mic meter at exactly the uplink cadence.
        const out = this._buf;
        let peak = 0;
        for (let i = 0; i < BLOCK_SAMPLES; i++) {
          const a = out[i] < 0 ? -out[i] : out[i];
          if (a > peak) peak = a;
        }
        this._buf = new Float32Array(BLOCK_SAMPLES);
        this._fill = 0;
        this.port.postMessage({ samples: out, peak }, [out.buffer]);
      }
    }
    return true;
  }
}

registerProcessor('mic-uplink', MicUplinkProcessor);

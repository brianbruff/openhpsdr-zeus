// Mic uplink: getUserMedia → AudioContext @ 48 kHz → MediaStreamSource →
// AudioWorkletNode('mic-uplink'). The worklet frames 128-sample ScriptProcessor
// chunks into 960-sample (20 ms) blocks and posts them here; we forward each
// block to the caller-supplied handler (typically ws-client to ship [0x20] ...).
//
//
// Ham-radio constraints: echoCancellation/noiseSuppression/autoGainControl all
// OFF so WDSP TXA is the only thing shaping mic audio. Browser constraint
// request for sampleRate: 48000 — most browsers honor this; if not, the
// worklet will mis-frame (resampler is a future concern).

// `peak` is the max(abs(sample)) across the 20 ms block, linear [0..1].
// Callers convert to dBFS via 20 * log10(peak); floor at −100 for silence.
export type MicUplinkBlockHandler = (samples: Float32Array, peak: number) => void;

export type MicUplinkHandle = {
  stop: () => Promise<void>;
};

const MIC_CONSTRAINTS: MediaStreamConstraints = {
  audio: {
    echoCancellation: false,
    noiseSuppression: false,
    autoGainControl: false,
    channelCount: 1,
    sampleRate: 48000,
  },
};

const WORKLET_URL = '/mic-uplink-worklet.js';
const EXPECTED_BLOCK_SAMPLES = 960;

export async function startMicUplink(
  onBlock: MicUplinkBlockHandler,
): Promise<MicUplinkHandle> {
  if (typeof navigator === 'undefined' || !navigator.mediaDevices?.getUserMedia) {
    throw new Error('getUserMedia not available in this environment');
  }
  const stream = await navigator.mediaDevices.getUserMedia(MIC_CONSTRAINTS);
  const context = new AudioContext({ sampleRate: 48000, latencyHint: 'interactive' });

  const cleanupStream = () => {
    for (const t of stream.getTracks()) {
      try { t.stop(); } catch { /* already stopped */ }
    }
  };

  try {
    if (context.state === 'suspended') {
      try { await context.resume(); } catch { /* may resolve later */ }
    }
    await context.audioWorklet.addModule(WORKLET_URL);
    const source = context.createMediaStreamSource(stream);
    const node = new AudioWorkletNode(context, 'mic-uplink', {
      numberOfInputs: 1,
      numberOfOutputs: 0,
      channelCount: 1,
      channelCountMode: 'explicit',
      channelInterpretation: 'discrete',
    });
    node.port.onmessage = (ev: MessageEvent<{ samples?: Float32Array; peak?: number }>) => {
      const samples = ev.data?.samples;
      const peak = typeof ev.data?.peak === 'number' ? ev.data.peak : 0;
      if (samples instanceof Float32Array && samples.length === EXPECTED_BLOCK_SAMPLES) {
        onBlock(samples, peak);
      }
    };
    source.connect(node);

    return {
      stop: async () => {
        try { node.port.onmessage = null; } catch { /* ignore */ }
        try { source.disconnect(); } catch { /* ignore */ }
        try { node.disconnect(); } catch { /* ignore */ }
        cleanupStream();
        try { await context.close(); } catch { /* ignore */ }
      },
    };
  } catch (err) {
    cleanupStream();
    try { await context.close(); } catch { /* ignore */ }
    throw err;
  }
}

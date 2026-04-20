import { describe, expect, it } from 'vitest';
import {
  AUDIO_HEADER_BYTES,
  AUDIO_BODY_FIXED_BYTES,
  MSG_TYPE_AUDIO_PCM,
  decodeAudioFrame,
  encodeAudioFrame,
  AudioFrameDecodeError,
} from './frame';

function sampleFrame(sampleCount: number, channels = 1) {
  const samples = new Float32Array(sampleCount * channels);
  for (let i = 0; i < samples.length; i++) samples[i] = Math.sin(i * 0.01) * 0.5;
  return {
    seq: 7,
    tsUnixMs: 1_700_000_000_456.25,
    rxId: 0,
    channels,
    sampleRateHz: 48_000,
    sampleCount,
    samples,
  };
}

describe('decodeAudioFrame', () => {
  it('round-trips mono', () => {
    const frame = sampleFrame(256);
    const buf = encodeAudioFrame(frame);
    expect(buf.byteLength).toBe(
      AUDIO_HEADER_BYTES + AUDIO_BODY_FIXED_BYTES + frame.sampleCount * 4,
    );

    const dec = decodeAudioFrame(buf);
    expect(dec.msgType).toBe(MSG_TYPE_AUDIO_PCM);
    expect(dec.seq).toBe(frame.seq);
    expect(dec.tsUnixMs).toBe(frame.tsUnixMs);
    expect(dec.channels).toBe(1);
    expect(dec.sampleRateHz).toBe(48_000);
    expect(dec.sampleCount).toBe(frame.sampleCount);
    expect(dec.samples.length).toBe(frame.samples.length);
    for (let i = 0; i < frame.samples.length; i++) {
      expect(dec.samples[i]).toBeCloseTo(frame.samples[i]!, 5);
    }
  });

  it('round-trips stereo', () => {
    const frame = sampleFrame(128, 2);
    const dec = decodeAudioFrame(encodeAudioFrame(frame));
    expect(dec.channels).toBe(2);
    expect(dec.samples.length).toBe(128 * 2);
  });

  it('rejects wrong msgType', () => {
    const frame = sampleFrame(16);
    const buf = encodeAudioFrame(frame);
    new DataView(buf).setUint8(0, 0x99);
    expect(() => decodeAudioFrame(buf)).toThrow(AudioFrameDecodeError);
  });

  it('rejects truncated buffer', () => {
    const frame = sampleFrame(64);
    const full = encodeAudioFrame(frame);
    const truncated = full.slice(0, full.byteLength - 4);
    expect(() => decodeAudioFrame(truncated)).toThrow(AudioFrameDecodeError);
  });
});

import { describe, expect, it } from 'vitest';
import {
  HEADER_BYTES,
  BODY_FIXED_BYTES,
  MSG_TYPE_DISPLAY_FRAME,
  decodeDisplayFrame,
  encodeDisplayFrame,
  FrameDecodeError,
} from './frame';

function sampleFrame(width: number) {
  const panDb = new Float32Array(width);
  const wfDb = new Float32Array(width);
  for (let i = 0; i < width; i++) {
    panDb[i] = -80 + (i % 32);
    wfDb[i] = -90 + (i % 16);
  }
  return {
    seq: 42,
    tsUnixMs: 1_700_000_000_123.5,
    rxId: 0,
    bodyFlags: 0x03,
    width,
    centerHz: 14_074_000n,
    hzPerPixel: 192_000 / width,
    panDb,
    wfDb,
  };
}

describe('decodeDisplayFrame', () => {
  it('round-trips happy path', () => {
    const frame = sampleFrame(1024);
    const buf = encodeDisplayFrame(frame);
    expect(buf.byteLength).toBe(HEADER_BYTES + BODY_FIXED_BYTES + frame.width * 4 * 2);

    const dec = decodeDisplayFrame(buf);
    expect(dec.msgType).toBe(MSG_TYPE_DISPLAY_FRAME);
    expect(dec.seq).toBe(frame.seq);
    expect(dec.tsUnixMs).toBe(frame.tsUnixMs);
    expect(dec.rxId).toBe(frame.rxId);
    expect(dec.bodyFlags).toBe(frame.bodyFlags);
    expect(dec.panValid).toBe(true);
    expect(dec.wfValid).toBe(true);
    expect(dec.width).toBe(frame.width);
    expect(dec.centerHz).toBe(frame.centerHz);
    expect(dec.hzPerPixel).toBeCloseTo(frame.hzPerPixel, 4);
    expect(dec.panDb.length).toBe(frame.width);
    expect(dec.wfDb.length).toBe(frame.width);
    for (let i = 0; i < frame.width; i++) {
      expect(dec.panDb[i]).toBeCloseTo(frame.panDb[i]!, 5);
      expect(dec.wfDb[i]).toBeCloseTo(frame.wfDb[i]!, 5);
    }
  });

  it('reports valid bits from bodyFlags', () => {
    const frame = sampleFrame(128);
    frame.bodyFlags = 0x01;
    const dec = decodeDisplayFrame(encodeDisplayFrame(frame));
    expect(dec.panValid).toBe(true);
    expect(dec.wfValid).toBe(false);
  });

  it('rejects wrong msgType', () => {
    const frame = sampleFrame(64);
    const buf = encodeDisplayFrame(frame);
    new DataView(buf).setUint8(0, 0x99);
    expect(() => decodeDisplayFrame(buf)).toThrow(FrameDecodeError);
  });

  it('rejects truncated buffer', () => {
    const frame = sampleFrame(64);
    const full = encodeDisplayFrame(frame);
    const truncated = full.slice(0, full.byteLength - 4);
    expect(() => decodeDisplayFrame(truncated)).toThrow(FrameDecodeError);
  });

  it('rejects mismatched payloadLen vs width', () => {
    const frame = sampleFrame(64);
    const buf = encodeDisplayFrame(frame);
    new DataView(buf).setUint16(2, BODY_FIXED_BYTES + 4, true);
    expect(() => decodeDisplayFrame(buf)).toThrow(FrameDecodeError);
  });
});

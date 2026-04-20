// Smoke: boots a Node ws server that emits 10 encoded DisplayFrame messages
// using the same binary layout, connects a client, runs frames through the
// decoder, and asserts structural invariants. This is the decoder-only check
// that substitutes for a real browser end-to-end run.

import { WebSocketServer, WebSocket } from 'ws';
import { createServer } from 'node:http';

const HEADER_BYTES = 16;
const BODY_FIXED_BYTES = 16;
const MSG = 0x01;
const WIDTH = 512;
const FRAME_COUNT = 10;
const CENTER_HZ = 14_074_000n;
const HZ_PER_PX = 192_000 / WIDTH;

function encodeFrame(seq) {
  const pixelBytes = WIDTH * 4;
  const payloadLen = BODY_FIXED_BYTES + pixelBytes * 2;
  const buf = new ArrayBuffer(HEADER_BYTES + payloadLen);
  const dv = new DataView(buf);
  dv.setUint8(0, MSG);
  dv.setUint8(1, 0);
  dv.setUint16(2, payloadLen, true);
  dv.setUint32(4, seq, true);
  dv.setFloat64(8, Date.now(), true);
  dv.setUint8(HEADER_BYTES + 0, 0);
  dv.setUint8(HEADER_BYTES + 1, 0x03);
  dv.setUint16(HEADER_BYTES + 2, WIDTH, true);
  dv.setBigInt64(HEADER_BYTES + 4, CENTER_HZ, true);
  dv.setFloat32(HEADER_BYTES + 12, HZ_PER_PX, true);
  const panOff = HEADER_BYTES + BODY_FIXED_BYTES;
  const wfOff = panOff + pixelBytes;
  const pan = new Float32Array(buf, panOff, WIDTH);
  const wf = new Float32Array(buf, wfOff, WIDTH);
  const peakCol = (seq * 13) % WIDTH;
  for (let i = 0; i < WIDTH; i++) {
    const d = Math.abs(i - peakCol);
    pan[i] = -90 + Math.max(0, 70 - d * 0.5);
    wf[i] = -95 + Math.max(0, 60 - d * 0.5);
  }
  return Buffer.from(buf);
}

function decode(buf) {
  const ab = buf.buffer.slice(buf.byteOffset, buf.byteOffset + buf.byteLength);
  const dv = new DataView(ab);
  if (dv.getUint8(0) !== MSG) throw new Error(`bad msgType ${dv.getUint8(0)}`);
  const payloadLen = dv.getUint16(2, true);
  const seq = dv.getUint32(4, true);
  const tsUnixMs = dv.getFloat64(8, true);
  if (ab.byteLength < HEADER_BYTES + payloadLen) {
    throw new Error(`short buffer for seq ${seq}`);
  }
  const width = dv.getUint16(HEADER_BYTES + 2, true);
  const centerHz = dv.getBigInt64(HEADER_BYTES + 4, true);
  const hzPerPixel = dv.getFloat32(HEADER_BYTES + 12, true);
  const panOff = HEADER_BYTES + BODY_FIXED_BYTES;
  const wfOff = panOff + width * 4;
  // Node Buffer.buffer is SharedArrayBuffer-safe; slice to guarantee alignment.
  const panDb = new Float32Array(ab.slice(panOff, panOff + width * 4));
  const wfDb = new Float32Array(ab.slice(wfOff, wfOff + width * 4));
  return { seq, tsUnixMs, width, centerHz, hzPerPixel, panDb, wfDb };
}

function assertEq(name, got, want) {
  if (got !== want) {
    throw new Error(`assertion failed ${name}: got ${got}, want ${want}`);
  }
}

async function main() {
  const http = createServer();
  const wss = new WebSocketServer({ server: http });
  await new Promise((r) => http.listen(0, '127.0.0.1', r));
  const port = http.address().port;

  wss.on('connection', (sock) => {
    for (let i = 1; i <= FRAME_COUNT; i++) {
      sock.send(encodeFrame(i));
    }
    setTimeout(() => sock.close(), 50);
  });

  const received = [];
  await new Promise((resolve, reject) => {
    const ws = new WebSocket(`ws://127.0.0.1:${port}`);
    ws.binaryType = 'nodebuffer';
    ws.on('message', (buf) => {
      const dec = decode(buf);
      received.push(dec);
    });
    ws.on('close', resolve);
    ws.on('error', reject);
  });

  wss.close();
  http.close();

  assertEq('frame count', received.length, FRAME_COUNT);
  for (let i = 0; i < FRAME_COUNT; i++) {
    const f = received[i];
    assertEq(`seq[${i}]`, f.seq, i + 1);
    assertEq(`width[${i}]`, f.width, WIDTH);
    assertEq(`centerHz[${i}]`, f.centerHz, CENTER_HZ);
    if (Math.abs(f.hzPerPixel - HZ_PER_PX) > 1e-3) {
      throw new Error(`hzPerPixel[${i}] drift: ${f.hzPerPixel} vs ${HZ_PER_PX}`);
    }
    assertEq(`panDb.length[${i}]`, f.panDb.length, WIDTH);
    assertEq(`wfDb.length[${i}]`, f.wfDb.length, WIDTH);
    let peak = -Infinity;
    let peakIdx = -1;
    for (let k = 0; k < f.panDb.length; k++) {
      if (f.panDb[k] > peak) {
        peak = f.panDb[k];
        peakIdx = k;
      }
    }
    const expectedPeak = ((i + 1) * 13) % WIDTH;
    if (peakIdx !== expectedPeak) {
      throw new Error(`peak col[${i}] expected ${expectedPeak}, got ${peakIdx}`);
    }
  }
  console.log(`smoke: ${FRAME_COUNT} frames encoded and decoded cleanly`);
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});

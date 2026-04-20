export const PAN_VS = /* glsl */ `#version 300 es
layout(location = 0) in float aDb;
uniform float uWidth;
uniform float uDbMin;
uniform float uDbMax;
uniform float uOffsetPx;
void main() {
  float x = (float(gl_VertexID) + 0.5 + uOffsetPx) / uWidth;
  float n = clamp((aDb - uDbMin) / (uDbMax - uDbMin), 0.0, 1.0);
  gl_Position = vec4(x * 2.0 - 1.0, n * 2.0 - 1.0, 0.0, 1.0);
}`;

export const PAN_FS = /* glsl */ `#version 300 es
precision highp float;
uniform vec3 uColor;
out vec4 fragColor;
void main() { fragColor = vec4(uColor, 1.0); }`;

// Fill under the trace. Pan dB values live in a 1-row R32F texture; vertex
// IDs map 2i → bottom vertex for bin i, 2i+1 → top, so texelFetch at
// `gl_VertexID >> 1` yields the same dB for both verts of a bin. Rendered as
// a TRIANGLE_STRIP this produces one thin quad per bin, alpha-faded from 0
// at the floor to `uFillAlphaTop` at the trace for the warm-glow look.
export const PAN_FILL_VS = /* glsl */ `#version 300 es
uniform sampler2D uPan;
uniform float uWidth;
uniform float uDbMin;
uniform float uDbMax;
uniform float uOffsetPx;
uniform float uFillAlphaTop;
out float v_alpha;
void main() {
  int binIdx = gl_VertexID >> 1;
  bool isTop = (gl_VertexID & 1) == 1;
  float aDb = texelFetch(uPan, ivec2(binIdx, 0), 0).r;
  float x = (float(binIdx) + 0.5 + uOffsetPx) / uWidth;
  float n = clamp((aDb - uDbMin) / (uDbMax - uDbMin), 0.0, 1.0);
  float y = isTop ? (n * 2.0 - 1.0) : -1.0;
  v_alpha = isTop ? uFillAlphaTop : 0.0;
  gl_Position = vec4(x * 2.0 - 1.0, y, 0.0, 1.0);
}`;

export const PAN_FILL_FS = /* glsl */ `#version 300 es
precision highp float;
in float v_alpha;
uniform vec3 uColor;
out vec4 fragColor;
void main() { fragColor = vec4(uColor * v_alpha, v_alpha); }`;

export const CURSOR_VS = /* glsl */ `#version 300 es
layout(location = 0) in vec2 aPos;
void main() { gl_Position = vec4(aPos, 0.0, 1.0); }`;

export const CURSOR_FS = /* glsl */ `#version 300 es
precision highp float;
uniform vec3 uColor;
out vec4 fragColor;
void main() { fragColor = vec4(uColor, 0.6); }`;

// Waterfall quad: fullscreen triangle-pair, samples the history texture
// with a vertical rolling offset so the newest row is at the top.
export const WF_VS = /* glsl */ `#version 300 es
layout(location = 0) in vec2 aPos;
out vec2 vUv;
void main() {
  vUv = aPos * 0.5 + 0.5;
  gl_Position = vec4(aPos, 0.0, 1.0);
}`;

export const WF_FS = /* glsl */ `#version 300 es
precision highp float;
in vec2 vUv;
uniform sampler2D uHistory;
uniform sampler2D uLut;
uniform float uDbMin;
uniform float uDbMax;
uniform float uWriteRow;
uniform float uH;
uniform float uBgAlpha;
out vec4 fragColor;
void main() {
  // vUv.y == 1.0 at top of canvas; newest row sits at the top.
  // row = (writeRow - (1 - vUv.y) * H) mod H, normalised.
  float agePx = (1.0 - vUv.y) * uH;
  float row = mod(uWriteRow - agePx + uH, uH);
  float v = texture(uHistory, vec2(vUv.x, (row + 0.5) / uH)).r;
  float n = clamp((v - uDbMin) / (uDbMax - uDbMin), 0.0, 1.0);
  vec4 c = texture(uLut, vec2(n, 0.5));
  // uBgAlpha=1 → fully opaque (normal mode). uBgAlpha=0 → noise floor is
  // fully transparent and signal peaks fade in proportionally; map/background
  // shows through between carriers. Smoothstep widens the signal-visible band
  // a touch so weaker activity still registers.
  float a = mix(smoothstep(0.05, 0.9, n), 1.0, uBgAlpha);
  fragColor = vec4(c.rgb * a, a);
}`;

// Horizontal-shift pass for doc 08 §5 ping-pong: sample the previous history
// at vUv.x - shiftUv, fall back to a background-noise seed dB where the shift
// exposes fresh columns. Rendered into the inactive R32F texture; the main
// WF_FS then reads from the now-active texture next draw.
export const WF_SHIFT_FS = /* glsl */ `#version 300 es
precision highp float;
in vec2 vUv;
uniform sampler2D uSrc;
uniform float uShiftUv;
uniform float uSeedDb;
layout(location = 0) out vec4 fragColor;
void main() {
  float srcX = vUv.x - uShiftUv;
  float v = (srcX < 0.0 || srcX > 1.0)
    ? uSeedDb
    : texture(uSrc, vec2(srcX, vUv.y)).r;
  fragColor = vec4(v, 0.0, 0.0, 1.0);
}`;

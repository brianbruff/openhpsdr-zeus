# openhpsdr-zeus

A modern SDR console for the Hermes Lite 2 — .NET 10 backend with a React + WebGL frontend served over WebSocket. Other OpenHPSDR Protocol-1 radios (ANAN, etc.) aren't supported yet.

## About the name

This project originally started life as **openhpsdr-nereus**. Nereus is a nice piece of lore — in Greek mythology he's the "Old Man of the Sea" and the father of Thetis, which makes him a fitting nod to the existing [Thetis](https://github.com/TAPR/OpenHPSDR-Thetis) project that much of the DSP heritage comes from.

The problem: somebody else was already working on a C++ AetherSDR-style project called **nereusSDR**. Two HPSDR-adjacent projects sharing a name got confusing fast, so we renamed.

Meet **Zeus** — king of all the gods. Thetis' grandfather-in-law (she married Peleus and their son was Achilles, but that's another story). It doesn't really get more regal than that. Zeus it is.

## Layout

- `Zeus.Server/` — ASP.NET Core host, SignalR/WebSocket streaming, radio discovery
- `Zeus.Protocol1/` — OpenHPSDR Protocol-1 client and framing
- `Zeus.Dsp/` — DSP engine (WDSP via P/Invoke + synthetic fallback)
- `Zeus.Contracts/` — wire-format DTOs shared between backend and web
- `zeus-web/` — Vite/React/TypeScript frontend with WebGL panadapter + waterfall
- `native/wdsp/` — WDSP build scaffolding
- `tests/Zeus.*.Tests/` — xUnit tests for each project

## Running

```bash
# backend (listens on :6060)
dotnet run --project Zeus.Server

# frontend (dev server on :5173, proxies to :6060)
cd zeus-web && npm install && npm run dev
```

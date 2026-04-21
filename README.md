# OpenHPSDR Zeus — The King of SDRs

![Zeus](docs/pics/zeus1.webp)

A browser-based SDR console for the **Hermes Lite 2**. .NET 10 backend talks
OpenHPSDR Protocol-1 to the radio and streams IQ / audio / meter data to a
React + WebGL frontend over WebSocket.

> Status: early but working. HL2 only. RX is solid; TX is operator-verified on
> FM and TUNE (v0.1, April 2026). Other Protocol-1 radios (ANAN etc.) are not
> yet supported.

## About the name

**Zeus** — king of the gods. It doesn't really get more regal than that. The
name is also a nod to [Thetis](https://github.com/TAPR/OpenHPSDR-Thetis), the
long-running project a lot of the DSP heritage traces back to.

## What's in the box

- **WebGL panadapter + waterfall** with zoom, click-to-tune, drag-pan gestures
- **DSP panel**: NB, NR, ANF, SNB, NBP — all driven by WDSP under the hood
- **Bands / modes / bandwidth / AGC / attenuator / preamp / drive / mic gain**
- **TX**: PTT, TUNE, mic uplink, TX stage meters, SWR-trip and TX-timeout
  protection
- **S-meter** (live + demo), RX meter frame streaming
- **Leaflet satellite map** with terminator and QRZ grid-square / beam heading
  — to interact with the map (pan / zoom), **press and hold the `M` key**.
  The experience isn't ideal yet and will improve over time.
- **Radio discovery** on the LAN (Protocol-1 broadcast)

## Layout

| Path                     | What it is                                          |
| ------------------------ | --------------------------------------------------- |
| `Zeus.Server/`           | ASP.NET Core host, SignalR hub, radio service       |
| `Zeus.Protocol1/`        | OpenHPSDR Protocol-1 client, framing, discovery     |
| `Zeus.Dsp/`              | DSP engine — WDSP via P/Invoke + synthetic fallback |
| `Zeus.Contracts/`        | Wire-format DTOs shared backend ↔ web               |
| `zeus-web/`              | Vite + React + TypeScript + WebGL frontend         |
| `native/wdsp/`           | WDSP build scaffolding for the native DSP library  |
| `tests/Zeus.*.Tests/`    | xUnit tests                                         |
| `tools/zeus-dump/`       | Protocol-1 packet dump utility                      |
| `tools/discovery-probe/` | LAN discovery probe                                 |

## Running

Build the frontend once; it lands in `Zeus.Server/wwwroot/`, then ASP.NET Core
serves everything — UI, API, WebSocket — on a single port.

```bash
# Build the web UI (one-time, or whenever zeus-web changes)
cd zeus-web && npm install && npm run build && cd ..

# Run the server on :6060
dotnet run --project Zeus.Server
```

Open **http://localhost:6060**, hit **Discover**, and connect to your HL2.

## Getting started (developers)

The backend and frontend run as two independent processes during development.
The Vite dev server proxies `/api` and the WebSocket hub through to the .NET
host, so you get hot-reload on the React side without rebuilding the server.

### Prerequisites

- **.NET 10 SDK**
- **Node.js 20+** (npm ships with it)
- **git**
- A **Hermes Lite 2** on the same LAN (optional — the synthetic DSP engine
  lets you drive most of the UI without a radio)

### First-time setup

```bash
git clone https://github.com/brianbruff/openhpsdr-zeus.git
cd openhpsdr-zeus

# Restore .NET dependencies and sanity-check the build
dotnet restore
dotnet build Zeus.slnx

# Install frontend dependencies
npm --prefix zeus-web install
```

### Dev loop (two terminals)

```bash
# Terminal 1 — backend on :6060
dotnet run --project Zeus.Server

# Terminal 2 — Vite dev server on :5173 (proxies /api and /hub to :6060)
npm --prefix zeus-web run dev
```

Then open **http://localhost:5173**. Edits under `zeus-web/src/` hot-reload;
changes under `Zeus.Server/`, `Zeus.Protocol1/`, `Zeus.Dsp/`, or
`Zeus.Contracts/` require restarting the backend.

If you'd rather run the production-style single-port build, use the `Running`
instructions above — the bundled UI is served directly from the .NET host on
`:6060`.

### Tests

```bash
dotnet test Zeus.slnx
```

Please ensure tests pass before opening a PR.

### Useful tools

- `tools/zeus-dump/` — Protocol-1 packet dumper, handy for protocol debugging
- `tools/discovery-probe/` — LAN discovery probe for Protocol-1 radios

### Project conventions

- Backend port **:6060**, Vite dev port **:5173** — don't change these casually
- Panadapter amber is **`#FFA028`** (single-hue, alpha-varied, no rainbow gradients)
- Reference implementation is **Thetis**
- Deeper context for agents and contributors lives in `CLAUDE.md`, `docs/lessons/`,
  and `docs/rca/` — worth a skim before touching DSP, protocol, or layout code

## Distribution

Shipping surfaces are being added one at a time, slowly:

- **PWA (installable web app)** — available now. Precached shell, works
  offline for the static assets, installs from any browser that supports PWAs.
- **Photino desktop installers (macOS / Windows / Linux)** — planned.
  Native-window wrapper around the same web UI, shipped as real OS installers.
  Release cadence: TBD.
- **Mobile apps (iOS / Android) via Capacitor** — planned. Cadence: TBD.

## Requirements

- .NET 10 SDK
- Node.js 20+
- A Hermes Lite 2 on the local network
- macOS / Linux / Windows; WDSP native libraries are available for:
  - macOS: arm64, x64
  - Linux: x64, arm64
  - Windows: x64, arm64

## Troubleshooting

### Missing native WDSP library on Windows or Linux

If you see errors related to wisdom file generation or missing `wdsp.dll` / `libwdsp.so`:

1. **For Windows users**: The native DLLs are automatically built via GitHub Actions.
   - Check if `Zeus.Dsp/runtimes/win-x64/native/wdsp.dll` (or `win-arm64`) exists
   - If missing, trigger a build: Go to repository Actions → "Build Native WDSP Libraries" → "Run workflow"
   - Or build locally following instructions in `native/README.md`

2. **For Linux users**:
   - Check if `Zeus.Dsp/runtimes/linux-x64/native/libwdsp.so` (or `linux-arm64`) exists
   - If missing, run `./native/build.sh` from the repository root
   - Or wait for the automated GitHub Actions build

3. **Fallback to synthetic DSP**: If native libraries are unavailable, Zeus will fall back to a synthetic DSP engine. Most UI features work, but actual signal processing requires the WDSP native library.

See `native/README.md` for detailed build instructions and `docs/lessons/wdsp-init-gotchas.md` for WDSP-specific troubleshooting.

## Acknowledgements

Zeus stands on the shoulders of the OpenHPSDR community. A huge thank-you to:

- **Richard Samphire (MW0LGE)** and **Reid (MI0BOT)** — Thetis is an awesome
  starting point. Much of what Zeus knows about Protocol-1 framing, WDSP init
  ordering, meter pipelines, and TX behaviour was learned by reading the
  [Thetis source](https://github.com/TAPR/OpenHPSDR-Thetis). Zeus is an
  independent reimplementation in .NET — not a fork — but Thetis is the sole
  authoritative reference for how a Protocol-1 client should behave.
- **Warren Pratt (NR0V)** — for [WDSP](https://github.com/TAPR/OpenHPSDR-Thetis/tree/master/Project%20Files/Source/wdsp),
  the DSP engine Zeus loads via P/Invoke.

## License

Zeus is free software: you can redistribute it and/or modify it under the
terms of the **GNU General Public License v2 or (at your option) any later
version**, as published by the Free Software Foundation. See
[`LICENSE`](LICENSE) for the full text.

This licensing aligns Zeus with its two direct upstreams — Thetis (GPL v2)
and WDSP (GPL v3) — so that derivative work and linked distributions remain
licence-compatible.

Zeus is distributed WITHOUT ANY WARRANTY; see the GPL for details.

# openhpsdr-zeus

![Zeus](docs/pics/zeus1.webp)

A browser-based SDR console for the **Hermes Lite 2**. .NET 10 backend talks
OpenHPSDR Protocol-1 to the radio and streams IQ / audio / meter data to a
React + WebGL frontend over WebSocket.

> Status: early but working. HL2 only. RX is solid; TX is operator-verified on
> FM and TUNE (v0.1, April 2026). Other Protocol-1 radios (ANAN etc.) are not
> yet supported.

## About the name

The project started life as **openhpsdr-nereus**. Nereus is a nice bit of
lore — in Greek mythology he's the "Old Man of the Sea" and the father of
Thetis, which made him a fitting nod to the long-running
[Thetis](https://github.com/TAPR/OpenHPSDR-Thetis) project that a lot of the
DSP heritage traces back to.

The problem: somebody else was already shipping a C++ AetherSDR-flavoured
project called **nereusSDR**. Two HPSDR-adjacent projects sharing a name got
confusing fast, so we moved on.

Meet **Zeus** — king of the gods. It doesn't really get more regal than that.

## What's in the box

- **WebGL panadapter + waterfall** with zoom, click-to-tune, drag-pan gestures
- **DSP panel**: NB, NR, ANF, SNB, NBP — all driven by WDSP under the hood
- **Bands / modes / bandwidth / AGC / attenuator / preamp / drive / mic gain**
- **TX**: PTT, TUNE, mic uplink, TX stage meters, SWR-trip and TX-timeout
  protection
- **S-meter** (live + demo), RX meter frame streaming
- **Leaflet satellite map** with terminator and QRZ grid-square / beam heading
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
- macOS / Linux (Windows untested); WDSP native lib ships for osx-arm64

## License

TBD.

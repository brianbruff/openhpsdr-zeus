# OpenHPSDR Zeus — The King of SDRs

![Zeus](docs/pics/zeus1.webp)

A browser-based SDR console for the **Hermes Lite 2**. .NET 10 backend talks
OpenHPSDR Protocol-1 to the radio and streams IQ / audio / meter data to a
React + WebGL frontend over WebSocket.

> Status: early but working.
>
> - **Hermes Lite 2 (Protocol-1):** RX is solid; TX is operator-verified on FM
>   and TUNE (v0.1, April 2026).
> - **ANAN G2 (Protocol-2):** RX verified on OrionMkII / fw 2.7b41 across
>   80m–10m. TX wired for TUNE and MOX — on-air carrier verified clean via an
>   external KiwiSDR. 160m not yet wired.
> - Other Protocol-1 radios (older ANAN, Hermes, Angelia, etc.) are not yet
>   supported.

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
- **Radio discovery** on the LAN (Protocol-1 + Protocol-2 broadcast, in parallel)

## Download

Grab the latest installer from the **[Releases page](https://github.com/brianbruff/openhpsdr-zeus/releases/latest)**.

| Platform              | File                                | Notes                       |
| --------------------- | ----------------------------------- | --------------------------- |
| Windows (x64)         | `Zeus-X.Y.Z-win-x64-setup.exe`      | Inno Setup installer        |
| macOS (Apple Silicon) | `Zeus-X.Y.Z-macos-arm64.dmg`        | drag-to-install, see below  |
| Linux (x64)           | `zeus-X.Y.Z-linux-x64.tar.gz`       | extract and run `./zeus`    |

Zeus is also a Progressive Web App. If you'd rather not install a desktop
package, run `Zeus.Server` from source and use your browser's **Install** icon
on `http://localhost:6060` to add Zeus to the dock / start menu directly.

### macOS — Removing Gatekeeper Warning

Zeus is not signed with an Apple Developer certificate, so macOS will block it
on first launch. To fix this, open Terminal and run:

```bash
xattr -cr /Applications/Zeus.app
```

If you still see a security warning, go to **System Settings → Privacy &
Security** and click **Open Anyway**.

## Layout

| Path                     | What it is                                          |
| ------------------------ | --------------------------------------------------- |
| `Zeus.Server/`           | ASP.NET Core host, SignalR hub, radio service       |
| `Zeus.Protocol1/`        | OpenHPSDR Protocol-1 client, framing, discovery     |
| `Zeus.Protocol2/`        | OpenHPSDR Protocol-2 client (ANAN G2)               |
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

### First run — wait for WDSP wisdom before connecting

The first time you start `Zeus.Server` on a machine, WDSP/FFTW runs a
one-shot "wisdom" pass to plan the FFT sizes Zeus uses. It takes **1–3
minutes** on a modern CPU, and during that time you'll see output like:

```
info: Zeus.Dsp.Wdsp.WdspWisdomInitializer[0]
      wdsp.wisdom initialising dir=/home/<user>/.local/share/Zeus/
Optimizing FFT sizes through 262145

Please do not close this window until wisdom plans are completed.

Planning COMPLEX FORWARD  FFT size 64
Planning COMPLEX BACKWARD FFT size 64
... (many lines) ...
```

**Don't click Discover or Connect in the web UI until you see:**

```
info: Zeus.Dsp.Wdsp.WdspWisdomInitializer[0]
      wdsp.wisdom ready result=1 (built) status=...
```

Connecting to a radio while wisdom is still building will crash the backend
with a native double-free abort. Once wisdom is cached
(`~/.local/share/Zeus/wdspWisdom00` on Linux, `%LOCALAPPDATA%\Zeus\wdspWisdom00`
on Windows), subsequent starts log `result=0 (loaded)` and come up instantly.

## PA settings (power calibration)

Zeus maps the drive / tune slider to an on-air wattage using a two-input
formula lifted from Thetis and pihpsdr:

1. **Rated PA Output (W)** — the amplifier's rated max power at full drive.
2. **PA Gain (dB)**, per band — the amplifier's forward gain from DUC output
   to the antenna (not a trim; the amplifier's physical gain).

Slider 100 % targets the rated wattage; slider 50 % targets half that wattage.
The per-band gain corrects for the fact that different HF bands need different
drive bytes to produce the same on-air watts.

### Defaults

Fresh installs now seed the Rated PA Output per board class:

| Radio class                        | Rated PA Output (default) |
| ---------------------------------- | ------------------------- |
| Hermes Lite 2                      |    5 W                    |
| Hermes / Metis / Griffin / ANAN-10 |   10 W                    |
| ANAN-100 / 100B / 8000D (Angelia)  |  100 W                    |
| ANAN-100D / 200D (Orion)           |  100 W                    |
| ANAN-7000D / G1 / G2 (OrionMkII)   |  100 W                    |

Per-band PA Gain defaults are seeded from Thetis's per-board calibration
tables (see `Zeus.Server/PaDefaults.cs`).

> **Upgrading from an older Zeus install?** The per-board default only
> applies on first run — if you've used Zeus before the `pa_globals` record
> is already stored with `PaMaxPowerWatts = 0`, which silently forces the
> legacy linear mode (PA Gain is ignored). **Open the Settings menu → PA
> tab, set `Rated PA Output (W)` to 100 for a G2 / ANAN or 10 for a Hermes,
> and click Apply.** Or delete `~/.local/share/Zeus/zeus-prefs.db` to start
> fresh and pick up the new defaults automatically.

### Applying changes

**PA settings require an explicit Apply click to persist.** After editing
any value in the Settings menu's PA tab, click the **Apply** button at the
bottom of the tab — changes only take effect on the radio at that moment.
Closing the Settings modal without clicking Apply discards pending edits.

## Known quirks

- **TUN carrier looks momentarily wide / pulses for ~1 s on the Zeus
  panadapter when you first key it.** This is a Zeus display artifact —
  the actual RF transmitted is a clean zero-beat single-tone carrier,
  verified via an external receiver (KiwiSDR). To be cleaned up at a
  later date.

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
changes under `Zeus.Server/`, `Zeus.Protocol1/`, `Zeus.Protocol2/`, `Zeus.Dsp/`,
or `Zeus.Contracts/` require restarting the backend.

> On the very first backend start, wait for `wdsp.wisdom ready` before clicking
> Discover — see [First run](#first-run--wait-for-wdsp-wisdom-before-connecting)
> above.

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
- **Native installers (Windows `.exe`, macOS `.dmg`, Linux `.tar.gz`)** —
  available now. Self-contained .NET 10 publish + WDSP native libraries.
  See the [Download](#download) section above.
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

Zeus stands on the shoulders of the OpenHPSDR community. Most of what Zeus
knows about Protocol-1 framing, Protocol-2 client behaviour, WDSP init
ordering, meter pipelines, and TX safety was learned by reading the
[Thetis source](https://github.com/ramdor/Thetis). Zeus is an independent
reimplementation in .NET — not a fork — but Thetis is the authoritative
reference for how an OpenHPSDR client should behave, and it continues a
GPL-governed lineage that runs from FlexRadio PowerSDR through the
OpenHPSDR (TAPR) ecosystem to Thetis itself.

Zeus gratefully acknowledges the Thetis contributors:

- **Richard Samphire** (MW0LGE)
- **Warren Pratt** (NR0V) — also author of **WDSP**, the DSP engine Zeus
  loads via P/Invoke
- **Laurence Barker** (G8NJJ)
- **Rick Koch** (N1GP)
- **Bryan Rambo** (W4WMT)
- **Chris Codella** (W2PA)
- **Doug Wigley** (W5WC)
- **Richard Allen** (W5SD)
- **Joe Torrey** (WD5Y)
- **Andrew Mansfield** (M0YGG)
- **Reid Campbell** (MI0BOT)
- **FlexRadio Systems**

Zeus contributors to date: **Brian Keating (EI6LF)** — project lead, and
**Douglas J. Cerrato (KB2UKA)**.

See [`ATTRIBUTIONS.md`](ATTRIBUTIONS.md) for the full provenance statement,
per-component licensing, and the per-file header convention Zeus uses to
carry this acknowledgement through every source file.

## License

Zeus is free software: you can redistribute it and/or modify it under the
terms of the **GNU General Public License v2 or (at your option) any later
version**, as published by the Free Software Foundation. See
[`LICENSE`](LICENSE) for the full text and [`ATTRIBUTIONS.md`](ATTRIBUTIONS.md)
for the full provenance statement.

This licensing aligns Zeus with its direct upstreams — Thetis (GPL v2+) and
WDSP (GPL v2+, by NR0V) — so that the derivation chain and any linked
distributions remain licence-compatible.

Zeus is distributed WITHOUT ANY WARRANTY; see the GPL for details.

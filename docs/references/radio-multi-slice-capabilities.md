# Radio Multi-Slice Capabilities

## Overview

This document describes the multi-receiver (multi-slice) capabilities of HPSDR radios supported by Zeus. A "slice" represents an independent receiver chain with its own DDC (Digital Down Converter), allowing simultaneous monitoring of multiple frequencies.

## Protocol 1 Radios (Original Protocol)

Protocol 1 supports **multiple DDCs** on boards that advertise it — the
host writes the DDC count into C4 bits [5:3] of the C0=0x00 command word
(see HL2 reference below). The number of independent slices is set by the
board's gateware, not by the protocol itself, so per-board limits vary.

| Board | Max Receivers | Notes |
|-------|--------------|-------|
| Hermes / ANAN-10 / 10E / 100 / 100B | 1 | Single DDC (`HermesClass` fingerprint) |
| Mercury/Penelope/Metis | 1 | Single DDC |
| Griffin | 1 | Single DDC |
| ANAN-100D (Angelia) | 2 | Dual ADC, two DDCs in P1 mode |
| ANAN-200D (Orion) | 2 | Dual ADC, two DDCs in P1 mode |
| HermesLite 2 (HL2) | 4 | Up to 4 DDCs via C4 bits [5:3] — see note |
| ANAN-G2E (HermesC10) | 1 | Single-RX hardware |
| Apache OrionMkII original | 2 | Saturn-class hardware, treated conservatively |
| Saturn-class 0x0A family (G2 / G2-1K / 7000DLE / 8000DLE / ANVELINA-PRO3 / Red Pitaya) | 8 | Up to 8 DDCs |

**HL2 multi-DDC note:** HL2 hardware has a single ADC, but the HL2
gateware multiplexes that ADC into up to 4 independent DDC slices. The
host selects the slice count by writing `(nddc - 1)` into C4 bits [5:3]
of the C0=0x00 command word. References:

- `docs/references/protocol-1/hermes-lite2-protocol.md:478-485` —
  authoritative description of C0=0x00 / C4[5:3]; bare HL2 RX uses 1
  receiver (Zeus default), HL2 PureSignal in the 2-DDC layout requires 2,
  4-DDC layout uses 4. Conservative default is 4 (the documented ceiling
  in the protocol reference).
- `docs/references/protocol-1/supported-settings.md:37` — mentions "up to
  12, HL2 gateware-dependent"; not all gateware variants honour that, so
  4 is the safer cross-version cap.
- mi0bot Thetis `networkproto1.c:973` — host write loop:
  `C4 |= (nddc - 1) << 3;`. This is the reference C implementation.

Earlier revisions of this document claimed HL2 was single-receiver only;
that was wrong and has been corrected. HL2 is a Protocol-1 board that
supports multi-DDC.

## Protocol 2 Radios (New Protocol / ETH)

Protocol 2 radios support multiple DDC channels. The actual number is board-specific and reported in the discovery reply `NumReceivers` field.

| Board | Max Receivers | DDC Notes | PureSignal Impact |
|-------|--------------|-----------|-------------------|
| ANAN-10 | 1 | Single DDC | DDC0 reserved for PS |
| ANAN-100 | 1-2 | Check firmware | DDC0/1 for PS when enabled |
| ANAN-100D | 2 | 2 DDCs | DDC0/1 for PS, user RX on DDC2+ |
| ANAN-200D | 4 | 4 DDCs | DDC0/1 for PS, user RX on DDC2-5 |
| ANAN-7000DLE | 4 | 4 DDCs | DDC0/1 for PS, user RX on DDC2-5 |
| ANAN-8000DLE | 8 | 8 DDCs | DDC0/1 for PS, user RX on DDC2-9 |
| Orion Mk II (G2) | 8 | 8 DDCs | DDC0/1 for PS, user RX on DDC2-9 |
| Saturn | 8 | 8 DDCs | DDC0/1 for PS, user RX on DDC2-9 |

### Protocol 2 DDC Allocation

On G2/Saturn/Orion-II class radios:
- **DDC0** and **DDC1** are reserved for PureSignal feedback (TX forward path and RX coupled loopback)
- **DDC2** through **DDC{N-1}** are available for user receivers
- Each DDC streams IQ on UDP port `1035 + ddc_index`
- Example: DDC2 → port 1037, DDC3 → port 1038, etc.

**Effective user receivers = NumReceivers - 2** when PureSignal is supported.

## Discovery API

The `NumReceivers` field is populated from the Protocol 2 discovery reply:
- **Offset:** Byte 20 of discovery reply
- **Range:** 1-8 (board-dependent)
- **Usage:** Reported in `/api/radio/discovered` endpoint
- **Reference:** `Zeus.Protocol2/Discovery/DiscoveredRadio.cs:63`

## Current Zeus Implementation

**Phase 1 (shipped on `claude/support-multiple-panadapters`, 2026-05-06):**

- **HL2 (Protocol 1):** multi-RX wired end-to-end. Default OFF;
  `POST /api/multi-slice` `{ Enabled: true, NumActiveSlices: N }` opens
  N DDCs (1..4) with the gateware bits at C0=0x00 / C4 [5:3]. Per-slice
  VFO via `POST /api/vfo` `{ Hz, RxId }`. Per-RxId panadapter / waterfall
  panels (`hero-rx1`..`hero-rx3`) appear in the Add Panel modal once
  multi-slice is enabled. Audio + TX stay on `RxId=0`.
- **PS-precedence:** if PureSignal is armed, multi-slice requests are
  refused (see `RadioService.SetMultiSlice`). PS+multi-RX coexistence is
  Phase 2.
- **Single-slice path is bit-identical** to v0.6.x — operators who don't
  toggle multi-slice on see no behavioural change.
- **Protocol 2 (Saturn-class G2 / G2-MkII / 7000DLE / 8000DLE) — NOT YET.**
  The capability table reports `MaxReceivers=8` for these boards and the
  P2 client opens DDC2 only. P2 multi-DDC is deferred to Phase 2.
- **Frontend:** `zeus-web/src/state/display-store.ts` is a per-RxId map;
  `Panadapter` / `Waterfall` / `FreqAxis` / `PassbandOverlay` accept an
  `rxId` config; `RadioSelector` exposes a multi-slice control on capable
  boards (currently HL2 only).

**Phase 2 (deferred, future PR):**

- Per-slice DSP / AGC / mode / filter state.
- Audio mixing across slices (RxId=0 audio only today).
- PS+multi-RX coexistence — the 4-DDC HL2 layout with 2 user RX + 2 PS
  feedback DDCs. The wire layer carries it; the policy gate in
  `SetMultiSlice` is the seam.
- Per-slice VFO optimistic-UI dial-marker on the FreqAxis (the wire is
  correct; only the dial visualisation lags one update).
- Protocol-2 multi-DDC for Saturn-class boards.

## References

- **Protocol 2 specification:** `docs/references/protocol-2/` (not yet in repo, see Thetis ChannelMaster)
- **pihpsdr reference:** `new_protocol_receive_specific` and `new_protocol_high_priority` in `new_protocol.c`
- **Thetis reference:** `ChannelMaster.cs` network setup and DDC management
- **Zeus Protocol2Client:** `Zeus.Protocol2/Protocol2Client.cs:65-71` (G2RxDdc constant)
- **Zeus Discovery:** `Zeus.Protocol2/Discovery/DiscoveredRadio.cs`

## Implementation Considerations

When adding multi-slice support:
1. **Check NumReceivers at connection time** to determine how many receivers to open
2. **Open N-2 WDSP RXA channels** (accounting for PS DDC reservation on P2)
3. **Broadcast per-RxId DisplayFrame** for each active slice
4. **Frontend: per-RxId display-store** (map RxId → { panDb, wfDb, centerHz, ... })
5. **Flex layout: allow multiple panadapter panels** with RxId prop
6. **Settings: multi-slice enable toggle** (default OFF for backward compat)
7. **VFO management: per-receiver VFO** (RxId 0 remains primary/master)

Default single-panadapter experience must remain unchanged for existing users.

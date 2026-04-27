# Radio Multi-Slice Capabilities

## Overview

This document describes the multi-receiver (multi-slice) capabilities of HPSDR radios supported by Zeus. A "slice" represents an independent receiver chain with its own DDC (Digital Down Converter), allowing simultaneous monitoring of multiple frequencies.

## Protocol 1 Radios (Original Protocol)

Protocol 1 radios use a single DDC and do not support true multi-slice operation in the protocol itself.

| Board | Max Receivers | Notes |
|-------|--------------|-------|
| Hermes | 1 | Single DDC at RX sample rate |
| Mercury/Penelope/Metis | 1 | Single DDC |
| Griffin | 1 | Single DDC |
| Angelia | 1 | Protocol 1 limitation |
| HermesLite 2 (HL2) | 1 | Protocol 1 only; hardware has 1 ADC |
| Orion | 1 | Protocol 1 mode, single DDC |

**Note:** HL2 firmware does NOT support multiple receivers. Despite some confusion in the issue, HL2 has a single ADC and runs Protocol 1 exclusively. The "2 slices" mentioned in the issue appears to be a misunderstanding—HL2 is a single-receiver board.

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

## Current Zeus Implementation (v1.0)

As of this writing, Zeus supports **single-receiver operation only**:
- `DspPipelineService` maintains one `_channelId` (int, not array)
- `DisplayFrame.RxId` field exists but is hardcoded to `0`
- Frontend `useDisplayStore` has single `panDb`/`wfDb` pair
- Only DDC2 is opened on Protocol 2 radios
- No UI for enabling/configuring additional slices

This document supports the multi-panadapter feature implementation (issue #XX) which will extend Zeus to support multiple simultaneous receivers on capable hardware.

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

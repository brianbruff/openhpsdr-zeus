# TCI (Transceiver Control Interface) Server

Zeus implements an **ExpertSDR3-compatible TCI server** for remote control and streaming via WebSocket. TCI is spoken by amateur radio logging and digital-mode applications.

## Supported Clients

- **Loggers:** Log4OM, N1MM+ (via TCI bridge)
- **Digital modes:** JTDX, WSJT-X (via TCI bridge), FT8/FT4 decoders
- **CW skimmers:** Morse decoder tools that support TCI
- **SDR display tools:** Third-party spectrum analyzers and remote consoles

## Configuration

TCI is **disabled by default** for security. Enable it in `appsettings.json`:

```json
{
  "Tci": {
    "Enabled": true,
    "BindAddress": "127.0.0.1",
    "Port": 40001
  }
}
```

### Options

| Option | Default | Description |
|--------|---------|-------------|
| `Enabled` | `false` | Enable the TCI server |
| `BindAddress` | `"127.0.0.1"` | Bind address (localhost only by default) |
| `Port` | `40001` | TCP port (ExpertSDR3 standard) |
| `RateLimitMs` | `50` | VFO event coalescing interval (ms) |
| `SendInitialStateOnConnect` | `true` | Send current state after handshake |
| `CwBecomesCwuAbove10MHz` | `false` | Legacy CW mode mapping |
| `LimitPowerLevels` | `false` | Clamp drive to 50%, tune to 25% |

**Security Note:** TCI has no authentication. Only bind to `0.0.0.0` on trusted networks.

## Connection

Connect your TCI client to:

```
ws://127.0.0.1:40001/
```

The server sends a handshake message immediately after the WebSocket upgrade, ending with `ready;`. All commands are ASCII text frames, semicolon-terminated:

```
command:arg1,arg2,...;
```

## Supported Commands (Phase 1 + Phase 2)

### Frequency Control

- `vfo:<rx>,<chan>,<hz>` — Set/query VFO frequency
- `dds:<rx>,<hz>` — Set/query DDS center frequency
- `if:<rx>,<chan>,<offset>` — Set/query IF offset (always zero)

### Mode & Filter

- `modulation:<rx>,<MODE>` — Set/query mode (AM, SAM, DSB, LSB, USB, FM, CWL, CWU, DIGL, DIGU)
- `rx_filter_band:<rx>,<lo_hz>,<hi_hz>` — Set/query RX filter bandwidth

### TX Control

- `trx:<rx>,<bool>` — MOX on/off
- `tune:<rx>,<bool>` — Internal tune carrier on/off
- `drive:<rx>,<0-100>` — TX drive percent
- `tune_drive:<rx>,<0-100>` — Tune power percent

### AGC (Phase 2)

- `agc_gain:<rx>,<db>` — Set/query AGC gain (synonymous with AGC top, range -20 to 120 dB)

### CW Keyer / Macros (Phase 2 — ack-only stubs)

The following commands are accepted by the dispatcher but currently no-op (logged at debug). Zeus has no CW keyer engine yet; these are present so loggers and contest tools that probe them on connect do not see protocol errors.

- `cw_macros_speed:<wpm>` — Keyer/macro speed
- `cw_macros:<slot>,<text>` — Store macro text in slot
- `cw_msg:<text>` — Send arbitrary text as CW
- `keyer:<bool>` — Enable/disable internal keyer

### Audio

- `mute:<bool>` — Master mute (stub)
- `rx_mute:<rx>,<bool>` — Per-RX mute (stub)
- `volume:<db>` — Master volume (stub)
- `mon_enable:<bool>` — Sidetone enable (stub)
- `mon_volume:<db>` — Sidetone volume (stub)

### Lifecycle

- `start` — Power on radio (requires REST API connection first)
- `stop` — Power off radio

### DX Cluster Spots

- `spot:<callsign>,<mode>,<freq_hz>,<argb>[,<comment>]` — Add spot
- `spot_delete:<callsign>` — Remove spot
- `spot_clear` — Clear all spots

**Note:** Spots are stored but not rendered on the panadapter in this release.

### Binary Streams (Phase 3)

- `iq_start:<rx>,<bool>` — Subscribe (true) / unsubscribe (false) to RX IQ binary stream for the given receiver
- `iq_stop:<rx>` — Alias for `iq_start:<rx>,false`
- `iq_samplerate:<hz>` — Set/query requested IQ sample rate (clamped to 48000–384000)

The actual rate of published frames is the radio's native IQ sample rate (set via the protocol layer); the server echoes the clamped requested rate so the client knows what it will receive. Streams emit WebSocket binary frames with the 64-byte TCI header described below.

#### Binary frame layout (64-byte header + samples)

All header fields are little-endian uint32. Layout matches Thetis `buildStreamPayload`:

| Offset | Field | IQ value |
|---|---|---|
| 0..3 | receiver index | `0` |
| 4..7 | sample rate (Hz) | radio native (48k/96k/192k/384k) |
| 8..11 | sample type | `3` = FLOAT32 |
| 12..19 | reserved | 0 |
| 20..23 | length | float-value count = `complex_samples * 2` |
| 24..27 | stream type | `0` = IQ_STREAM |
| 28..31 | channels | `2` (I, Q interleaved) |
| 32..63 | reserved | 0 |
| 64.. | payload | FLOAT32 little-endian, interleaved I, Q, I, Q, … |

## Events (Server → Client)

The server broadcasts these events to all connected clients when radio state changes:

### Frequency & Mode Events

- `vfo:...` — VFO frequency changed (rate-limited)
- `dds:...` — DDS center changed (rate-limited)
- `modulation:...` — Mode changed
- `rx_filter_band:...` — Filter bandwidth changed
- `tx_frequency:<hz>` — TX frequency (derived from VFO)
- `if_limits:...` — IF limits (on sample rate change)

### TX Control Events

- `start` — Radio connected
- `stop` — Radio disconnected

### Meter Events (Phase 2)

- `rx_smeter:<rx>,<chan>,<dbm>` — RX S-meter reading in dBm (rate-limited, approximately 5 Hz)
- `tx_power:<watts>` — TX forward power in watts (approximately 10 Hz during MOX)
- `tx_swr:<ratio>` — SWR ratio as decimal (e.g., "1.5" for 1.5:1)
- `tx_alc:<percent>` — ALC gain reduction as percentage (0-100)

## Rate Limiting

VFO/DDS changes during tuning can fire hundreds of events per second. The server coalesces rapid updates and broadcasts at most once per `RateLimitMs` (default 50 ms = 20 Hz) to avoid flooding clients.

## Examples

### Connect and Query VFO

```
# Client → Server
vfo:0,0;

# Server → Client
vfo:0,0,14074000;
```

### Set Mode to USB

```
# Client → Server
modulation:0,USB;

# (No immediate response; StateChanged event broadcasts to all clients)
# Server → All Clients
modulation:0,USB;
```

### Enable MOX

```
# Client → Server
trx:0,true;

# Server → All Clients
trx:0,true;
tx_enable:0,true;
```

## Future Phases

**Phase 2 — Digital Mode Support** ✅ (Partially Complete)
- ✅ AGC gain commands
- ✅ S-meter event broadcasting
- ✅ TX-meter event broadcasts (power, SWR, ALC)
- 🟡 CW message / keyer commands (ack-only stubs; functional impl deferred pending CW engine)

**Phase 3 — Binary Streams** 🟡 (Partially Complete)
- ✅ IQ streaming (`iq_start`, `iq_stop`, `iq_samplerate`) — single receiver, FLOAT32, native radio rate
- ✅ Outbound priority queues (Urgent / Binary / Control) mirroring Thetis architecture
- ⏸️ Audio streaming (`audio_start`, `audio_stop`, `audio_samplerate`)
- ⏸️ Multi-receiver IQ when Zeus gains Diversity / dual-RX support
- ⏸️ Conformance test against a real third-party client (Log4OM / SMP)

**Phase 4 — Polish**
- Noise reduction commands (NB, NR, ANF, ANC)
- Preamp / attenuator commands
- Spot rendering on panadapter
- REST API for TCI status/control

## Protocol Reference

- **ExpertSDR3 TCI v1.8:** https://github.com/ExpertSDR3/TCI
- **Thetis Implementation:** https://github.com/mi0bot/OpenHPSDR-Thetis (see `TCIServer.cs`)

## Troubleshooting

**Client can't connect:**
- Check `Tci:Enabled=true` in `appsettings.json`
- Verify port 40001 is not blocked by firewall
- Check server logs for `tci.listening` message

**VFO changes not updating:**
- Rate limiting is working as intended (50 ms default)
- Increase `RateLimitMs` for slower updates

**Commands ignored:**
- Ensure semicolon termination: `command:args;`
- Commands are case-sensitive (lowercase)
- Check server logs for parse errors

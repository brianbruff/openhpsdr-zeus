# PureSignal Implementation for Protocol 1 (Hermes Lite 2)

## Summary

This document describes the PureSignal (predistortion) implementation for Protocol 1 radios, specifically the Hermes Lite 2. This feature extends the existing Protocol 2 PureSignal support (developed by KB2UKA for ANAN G2) to work with Hermes Lite radios.

## Background

PureSignal is an adaptive digital predistortion system that linearizes transmit signals by:
1. Sampling the actual transmitted signal via a feedback coupler
2. Comparing it with the intended signal
3. Computing and applying correction coefficients using WDSP's `psccF` algorithm

Protocol 2 radios (ANAN G2 MkII, Orion MkII) achieve this using dedicated dual-DDC hardware channels:
- **DDC0**: Post-PA feedback coupler IQ
- **DDC1**: TX-DAC loopback IQ

Hermes Lite 2 uses a different architecture with register-based control and feedback integrated into the main RX stream.

## Implementation Overview

### Protocol Level Changes

#### 1. Control Registers (Zeus.Protocol1/ControlFrame.cs)

Added two new HL2-specific register types:

**PsEnableLna (0x0a)**: PureSignal enable and LNA configuration
- Bit 22 (C3[6]): PureSignal enable (0=disable, 1=enable)
- Bits 5-0: LNA gain value
- Bit 6: Extended LNA range selector

**Predistortion (0x2b)**: Predistortion subindex and configuration
- Bits 31-24 (C1): Predistortion subindex
- Bits 19-16 (C2[7:4]): Predistortion value

#### 2. Register Rotation

Extended the TX packet register rotation from 4-phase to 8-phase when PureSignal is enabled:

**4-phase (PS disabled)**:
```
Phase 0: Config/TxFreq, RxFreq
Phase 1: RxFreq/TxFreq, DriveFilter
Phase 2: Attenuator, RxFreq/TxFreq
Phase 3: RxFreq/TxFreq, Config
```

**8-phase (PS enabled)**:
```
Phase 0: Config/TxFreq, RxFreq
Phase 1: RxFreq/TxFreq, DriveFilter
Phase 2: Attenuator, RxFreq/TxFreq
Phase 3: RxFreq/TxFreq, Config
Phase 4: PsEnableLna, RxFreq/TxFreq
Phase 5: RxFreq/TxFreq, Predistortion
Phase 6: Attenuator, RxFreq/TxFreq
Phase 7: RxFreq/TxFreq, RxFreq
```

This ensures PureSignal registers are sent regularly while maintaining the critical TxFreq updates needed for duplex operation.

### Client State Management (Zeus.Protocol1/Protocol1Client.cs)

Added three atomic state fields:
- `_psEnabled`: PureSignal master enable (0/1)
- `_psSubindex`: Predistortion subindex (0..255)
- `_psPredistortion`: Predistortion value (0..15)

Public API methods:
```csharp
void SetPsEnabled(bool enabled)
void SetPsPredistortion(byte subindex, byte value)
```

### Feedback Path

#### Architecture

Created a shared `PsFeedbackFrame` structure in `Zeus.Contracts`:

```csharp
public readonly record struct PsFeedbackFrame(
    float[] TxI,    // TX modulator IQ (1024 samples)
    float[] TxQ,
    float[] RxI,    // RX feedback coupler IQ (1024 samples)
    float[] RxQ,
    ulong SeqHint);  // Diagnostic sequence number
```

This structure is shared between Protocol 1 and Protocol 2, with protocol-specific extraction mechanisms.

#### Protocol1Client Integration

Added PureSignal feedback channel:
```csharp
private readonly Channel<PsFeedbackFrame> _psFeedbackChannel;
public ChannelReader<PsFeedbackFrame> PsFeedbackFrames { get; }
```

**IMPORTANT**: The exact mechanism by which Hermes Lite 2 provides feedback IQ samples when PureSignal is enabled requires hardware verification. Based on research:

1. **HL2 gateware**: When register 0x0a[22] is set, feedback ADC data is included in the RX stream
2. **Possible mechanisms**:
   - Time-multiplexed: Alternating between main RX and feedback samples
   - Frequency-multiplexed: Feedback on a separate channel or frequency offset
   - Protocol-1 extension: Additional packet format with feedback data

The current implementation provides the infrastructure (channels, state management, register writes) but feedback extraction from the RX stream needs to be verified with actual HL2 hardware.

### UI Changes (zeus-web/src/components/PsToggleButton.tsx)

Removed the Protocol 1 gate that disabled PureSignal UI:

**Before**:
```typescript
const p1Disabled = protocol === 'P1';
const disabled = !connected || p1Disabled;
const tooltip = p1Disabled
  ? 'PureSignal for Hermes coming in a follow-up'
  : ...;
```

**After**:
```typescript
const disabled = !connected;
const tooltip = psEnabled
  ? 'PureSignal armed — predistortion active'
  : 'Arm PureSignal predistortion';
```

PureSignal button is now enabled for both Protocol 1 and Protocol 2 connections.

## Hardware Support

### Hermes Lite 2 Requirements

1. **Gateware version**: Requires HL2 gateware that implements:
   - Register 0x0a[22] PureSignal enable bit
   - Register 0x2b predistortion configuration
   - Feedback IQ injection into RX stream when PS enabled

2. **Hardware**: PA feedback coupler must be present and properly calibrated

3. **PA Settings**: Hardware peak resolution for HL2 is **0.233** (from Thetis reference)

### External Amplifier Support

For external amplifiers with HL2:

1. **Feedback source selection**: Use `PsSettingsStore.Source` (Internal/External)
2. **ALEX bypass**: When External + PS armed + MOX, set ALEX bypass bit to tap external antenna
3. **Step attenuator**: TX step attenuator can be controlled via auto-attenuate loop
4. **Delay compensation**: Use `PsAdvanced.AmpDelayNs` to compensate for external amp delays

**Note**: ALEX bypass bit handling for Protocol 1 external amplifier support is not yet implemented and requires additional work in the wire-format layer.

## Testing Strategy

### Unit Tests

Updated existing Protocol1 tests:
- `ControlFrameTests.PhaseTable_*`: Now pass `psEnabled` parameter
- All tests pass with PS disabled (backward compatibility)
- New tests needed for PS-enabled register rotation

### Integration Testing Requirements

**Before deploying to production**, the following must be verified with actual HL2 hardware:

1. **Register writes**:
   ```
   dotnet run --project Zeus.Server
   # Connect to HL2
   # Enable PureSignal via UI
   # Verify radio acknowledges 0x0a[22]=1
   ```

2. **Feedback reception**:
   ```
   # With PS armed and MOX on
   # Monitor Protocol1Client.PsFeedbackFrames channel
   # Verify feedback IQ samples arrive
   # Check sample rate, format, and timing
   ```

3. **WDSP integration**:
   ```
   # With feedback flowing
   # Verify DspPipelineService routes to WDSP psccF
   # Check calibration state progression
   # Verify correction is applied to TX
   ```

4. **Two-tone testing**:
   ```
   # Generate two-tone test signal (700Hz + 1900Hz)
   # Enable PS and observe IMD reduction
   # Measure correction depth via spectrum analyzer
   ```

## Known Limitations

1. **Feedback extraction**: Not implemented - requires HL2 hardware testing
2. **External amplifier ALEX**: Bypass bit handling not wired up for Protocol 1
3. **Predistortion subindex**: Usage not documented - may be HL2 gateware version-specific
4. **DspPipelineService**: Protocol 1 feedback pump not yet connected

## Files Modified

### Core Protocol
- `Zeus.Protocol1/ControlFrame.cs` - Register definitions and payload writers
- `Zeus.Protocol1/Protocol1Client.cs` - State management and feedback channel

### Contracts
- `Zeus.Contracts/PsFeedbackFrame.cs` - Shared feedback structure (moved from Protocol2)

### Protocol 2 (compatibility)
- `Zeus.Protocol2/Protocol2Client.cs` - Added Zeus.Contracts using
- `Zeus.Protocol2/PsFeedbackFrame.cs` - Now a type-forward shim

### UI
- `zeus-web/src/components/PsToggleButton.tsx` - Removed Protocol 1 gate

### Tests
- `tests/Zeus.Protocol1.Tests/ControlFrameTests.cs` - Updated for new PhaseRegisters signature

## Next Steps

1. **Hardware verification session**:
   - Connect to HL2 with gateware supporting PS
   - Enable PS via UI and monitor wire traffic
   - Document actual feedback mechanism
   - Implement RX stream parser for feedback IQ

2. **DspPipelineService integration**:
   - Add Protocol1Client feedback pump (similar to Protocol2)
   - Wire PsFeedbackFrames → WDSP psccF
   - Test calibration convergence

3. **External amplifier**:
   - Implement ALEX bypass bit for Protocol 1
   - Add TX step attenuator control path
   - Test with external PA

4. **Documentation**:
   - Update user guide with HL2 PureSignal setup
   - Document any gateware version requirements
   - Create troubleshooting guide

## References

- **Protocol documentation**: `docs/references/protocol-1/hermes-lite2-protocol.md`
- **Supported settings matrix**: `docs/references/supported-settings.md`
- **HL2 protocol extensions**: Lines 60, 76-77, 108 in hermes-lite2-protocol.md
- **Protocol 2 reference**: `Zeus.Protocol2/Protocol2Client.cs` lines 143-170 (DDC feedback)
- **WDSP integration**: `Zeus.Dsp/Wdsp/WdspDspEngine.cs` lines 1335-1559

## Contributors

- **Douglas J. Cerrato (KB2UKA)**: Original PureSignal implementation for Protocol 2 / ANAN G2
- **Protocol 1 extension**: Based on KB2UKA's architecture, adapted for Hermes Lite 2

## License

GPL-2.0-or-later (same as Zeus project)

namespace Zeus.Contracts;

public enum MsgType : byte
{
    // Server → client (RX display + audio)
    DisplayFrame = 0x01,
    AudioPcm = 0x02,
    Status = 0x03,

    // Client → server (TX uplink). f32le mono samples at 48 kHz, framed into
    // 960-sample (20 ms) blocks. 0x20 chosen to live in a "2x = uplink" nibble
    // so future client→server types (PTT heartbeats, etc.) cluster together
    // and stay visually distinct from the 0x1x server→client telemetry.
    MicPcm = 0x20,

    // Server → client (TX telemetry + protection)
    TxMeters = 0x11,
    TxStatus = 0x12,
    Alert = 0x13,

    // Server → client (RX signal strength, dBm)
    RxMeter = 0x14,

    // Server → client (DSP bootstrap state). Broadcast when the WDSPwisdom
    // FFTW plan cache transitions between idle/building/ready; also pushed
    // once per client at WS attach so late joiners get the current state.
    WisdomStatus = 0x15,

    // Server → client (TX telemetry v2). Compatible additive extension of
    // TxMeters (0x11): carries average readings alongside peak for every
    // stage, plus CFC/COMP stages that v1 omitted. Operators need the
    // average to judge level and the peak to catch clipping; v1's peak-only
    // payload hid transient overshoots inside the smoothing window. v1 is
    // left in the enum for decoder interop / historical clients but the
    // server only broadcasts v2 after the feat/tx-audio-meters branch.
    TxMetersV2 = 0x16,

    // Server → client (HL2 PA temperature in °C, MCP9700 sensor). Separate
    // from the TX meter frame because temperature is a protection signal
    // the operator wants to see during RX-only operation too — the HL2
    // gateware auto-disables TX at 55 °C (Q6 sensor) — and it moves on a
    // seconds timescale, so bolting it onto the 10 Hz TX meter cadence
    // would be overkill. Broadcast at 2 Hz always.
    PaTemp = 0x17,
}

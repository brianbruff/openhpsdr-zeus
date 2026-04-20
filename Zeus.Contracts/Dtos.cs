namespace Zeus.Contracts;

public enum RxMode : byte
{
    LSB, USB, CWL, CWU, AM, FM, SAM, DSB, DIGL, DIGU,
}

public enum ConnectionStatus { Disconnected, Connecting, Connected, Error }

// Thetis NR-button state: Off = no spectral NR, Anr = NR1 (time-domain LMS),
// Emnr = NR2 (Ephraim–Malah spectral). ANR and EMNR are mutually exclusive
// in Thetis, so the button carries both in one enum.
public enum NrMode : byte { Off, Anr, Emnr }

// Pre-RXA time-domain blanker. Nb1 = ANB (noise blanker), Nb2 = NOB (noise gate).
// Engine silently ignores this until the pre-RXA pipeline lands (task #4);
// kept in the contract so the UI shape doesn't churn when it does.
public enum NbMode : byte { Off, Nb1, Nb2 }

// Thetis default NbThreshold = 3.3 (WDSP units), which is `0.165 × 20` — the
// Thetis UI slider sitting at 20. Kept here so REST round-trips preserve the
// UI-space value rather than the scaled one.
public sealed record NrConfig(
    NrMode NrMode = NrMode.Off,
    bool AnfEnabled = false,
    bool SnbEnabled = false,
    bool NbpNotchesEnabled = false,
    NbMode NbMode = NbMode.Off,
    double NbThreshold = 20.0);

public sealed record StateDto(
    ConnectionStatus Status,
    string? Endpoint,
    long VfoHz,
    RxMode Mode,
    int FilterLowHz,
    int FilterHighHz,
    int SampleRate,
    double AgcTopDb = 80.0,
    // User-baseline attenuator in dB, 0..31. Hardware receives
    // <c>AttenDb + AttOffsetDb</c> (clamped to 31) while auto-ATT is engaged.
    // Default is 0 — auto-ATT ramps the offset up on observed ADC overloads.
    int AttenDb = 0,
    NrConfig? Nr = null,
    int ZoomLevel = 1,
    // Auto-attenuator control loop. When on (default), the server raises
    // AttOffsetDb by 1 per ~100 ms window in which any ADC-overload bit was
    // seen, and decays it by 1 per clean window. Ported from Thetis
    // console.cs:22167 (handleOverload).
    bool AutoAttEnabled = true,
    int AttOffsetDb = 0,
    // Red-lamp flag derived from Thetis' overload-level counter
    // (+2 per overload cycle, -1 per clean, clamped 0..5, warn when >3).
    bool AdcOverloadWarning = false);

public sealed record RadioInfo(
    string MacAddress,
    string IpAddress,
    string BoardId,
    string FirmwareVersion,
    bool Busy,
    IReadOnlyDictionary<string, string>? Details = null);

public sealed record ConnectRequest(
    string Endpoint,
    int SampleRate = 192_000,
    bool? PreampOn = null,
    int? Atten = null);

public sealed record VfoSetRequest(long Hz);

public sealed record ModeSetRequest(RxMode Mode);

public sealed record BandwidthSetRequest(int Low, int High);

public sealed record SampleRateSetRequest(int Rate);

public sealed record PreampSetRequest(bool On);

public sealed record AgcGainSetRequest(double TopDb);

public sealed record AttenuatorSetRequest(int Db);

public sealed record MoxSetRequest(bool On);

public sealed record DriveSetRequest(int Percent);

public sealed record NrSetRequest(NrConfig Nr);

// Panadapter/waterfall zoom levels. Level=1 means the analyzer covers the full
// sample-rate span; level=2 means VFO-centered half-span (×2 bins/Hz), and so
// on. The span-centering math lives in the engine; this contract just carries
// the discrete factor on the wire.
public sealed record ZoomSetRequest(int Level);

public sealed record AutoAttSetRequest(bool Enabled);

public sealed record TunSetRequest(bool On);

public sealed record MicGainSetRequest(int Db);

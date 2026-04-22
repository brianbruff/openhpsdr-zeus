namespace Zeus.Dsp;

/// <summary>
/// Per-block TXA stage readings sampled from the WDSP metering ring. Level
/// fields (*Pk/*Av) are dBFS; gain-reduction fields (*Gr) are positive dB
/// of reduction (0 = no reduction, 6 = 6 dB cut). When TXA is not processing
/// (MOX off, or engine lacks TXA), all level fields are
/// <see cref="float.NegativeInfinity"/> and all gain-reduction fields are 0.
///
/// Both peak and average are captured for each active stage. The operator
/// uses average to judge level and peak to spot clipping-induced distortion
/// that hides inside the average window's ~100 ms smoothing. Indices tracked
/// per <c>native/wdsp/TXA.h:49-66</c> txaMeterType.
///
/// Sign convention for *Gr fields: WDSP returns <c>TXA_*_GAIN</c> as
/// <c>20*log10(linear_gain)</c>, which is ≤ 0 when the stage is reducing.
/// Callers must negate before storing here so downstream consumers see a
/// monotonic "how much are we cutting?" scale.
///
/// CFC / COMP readings will sit at the WDSP silence sentinel (≈ −400 dBFS)
/// until their stages are engaged; the frontend treats the sentinel as
/// "bypassed" (P1.4 sentinel handling). Leaving the fields in the record
/// keeps the DSP→wire pipeline uniform regardless of which stages are on.
/// </summary>
public readonly record struct TxStageMeters(
    float MicPk,
    float MicAv,
    float EqPk,
    float EqAv,
    float LvlrPk,
    float LvlrAv,
    float LvlrGr,
    float CfcPk,
    float CfcAv,
    float CfcGr,
    float CompPk,
    float CompAv,
    float AlcPk,
    float AlcAv,
    float AlcGr,
    float OutPk,
    float OutAv)
{
    public static readonly TxStageMeters Silent = new(
        MicPk: float.NegativeInfinity,
        MicAv: float.NegativeInfinity,
        EqPk: float.NegativeInfinity,
        EqAv: float.NegativeInfinity,
        LvlrPk: float.NegativeInfinity,
        LvlrAv: float.NegativeInfinity,
        LvlrGr: 0f,
        CfcPk: float.NegativeInfinity,
        CfcAv: float.NegativeInfinity,
        CfcGr: 0f,
        CompPk: float.NegativeInfinity,
        CompAv: float.NegativeInfinity,
        AlcPk: float.NegativeInfinity,
        AlcAv: float.NegativeInfinity,
        AlcGr: 0f,
        OutPk: float.NegativeInfinity,
        OutAv: float.NegativeInfinity);
}

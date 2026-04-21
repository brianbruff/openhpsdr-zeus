namespace Zeus.Dsp;

/// <summary>
/// Per-block peak readings from the WDSP TXA metering ring. Values are in
/// dBFS for the level meters and dB for the gain-reduction meters. When TXA
/// is not processing (MOX off, or engine lacks TXA), all fields are
/// <see cref="float.NegativeInfinity"/> except the gain fields which are 0.
///
/// Chosen as peak (not average) readings — the diagnostic we care about is
/// clipping-induced distortion, which hides inside the average window's
/// ~100 ms smoothing. Indices tracked per <c>native/wdsp/TXA.h</c> txaMeterType.
///
/// Sign convention: <see cref="AlcGr"/> is stored as positive dB of gain
/// reduction (0 = no reduction, 6 = 6 dB cut). WDSP returns <c>TXA_ALC_GAIN</c>
/// as <c>20*log10(linear_gain)</c> which is ≤ 0 when reducing — callers must
/// negate before storing here.
/// </summary>
public readonly record struct TxStageMeters(
    float MicPk,
    float EqPk,
    float LvlrPk,
    float AlcPk,
    float AlcGr,
    float OutPk)
{
    public static readonly TxStageMeters Silent = new(
        MicPk: float.NegativeInfinity,
        EqPk: float.NegativeInfinity,
        LvlrPk: float.NegativeInfinity,
        AlcPk: float.NegativeInfinity,
        AlcGr: 0f,
        OutPk: float.NegativeInfinity);
}

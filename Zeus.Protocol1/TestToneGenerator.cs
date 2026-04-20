namespace Zeus.Protocol1;

/// <summary>
/// Server-side single-frequency sinewave IQ generator used to prove the TX
/// chain end-to-end without an uplink mic path. 48 kHz IQ stream with a
/// continuous phase accumulator so successive packets splice together without
/// clicks at the frame boundary.
/// </summary>
public sealed class TestToneGenerator : ITxIqSource
{
    public const int DefaultSampleRateHz = 48_000;
    public const double DefaultFrequencyHz = 1_000.0;

    private readonly double _phaseIncrement;
    private double _phase;

    public TestToneGenerator(double frequencyHz = DefaultFrequencyHz, int sampleRateHz = DefaultSampleRateHz)
    {
        _phaseIncrement = 2.0 * Math.PI * frequencyHz / sampleRateHz;
    }

    public double Phase => _phase;

    /// <summary>
    /// Emit the next IQ sample. <paramref name="amplitude"/> is in 0..1 and
    /// scales the full-scale s16 range; deskhpsdr's TX path treats the radio
    /// as expecting full-scale IQ, so 1.0 here means s16 max. 0.5 leaves 6 dB
    /// of headroom which is where the task #3 spec parks us at 100% drive.
    /// </summary>
    public (short i, short q) Next(double amplitude)
    {
        double a = Math.Clamp(amplitude, 0.0, 1.0);
        double cos = Math.Cos(_phase);
        double sin = Math.Sin(_phase);
        _phase += _phaseIncrement;
        if (_phase >= 2.0 * Math.PI) _phase -= 2.0 * Math.PI;

        short i = (short)Math.Round(a * cos * short.MaxValue);
        short q = (short)Math.Round(a * sin * short.MaxValue);
        return (i, q);
    }
}

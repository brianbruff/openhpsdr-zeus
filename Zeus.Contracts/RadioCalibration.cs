namespace Nereus.Contracts;

/// <summary>
/// Per-radio-model constants for TX forward / reflected power calibration and
/// the safe PA ceiling used for meter scaling. The power math is the same
/// across boards — <c>watts = volts² / bridge_volt</c> where
/// <c>volts = (adc − cal_offset) / 4095 · ref_voltage</c> — only the constants
/// differ. Thetis <c>console.cs:25008-25072</c> is the reference.
/// </summary>
public sealed record RadioCalibration(
    double BridgeVolt,
    double RefVoltage,
    int AdcCalOffset,
    double MaxWatts)
{
    /// <summary>
    /// Hermes-Lite 2 defaults. Thetis <c>console.cs:25973-25977</c> uses
    /// <c>bridge_volt = 1.5</c> for HL2 specifically — its onboard RF detector
    /// has a very different transfer function from the classic Alex bridge
    /// (which is 0.09). Using the Alex value reads ~16× too high.
    /// MaxWatts is the 5 W PA rating — meter scaling only, not protection.
    /// </summary>
    public static readonly RadioCalibration HermesLite2 = new(
        BridgeVolt: 1.5,
        RefVoltage: 3.3,
        AdcCalOffset: 6,
        MaxWatts: 5.0);
}

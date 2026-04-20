namespace Nereus.Protocol1;

/// <summary>
/// Supplies one (I, Q) s16 pair per call for the EP2 TX payload. Two concrete
/// sources: <see cref="TestToneGenerator"/> for bring-up / no-mic diagnostics,
/// and <see cref="TxIqRing"/> for the mic-driven WDSP-TXA path.
/// Implementations must be safe for a single reader (the Protocol1 TX loop);
/// the ring variant handles cross-thread writes from the ingest side.
/// </summary>
public interface ITxIqSource
{
    /// <summary>
    /// Return the next IQ sample. <paramref name="amplitude"/> is a 0..1
    /// multiplier on the s16 range; the test-tone uses it for headroom, the
    /// ring passes it through to the already-modulated sample.
    /// </summary>
    (short i, short q) Next(double amplitude);
}

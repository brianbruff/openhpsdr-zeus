namespace Nereus.Protocol1;

/// <summary>
/// One decoded RX IQ packet. Interleaved I/Q stored as <c>double</c>s already
/// scaled from the wire's int24 big-endian form into [-1.0, +1.0] so that
/// <c>Nereus.Dsp</c> is never exposed to the wire format (doc 07 §1.1).
/// </summary>
/// <remarks>
/// <see cref="InterleavedSamples"/> backs onto a buffer rented from
/// <see cref="System.Buffers.ArrayPool{Double}.Shared"/> at RX time. Consumers
/// treat the memory as read-only for the frame's lifetime.
/// </remarks>
public readonly record struct IqFrame(
    ReadOnlyMemory<double> InterleavedSamples,
    int SampleCount,
    int SampleRateHz,
    uint Sequence,
    long TimestampNs);

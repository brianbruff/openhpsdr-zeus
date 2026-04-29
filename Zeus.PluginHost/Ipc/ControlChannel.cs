// ControlChannel.cs — length-prefixed control transport.
//
// Phase 1 carries opaque byte[] payloads with a 4-byte little-endian
// length prefix. The CBOR encoding lands in Phase 2 — likely PeterO.Cbor
// (BSD-3) or System.Formats.Cbor (built-in to .NET 8). For now the test
// surface is the framing layer only; the payload bytes pass through
// untouched.
//
// Transport: Win32 named pipe on Windows, Unix-domain socket on
// Linux/macOS. Phase 1 implementation is the thin wrapper below — the
// concrete pipe / UDS open is handled by the SidecarProcess + manager
// once the sidecar is running. Until that wiring exists, this class is
// constructable around any duplex Stream so it can be unit-tested
// against a paired pair of MemoryStreams or a NamedPipeServerStream
// pair.

using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Zeus.PluginHost.Ipc;

/// <summary>
/// Thin length-prefixed framing on top of a duplex byte stream.
/// </summary>
/// <remarks>
/// Wire frame:
/// <code>
///   [u32 length, little-endian] [payload bytes ...]
/// </code>
/// Phase 1 keeps the payload opaque. Phase 2 will replace
/// <see cref="SendAsync"/> / <see cref="ReceiveAsync"/> with CBOR-encoded
/// <see cref="ControlMessageType"/>-tagged messages.
/// </remarks>
public sealed class ControlChannel : IDisposable
{
    /// <summary>Defensive cap on a single frame's payload — 1 MiB is far
    /// larger than any Phase 1 message and far smaller than what a
    /// runaway sidecar could try to push in a memory exhaustion attempt.</summary>
    public const int MaxPayloadBytes = 1 * 1024 * 1024;

    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private bool _disposed;

    // TODO(phase2): swap byte[] payloads for CBOR encode/decode here. The
    // candidate libraries are PeterO.Cbor (BSD-3, mature, RFC 8949) and
    // System.Formats.Cbor (in-box on .NET 8 but with a thinner API). We
    // defer the choice until the sidecar's C++ control writer exists, so
    // both ends can agree on a single tag scheme at the same time.

    public ControlChannel(Stream stream, bool ownsStream = true)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _ownsStream = ownsStream;
    }

    /// <summary>Send one length-prefixed frame.</summary>
    public async Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (payload.Length > MaxPayloadBytes)
        {
            throw new ArgumentException(
                $"payload exceeds MaxPayloadBytes ({MaxPayloadBytes})",
                nameof(payload));
        }

        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)payload.Length);
        await _stream.WriteAsync(prefix, ct).ConfigureAwait(false);
        if (payload.Length > 0)
        {
            await _stream.WriteAsync(payload, ct).ConfigureAwait(false);
        }
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Receive one length-prefixed frame, or <c>null</c> if the peer
    /// closed the stream cleanly between frames.
    /// </summary>
    public async Task<byte[]?> ReceiveAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        var prefix = new byte[4];
        var read = await ReadFullyAsync(_stream, prefix, ct).ConfigureAwait(false);
        if (read == 0) return null; // clean EOF
        if (read != 4) throw new EndOfStreamException("truncated length prefix");

        var length = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        if (length > MaxPayloadBytes)
        {
            throw new InvalidDataException(
                $"control frame length {length} exceeds MaxPayloadBytes");
        }
        if (length == 0) return Array.Empty<byte>();

        var buf = new byte[length];
        var got = await ReadFullyAsync(_stream, buf, ct).ConfigureAwait(false);
        if (got != length)
        {
            throw new EndOfStreamException("truncated control payload");
        }
        return buf;
    }

    private static async Task<int> ReadFullyAsync(
        Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.Slice(total), ct).ConfigureAwait(false);
            if (n == 0)
            {
                return total; // peer closed; caller decides whether truncation is fatal
            }
            total += n;
        }
        return total;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ControlChannel));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsStream)
        {
            try { _stream.Dispose(); } catch { /* best-effort close */ }
        }
    }
}

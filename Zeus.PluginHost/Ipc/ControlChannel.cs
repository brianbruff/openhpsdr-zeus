// ControlChannel.cs — host-side AF_UNIX SOCK_STREAM control plane.
//
// We are the server. Bind + listen happen BEFORE the sidecar is forked,
// then the sidecar connect()s. The wire is length-prefixed:
//
//   [u32 length, little-endian] [u8 type] [payload bytes ...]
//                                ^ length includes the type byte
//
// Phase 2 message types are mirrored from the C++ ControlMessageTag enum;
// see docs/proposals/vst-host-phase2-wire.md for the canonical list.
//
// Synchronous I/O is fine for Phase 2 — message volume is tiny (Hello +
// HelloAck + Goodbye + the occasional LogLine). Phase 3 will move to
// async with cancellation tokens once we have parameter messages flowing.

using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Zeus.PluginHost.Ipc;

/// <summary>
/// Phase 2 / 3a message tag — wire-shared with C++ <c>ControlMessageTag</c>.
/// </summary>
public enum ControlTag : byte
{
    Hello                  = 0x01,
    HelloAck               = 0x02,
    Heartbeat              = 0x03,
    Goodbye                = 0x04,
    LogLine                = 0x05,
    LoadPlugin             = 0x10,
    LoadPluginResult       = 0x11,
    UnloadPlugin           = 0x12,
    UnloadPluginResult     = 0x13,
    SlotLoadPlugin         = 0x14,
    SlotLoadPluginResult   = 0x15,
    SlotUnloadPlugin       = 0x16,
    SlotUnloadPluginResult = 0x17,
    SlotSetBypass          = 0x18,
    SlotSetBypassResult    = 0x19,
    SetChainEnabled        = 0x1A,
    SetChainEnabledResult  = 0x1B,
    SlotListParams         = 0x20,
    SlotParamListResult    = 0x21,
    SlotSetParam           = 0x22,
    SlotSetParamResult     = 0x23,
    SlotShowEditor         = 0x30,
    SlotShowEditorResult   = 0x31,
    SlotHideEditor         = 0x32,
    SlotHideEditorResult   = 0x33,
    EditorClosed           = 0x34,
    EditorResized          = 0x35,
    ParamChanged           = 0x36,
}

/// <summary>
/// Host-side AF_UNIX SOCK_STREAM server. One concurrent connection per
/// host instance. Constructed with <see cref="Bind"/>, then
/// <see cref="AcceptAsync"/> must be called to wait for the sidecar's
/// connect.
/// </summary>
public sealed class ControlChannel : IDisposable
{
    /// <summary>Defensive cap on a single frame's payload. Phase 2 messages
    /// are all under 100 bytes; 1 MiB is far larger than anything plausible
    /// and protects against a runaway sidecar exhausting host memory.</summary>
    public const int MaxPayloadBytes = 1 * 1024 * 1024;

    private readonly string _socketPath;
    private Socket? _listener;
    private Socket? _conn;
    private NetworkStream? _stream;
    private bool _disposed;

    /// <summary>True once a sidecar has connected and the stream is ready.</summary>
    public bool IsConnected => _stream != null && !_disposed;

    /// <summary>
    /// Async event: the sidecar closed an editor window (window-manager
    /// DELETE button OR plugin-driven). Raised whenever a 0x34
    /// EditorClosed frame is dispatched via <see cref="DispatchAsyncIfApplicable"/>
    /// (typically from the request/reply pump in PluginHostManager).
    /// </summary>
    public event EventHandler<EditorClosedEventArgs>? EditorClosed;

    /// <summary>
    /// Async event: the plugin asked to resize the editor window and
    /// the sidecar honored it. Informational.
    /// </summary>
    public event EventHandler<EditorResizedEventArgs>? EditorResized;

    /// <summary>
    /// Async event: the plugin's editor (or its internal automation) drove
    /// a parameter change via IComponentHandler::performEdit. Each fire
    /// carries (slotIdx, paramId, normalizedValue). Subscribers should
    /// update their cached parameter snapshot and schedule a persistence
    /// flush; do NOT mirror the value back via SlotSetParam (that would
    /// echo). Wave 7 — wire-spec tag 0x36.
    /// </summary>
    public event EventHandler<ParamChangedEventArgs>? ParamChanged;

    /// <summary>
    /// Returns true and dispatches via <see cref="EditorClosed"/> /
    /// <see cref="EditorResized"/> when the frame's tag is one of the
    /// async editor events; returns false for any other tag (caller is
    /// then responsible for dispatch, e.g. matching to a sync awaiter).
    /// </summary>
    public bool DispatchAsyncIfApplicable(in ControlFrame frame)
    {
        switch (frame.Tag)
        {
            case ControlTag.EditorClosed:
            {
                var ev = EditorClosedEvent.Decode(frame.Payload);
                EditorClosed?.Invoke(this, new EditorClosedEventArgs(ev.SlotIdx));
                return true;
            }
            case ControlTag.EditorResized:
            {
                var ev = EditorResizedEvent.Decode(frame.Payload);
                EditorResized?.Invoke(this, new EditorResizedEventArgs(
                    ev.SlotIdx, (int)ev.Width, (int)ev.Height));
                return true;
            }
            case ControlTag.ParamChanged:
            {
                var ev = ParamChangedEvent.Decode(frame.Payload);
                ParamChanged?.Invoke(this, new ParamChangedEventArgs(
                    ev.SlotIdx, ev.ParamId, ev.NormalizedValue));
                return true;
            }
            default:
                return false;
        }
    }

    /// <summary>Path of the AF_UNIX socket the host bound.</summary>
    public string SocketPath => _socketPath;

    private ControlChannel(string socketPath, Socket listener)
    {
        _socketPath = socketPath;
        _listener = listener;
    }

    /// <summary>
    /// Bind + listen on <paramref name="socketPath"/>. The path is unlinked
    /// first if it already exists (recovery from a crashed prior host).
    /// </summary>
    public static ControlChannel Bind(string socketPath)
    {
        EnsureSupported();
        if (string.IsNullOrEmpty(socketPath))
        {
            throw new ArgumentException("socketPath must be non-empty", nameof(socketPath));
        }
        // Recover from any previous crash that left the inode behind.
        try { File.Delete(socketPath); } catch { /* best-effort */ }

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(1);
        return new ControlChannel(socketPath, listener);
    }

    /// <summary>
    /// Wait for the sidecar to connect. Throws on timeout. Idempotent if a
    /// sidecar is already connected (returns immediately).
    /// </summary>
    public async Task AcceptAsync(TimeSpan timeout, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (_stream != null) return;

        var listener = _listener
            ?? throw new InvalidOperationException("ControlChannel is not bound");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        Socket conn;
        try
        {
            conn = await listener.AcceptAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(
                $"ControlChannel: no sidecar connect within {timeout}");
        }
        _conn = conn;
        _stream = new NetworkStream(conn, ownsSocket: true);
    }

    /// <summary>Send one length-prefixed control message.</summary>
    public async Task SendAsync(ControlTag tag, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (_stream == null) throw new InvalidOperationException("not connected");
        if (payload.Length > MaxPayloadBytes)
        {
            throw new ArgumentException(
                $"payload exceeds MaxPayloadBytes ({MaxPayloadBytes})", nameof(payload));
        }

        var prefix = new byte[5];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)(payload.Length + 1));
        prefix[4] = (byte)tag;
        await _stream.WriteAsync(prefix, ct).ConfigureAwait(false);
        if (payload.Length > 0)
        {
            await _stream.WriteAsync(payload, ct).ConfigureAwait(false);
        }
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Receive one length-prefixed control frame, or <c>null</c> if the peer
    /// closed cleanly between frames.
    /// </summary>
    public async Task<ControlFrame?> ReceiveAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        if (_stream == null) throw new InvalidOperationException("not connected");

        var prefix = new byte[4];
        var got = await ReadFullyAsync(_stream, prefix, ct).ConfigureAwait(false);
        if (got == 0) return null;
        if (got != 4) throw new EndOfStreamException("truncated length prefix");

        var length = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        if (length == 0 || length > MaxPayloadBytes)
        {
            throw new InvalidDataException($"invalid control frame length {length}");
        }

        var tagBuf = new byte[1];
        if (await ReadFullyAsync(_stream, tagBuf, ct).ConfigureAwait(false) != 1)
        {
            throw new EndOfStreamException("truncated tag byte");
        }
        var tag = (ControlTag)tagBuf[0];

        var payloadLen = (int)(length - 1);
        if (payloadLen == 0) return new ControlFrame(tag, Array.Empty<byte>());

        var buf = new byte[payloadLen];
        if (await ReadFullyAsync(_stream, buf, ct).ConfigureAwait(false) != payloadLen)
        {
            throw new EndOfStreamException("truncated payload");
        }
        return new ControlFrame(tag, buf);
    }

    private static async Task<int> ReadFullyAsync(
        Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.Slice(total), ct).ConfigureAwait(false);
            if (n == 0) return total;
            total += n;
        }
        return total;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ControlChannel));
    }

    private static void EnsureSupported()
    {
        if (!Socket.OSSupportsUnixDomainSockets)
        {
            throw new PlatformNotSupportedException(
                "AF_UNIX sockets are not supported on this OS.");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _stream?.Dispose(); } catch { /* best-effort */ }
        try { _conn?.Dispose(); } catch { /* best-effort */ }
        try { _listener?.Dispose(); } catch { /* best-effort */ }
        _stream = null;
        _conn = null;
        _listener = null;
        try
        {
            if (File.Exists(_socketPath)) File.Delete(_socketPath);
        }
        catch { /* best-effort */ }
    }
}

/// <summary>One received control frame: tag plus payload bytes.</summary>
public readonly record struct ControlFrame(ControlTag Tag, byte[] Payload);

/// <summary>Async event payload from the sidecar: editor for slot was closed.</summary>
public sealed class EditorClosedEventArgs : EventArgs
{
    public int SlotIdx { get; }
    public EditorClosedEventArgs(int slotIdx) { SlotIdx = slotIdx; }
}

/// <summary>Async event payload from the sidecar: editor for slot was resized.</summary>
public sealed class EditorResizedEventArgs : EventArgs
{
    public int SlotIdx { get; }
    public int Width   { get; }
    public int Height  { get; }
    public EditorResizedEventArgs(int slotIdx, int width, int height)
    {
        SlotIdx = slotIdx;
        Width   = width;
        Height  = height;
    }
}

/// <summary>Async event payload: editor (or plugin internal) drove a
/// parameter change via IComponentHandler::performEdit. Used by the host
/// to keep ChainSlot.Parameters in sync and trigger LiteDB save.</summary>
public sealed class ParamChangedEventArgs : EventArgs
{
    public int    SlotIdx         { get; }
    public uint   ParamId         { get; }
    public double NormalizedValue { get; }
    public ParamChangedEventArgs(int slotIdx, uint paramId, double value)
    {
        SlotIdx         = slotIdx;
        ParamId         = paramId;
        NormalizedValue = value;
    }
}

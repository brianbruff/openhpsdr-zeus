// ControlMessages.cs — concrete Phase 2 control-message types.
//
// Wire encoding is the simple length-prefixed framing in ControlChannel.cs:
// each message is a u32 length + u8 tag + payload. The records below
// represent the typed payload only; the tag is taken from
// <see cref="ControlTag"/>. See docs/proposals/vst-host-phase2-wire.md
// for the canonical byte layout.

using System;
using System.Buffers.Binary;

namespace Zeus.PluginHost.Ipc;

/// <summary>
/// Sidecar -> host handshake. Sent immediately after the sidecar connects.
/// Payload is 16 bytes, four little-endian uint32s.
/// </summary>
public sealed record HelloMessage(
    uint ProtocolVersion,
    uint SampleRate,
    uint FramesPerBlock,
    uint Channels)
{
    public const int PayloadBytes = 16;

    public byte[] Encode()
    {
        var buf = new byte[PayloadBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4),  ProtocolVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4),  SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8, 4),  FramesPerBlock);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12, 4), Channels);
        return buf;
    }

    public static HelloMessage Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != PayloadBytes)
        {
            throw new ArgumentException(
                $"Hello payload must be {PayloadBytes} bytes, got {payload.Length}");
        }
        var ver  = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(0, 4));
        var rate = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4));
        var fpb  = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(8, 4));
        var ch   = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(12, 4));
        return new HelloMessage(ver, rate, fpb, ch);
    }
}

/// <summary>Host -> sidecar handshake confirmation. Empty payload.</summary>
public sealed record HelloAckMessage
{
    public static readonly HelloAckMessage Instance = new();
    public byte[] Encode() => Array.Empty<byte>();
}

/// <summary>Bidirectional 1 Hz keepalive. Empty payload in Phase 2.</summary>
public sealed record HeartbeatMessage
{
    public static readonly HeartbeatMessage Instance = new();
    public byte[] Encode() => Array.Empty<byte>();
}

/// <summary>Host -> sidecar graceful-shutdown signal. Empty payload.</summary>
public sealed record GoodbyeMessage
{
    public static readonly GoodbyeMessage Instance = new();
    public byte[] Encode() => Array.Empty<byte>();
}

/// <summary>Sidecar -> host diagnostic line. Payload is UTF-8 text.</summary>
public sealed record LogLineMessage(string Text)
{
    public byte[] Encode() => System.Text.Encoding.UTF8.GetBytes(Text);

    public static LogLineMessage Decode(ReadOnlySpan<byte> payload)
    {
        return new LogLineMessage(System.Text.Encoding.UTF8.GetString(payload));
    }
}

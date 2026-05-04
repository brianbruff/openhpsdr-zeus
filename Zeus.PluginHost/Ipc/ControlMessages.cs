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

/// <summary>
/// Host -> sidecar plugin load request. Payload is u32 LE pathLen + UTF-8 path.
/// The path is an absolute filesystem location of a VST3 bundle directory or
/// single .vst3 file (platform-dependent).
/// </summary>
public sealed record LoadPluginRequest(string Path)
{
    public byte[] Encode()
    {
        var pathBytes = System.Text.Encoding.UTF8.GetBytes(Path);
        var buf = new byte[4 + pathBytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4),
            (uint)pathBytes.Length);
        pathBytes.CopyTo(buf, 4);
        return buf;
    }

    public static LoadPluginRequest Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4)
        {
            throw new ArgumentException(
                "LoadPluginRequest payload must contain at least the u32 length prefix");
        }
        var len = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(0, 4));
        if (payload.Length != 4 + (int)len)
        {
            throw new ArgumentException(
                $"LoadPluginRequest payload length {payload.Length} != " +
                $"4 + {len}");
        }
        var path = System.Text.Encoding.UTF8.GetString(
            payload.Slice(4, (int)len));
        return new LoadPluginRequest(path);
    }
}

/// <summary>
/// Sidecar -> host plugin load result. <see cref="Status"/> = 0 ok with
/// <see cref="Name"/>/<see cref="Vendor"/>/<see cref="Version"/> populated;
/// non-zero with <see cref="Error"/> populated. Status codes mirror the wire
/// spec:
///   0 ok, 1 file-not-found, 2 not-a-vst3, 3 no-audio-effect-class,
///   4 activate-failed, 5 other.
/// </summary>
public sealed record LoadPluginResult(
    byte Status,
    string? Name,
    string? Vendor,
    string? Version,
    string? Error)
{
    public byte[] Encode()
    {
        var ms = new System.IO.MemoryStream();
        ms.WriteByte(Status);
        if (Status == 0)
        {
            WriteLengthPrefixedString(ms, Name ?? string.Empty);
            WriteLengthPrefixedString(ms, Vendor ?? string.Empty);
            WriteLengthPrefixedString(ms, Version ?? string.Empty);
        }
        else
        {
            WriteLengthPrefixedString(ms, Error ?? string.Empty);
        }
        return ms.ToArray();
    }

    public static LoadPluginResult Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1)
        {
            throw new ArgumentException("LoadPluginResult payload must be at least 1 byte");
        }
        var status = payload[0];
        var cursor = 1;
        if (status == 0)
        {
            var name    = ReadLengthPrefixedString(payload, ref cursor);
            var vendor  = ReadLengthPrefixedString(payload, ref cursor);
            var version = ReadLengthPrefixedString(payload, ref cursor);
            return new LoadPluginResult(0, name, vendor, version, null);
        }
        var err = ReadLengthPrefixedString(payload, ref cursor);
        return new LoadPluginResult(status, null, null, null, err);
    }

    private static void WriteLengthPrefixedString(System.IO.Stream s, string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)bytes.Length);
        s.Write(len);
        s.Write(bytes, 0, bytes.Length);
    }

    private static string ReadLengthPrefixedString(
        ReadOnlySpan<byte> input, ref int cursor)
    {
        if (input.Length < cursor + 4)
        {
            throw new ArgumentException(
                "LoadPluginResult: truncated string length prefix");
        }
        var len = BinaryPrimitives.ReadUInt32LittleEndian(
            input.Slice(cursor, 4));
        if (input.Length < cursor + 4 + (int)len)
        {
            throw new ArgumentException(
                "LoadPluginResult: truncated string body");
        }
        var value = System.Text.Encoding.UTF8.GetString(
            input.Slice(cursor + 4, (int)len));
        cursor += 4 + (int)len;
        return value;
    }
}

/// <summary>Host -> sidecar plugin unload request. Empty payload.</summary>
public sealed record UnloadPluginRequest
{
    public static readonly UnloadPluginRequest Instance = new();
    public byte[] Encode() => Array.Empty<byte>();
}

/// <summary>
/// Sidecar -> host plugin unload result. <see cref="Status"/>:
/// 0 ok, 1 no-plugin-loaded (the unload was a no-op), 5 other.
/// </summary>
public sealed record UnloadPluginResult(byte Status)
{
    public byte[] Encode() => new[] { Status };

    public static UnloadPluginResult Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 1)
        {
            throw new ArgumentException(
                $"UnloadPluginResult payload must be 1 byte, got {payload.Length}");
        }
        return new UnloadPluginResult(payload[0]);
    }
}

// =====================================================================
// Phase 3a — slot-aware chain operations.
// =====================================================================

/// <summary>
/// Host -> sidecar slot-aware load. Payload: u8 slotIdx + u32 LE pathLen
/// + UTF-8 path bytes.
/// </summary>
public sealed record SlotLoadPluginRequest(byte SlotIdx, string Path)
{
    public byte[] Encode()
    {
        var pathBytes = System.Text.Encoding.UTF8.GetBytes(Path);
        var buf = new byte[1 + 4 + pathBytes.Length];
        buf[0] = SlotIdx;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(1, 4),
            (uint)pathBytes.Length);
        pathBytes.CopyTo(buf, 5);
        return buf;
    }

    public static SlotLoadPluginRequest Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 5)
        {
            throw new ArgumentException(
                "SlotLoadPluginRequest payload must contain at least 5 bytes");
        }
        var slot = payload[0];
        var len = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(1, 4));
        if (payload.Length != 5 + (int)len)
        {
            throw new ArgumentException(
                $"SlotLoadPluginRequest payload length {payload.Length} != " +
                $"5 + {len}");
        }
        var path = System.Text.Encoding.UTF8.GetString(
            payload.Slice(5, (int)len));
        return new SlotLoadPluginRequest(slot, path);
    }
}

/// <summary>
/// Sidecar -> host slot-aware load result. Status codes:
///   0 ok, 1 file-not-found, 2 not-a-vst3, 3 no-audio-effect-class,
///   4 activate-failed, 5 other, 6 invalid-slot-index.
/// On status==0: name/vendor/version follow. On non-zero: error string.
/// </summary>
public sealed record SlotLoadPluginResult(
    byte SlotIdx,
    byte Status,
    string? Name,
    string? Vendor,
    string? Version,
    string? Error)
{
    public byte[] Encode()
    {
        var ms = new System.IO.MemoryStream();
        ms.WriteByte(SlotIdx);
        ms.WriteByte(Status);
        if (Status == 0)
        {
            WriteLengthPrefixedString(ms, Name ?? string.Empty);
            WriteLengthPrefixedString(ms, Vendor ?? string.Empty);
            WriteLengthPrefixedString(ms, Version ?? string.Empty);
        }
        else
        {
            WriteLengthPrefixedString(ms, Error ?? string.Empty);
        }
        return ms.ToArray();
    }

    public static SlotLoadPluginResult Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2)
        {
            throw new ArgumentException(
                "SlotLoadPluginResult payload must be at least 2 bytes");
        }
        var slot = payload[0];
        var status = payload[1];
        var cursor = 2;
        if (status == 0)
        {
            var name    = ReadLengthPrefixedString(payload, ref cursor);
            var vendor  = ReadLengthPrefixedString(payload, ref cursor);
            var version = ReadLengthPrefixedString(payload, ref cursor);
            return new SlotLoadPluginResult(slot, 0, name, vendor, version, null);
        }
        var err = ReadLengthPrefixedString(payload, ref cursor);
        return new SlotLoadPluginResult(slot, status, null, null, null, err);
    }

    internal static void WriteLengthPrefixedString(System.IO.Stream s, string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)bytes.Length);
        s.Write(len);
        s.Write(bytes, 0, bytes.Length);
    }

    internal static string ReadLengthPrefixedString(
        ReadOnlySpan<byte> input, ref int cursor)
    {
        if (input.Length < cursor + 4)
        {
            throw new ArgumentException(
                "SlotLoadPluginResult: truncated string length prefix");
        }
        var len = BinaryPrimitives.ReadUInt32LittleEndian(
            input.Slice(cursor, 4));
        if (input.Length < cursor + 4 + (int)len)
        {
            throw new ArgumentException(
                "SlotLoadPluginResult: truncated string body");
        }
        var value = System.Text.Encoding.UTF8.GetString(
            input.Slice(cursor + 4, (int)len));
        cursor += 4 + (int)len;
        return value;
    }
}

/// <summary>Host -> sidecar slot unload. Payload: u8 slotIdx.</summary>
public sealed record SlotUnloadPluginRequest(byte SlotIdx)
{
    public byte[] Encode() => new[] { SlotIdx };

    public static SlotUnloadPluginRequest Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 1)
        {
            throw new ArgumentException(
                $"SlotUnloadPluginRequest payload must be 1 byte, got {payload.Length}");
        }
        return new SlotUnloadPluginRequest(payload[0]);
    }
}

/// <summary>
/// Sidecar -> host slot unload result. Status codes:
///   0 ok, 1 no-plugin-loaded, 5 other, 6 invalid-slot-index.
/// </summary>
public sealed record SlotUnloadPluginResult(byte SlotIdx, byte Status)
{
    public byte[] Encode() => new[] { SlotIdx, Status };

    public static SlotUnloadPluginResult Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 2)
        {
            throw new ArgumentException(
                $"SlotUnloadPluginResult payload must be 2 bytes, got {payload.Length}");
        }
        return new SlotUnloadPluginResult(payload[0], payload[1]);
    }
}

/// <summary>Host -> sidecar bypass toggle. Payload: u8 slot + u8 bypass.</summary>
public sealed record SlotSetBypassRequest(byte SlotIdx, bool Bypass)
{
    public byte[] Encode() => new[] { SlotIdx, (byte)(Bypass ? 1 : 0) };

    public static SlotSetBypassRequest Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 2)
        {
            throw new ArgumentException(
                $"SlotSetBypassRequest payload must be 2 bytes, got {payload.Length}");
        }
        return new SlotSetBypassRequest(payload[0], payload[1] != 0);
    }
}

/// <summary>
/// Sidecar -> host bypass result. Status codes:
///   0 ok, 5 other, 6 invalid-slot-index.
/// </summary>
public sealed record SlotSetBypassResult(byte SlotIdx, byte Status)
{
    public byte[] Encode() => new[] { SlotIdx, Status };

    public static SlotSetBypassResult Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 2)
        {
            throw new ArgumentException(
                $"SlotSetBypassResult payload must be 2 bytes, got {payload.Length}");
        }
        return new SlotSetBypassResult(payload[0], payload[1]);
    }
}

/// <summary>Host -> sidecar master enable. Payload: u8 enabled.</summary>
public sealed record SetChainEnabledRequest(bool Enabled)
{
    public byte[] Encode() => new[] { (byte)(Enabled ? 1 : 0) };

    public static SetChainEnabledRequest Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 1)
        {
            throw new ArgumentException(
                $"SetChainEnabledRequest payload must be 1 byte, got {payload.Length}");
        }
        return new SetChainEnabledRequest(payload[0] != 0);
    }
}

/// <summary>Sidecar -> host master enable result. Status: 0 ok, 5 other.</summary>
public sealed record SetChainEnabledResult(byte Status)
{
    public byte[] Encode() => new[] { Status };

    public static SetChainEnabledResult Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 1)
        {
            throw new ArgumentException(
                $"SetChainEnabledResult payload must be 1 byte, got {payload.Length}");
        }
        return new SetChainEnabledResult(payload[0]);
    }
}

/// <summary>Host -> sidecar list-parameters request. Payload: u8 slotIdx.</summary>
public sealed record SlotListParamsRequest(byte SlotIdx)
{
    public byte[] Encode() => new[] { SlotIdx };

    public static SlotListParamsRequest Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 1)
        {
            throw new ArgumentException(
                $"SlotListParamsRequest payload must be 1 byte, got {payload.Length}");
        }
        return new SlotListParamsRequest(payload[0]);
    }
}

/// <summary>
/// One element of a parameter-list reply. Mirrors the C++ ParamInfo struct
/// and the wire-spec layout (u32 paramId, u32+utf8 name, u32+utf8 units,
/// f64 default, f64 current, i32 stepCount, u8 flags).
/// </summary>
public sealed record SlotParamWire(
    uint Id,
    string Name,
    string Units,
    double DefaultValue,
    double CurrentValue,
    int StepCount,
    byte Flags);

/// <summary>
/// Sidecar -> host parameter list. Status codes:
///   0 ok, 1 no-plugin-loaded, 5 other, 6 invalid-slot-index,
///   7 controller-unavailable.
/// On 0: paramCount + that many serialised parameters follow.
/// </summary>
public sealed record SlotParamListResult(
    byte SlotIdx,
    byte Status,
    System.Collections.Generic.IReadOnlyList<SlotParamWire> Parameters)
{
    public byte[] Encode()
    {
        var ms = new System.IO.MemoryStream();
        ms.WriteByte(SlotIdx);
        ms.WriteByte(Status);
        if (Status == 0)
        {
            Span<byte> u32 = stackalloc byte[4];
            Span<byte> f64 = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)Parameters.Count);
            ms.Write(u32);
            foreach (var p in Parameters)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(u32, p.Id);
                ms.Write(u32);
                SlotLoadPluginResult.WriteLengthPrefixedString(ms, p.Name);
                SlotLoadPluginResult.WriteLengthPrefixedString(ms, p.Units);
                BinaryPrimitives.WriteDoubleLittleEndian(f64, p.DefaultValue);
                ms.Write(f64);
                BinaryPrimitives.WriteDoubleLittleEndian(f64, p.CurrentValue);
                ms.Write(f64);
                BinaryPrimitives.WriteInt32LittleEndian(u32, p.StepCount);
                ms.Write(u32);
                ms.WriteByte(p.Flags);
            }
        }
        return ms.ToArray();
    }

    public static SlotParamListResult Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2)
        {
            throw new ArgumentException(
                "SlotParamListResult payload must be at least 2 bytes");
        }
        var slot = payload[0];
        var status = payload[1];
        if (status != 0)
        {
            return new SlotParamListResult(slot, status,
                System.Array.Empty<SlotParamWire>());
        }
        var cursor = 2;
        if (payload.Length < cursor + 4)
        {
            throw new ArgumentException(
                "SlotParamListResult: truncated paramCount");
        }
        var count = BinaryPrimitives.ReadUInt32LittleEndian(
            payload.Slice(cursor, 4));
        cursor += 4;
        var list = new System.Collections.Generic.List<SlotParamWire>(
            (int)count);
        for (uint i = 0; i < count; i++)
        {
            if (payload.Length < cursor + 4)
                throw new ArgumentException("truncated paramId");
            var id = BinaryPrimitives.ReadUInt32LittleEndian(
                payload.Slice(cursor, 4));
            cursor += 4;
            var name = SlotLoadPluginResult.ReadLengthPrefixedString(
                payload, ref cursor);
            var units = SlotLoadPluginResult.ReadLengthPrefixedString(
                payload, ref cursor);
            if (payload.Length < cursor + 8)
                throw new ArgumentException("truncated defaultValue");
            var defaultValue = BinaryPrimitives.ReadDoubleLittleEndian(
                payload.Slice(cursor, 8));
            cursor += 8;
            if (payload.Length < cursor + 8)
                throw new ArgumentException("truncated currentValue");
            var currentValue = BinaryPrimitives.ReadDoubleLittleEndian(
                payload.Slice(cursor, 8));
            cursor += 8;
            if (payload.Length < cursor + 4)
                throw new ArgumentException("truncated stepCount");
            var stepCount = BinaryPrimitives.ReadInt32LittleEndian(
                payload.Slice(cursor, 4));
            cursor += 4;
            if (payload.Length < cursor + 1)
                throw new ArgumentException("truncated flags");
            var flags = payload[cursor];
            cursor += 1;
            list.Add(new SlotParamWire(
                id, name, units, defaultValue, currentValue, stepCount, flags));
        }
        return new SlotParamListResult(slot, status, list);
    }
}

/// <summary>
/// Host -> sidecar set-parameter request. Payload: u8 slot + u32 paramId
/// + f64 normalized.
/// </summary>
public sealed record SlotSetParamRequest(byte SlotIdx, uint ParamId, double Normalized)
{
    public byte[] Encode()
    {
        var buf = new byte[1 + 4 + 8];
        buf[0] = SlotIdx;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(1, 4), ParamId);
        BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(5, 8), Normalized);
        return buf;
    }

    public static SlotSetParamRequest Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 13)
        {
            throw new ArgumentException(
                $"SlotSetParamRequest payload must be 13 bytes, got {payload.Length}");
        }
        var slot = payload[0];
        var id = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(1, 4));
        var v = BinaryPrimitives.ReadDoubleLittleEndian(payload.Slice(5, 8));
        return new SlotSetParamRequest(slot, id, v);
    }
}

/// <summary>
/// Sidecar -> host set-parameter result. Status codes:
///   0 ok, 1 no-plugin-loaded, 5 other, 6 invalid-slot-index,
///   7 controller-unavailable.
/// <see cref="ActualValue"/> carries the value the plugin actually accepted
/// after clamp/quantise (NaN on error).
/// </summary>
public sealed record SlotSetParamResult(
    byte SlotIdx, uint ParamId, byte Status, double ActualValue)
{
    public byte[] Encode()
    {
        var buf = new byte[1 + 4 + 1 + 8];
        buf[0] = SlotIdx;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(1, 4), ParamId);
        buf[5] = Status;
        BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(6, 8), ActualValue);
        return buf;
    }

    public static SlotSetParamResult Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 14)
        {
            throw new ArgumentException(
                $"SlotSetParamResult payload must be 14 bytes, got {payload.Length}");
        }
        var slot = payload[0];
        var id = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(1, 4));
        var status = payload[5];
        var v = BinaryPrimitives.ReadDoubleLittleEndian(payload.Slice(6, 8));
        return new SlotSetParamResult(slot, id, status, v);
    }
}

// =====================================================================
// Phase 3 GUI: native plugin editor windows (Linux X11 this wave).
// =====================================================================

/// <summary>Host -> sidecar request to open a slot's editor. Payload: u8 slot.</summary>
public sealed record SlotShowEditorRequest(byte SlotIdx)
{
    public byte[] Encode() => new[] { SlotIdx };

    public static SlotShowEditorRequest Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 1)
        {
            throw new ArgumentException(
                $"SlotShowEditorRequest payload must be 1 byte, got {payload.Length}");
        }
        return new SlotShowEditorRequest(payload[0]);
    }
}

/// <summary>
/// Sidecar -> host editor-show result. Status codes:
///   0 ok (Width/Height populated),
///   1 no-plugin-loaded,
///   2 plugin-has-no-editor,
///   3 platform-not-supported,
///   4 attach-failed,
///   5 other,
///   6 invalid-slot-index,
///   7 gui-thread-init-failed.
/// </summary>
public sealed record SlotShowEditorResult(
    byte SlotIdx, byte Status, uint Width, uint Height)
{
    public byte[] Encode()
    {
        if (Status == 0)
        {
            var buf = new byte[2 + 4 + 4];
            buf[0] = SlotIdx;
            buf[1] = Status;
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(2, 4), Width);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(6, 4), Height);
            return buf;
        }
        return new[] { SlotIdx, Status };
    }

    public static SlotShowEditorResult Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2)
        {
            throw new ArgumentException(
                "SlotShowEditorResult payload must be at least 2 bytes");
        }
        var slot   = payload[0];
        var status = payload[1];
        if (status == 0)
        {
            if (payload.Length != 2 + 4 + 4)
            {
                throw new ArgumentException(
                    "SlotShowEditorResult ok payload must be 10 bytes");
            }
            var w = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(2, 4));
            var h = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(6, 4));
            return new SlotShowEditorResult(slot, 0, w, h);
        }
        return new SlotShowEditorResult(slot, status, 0, 0);
    }
}

/// <summary>Host -> sidecar request to close a slot's editor. Payload: u8 slot.</summary>
public sealed record SlotHideEditorRequest(byte SlotIdx)
{
    public byte[] Encode() => new[] { SlotIdx };

    public static SlotHideEditorRequest Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 1)
        {
            throw new ArgumentException(
                $"SlotHideEditorRequest payload must be 1 byte, got {payload.Length}");
        }
        return new SlotHideEditorRequest(payload[0]);
    }
}

/// <summary>
/// Sidecar -> host editor-hide result. Status codes:
///   0 ok, 1 no-editor-open, 5 other, 6 invalid-slot-index.
/// </summary>
public sealed record SlotHideEditorResult(byte SlotIdx, byte Status)
{
    public byte[] Encode() => new[] { SlotIdx, Status };

    public static SlotHideEditorResult Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 2)
        {
            throw new ArgumentException(
                $"SlotHideEditorResult payload must be 2 bytes, got {payload.Length}");
        }
        return new SlotHideEditorResult(payload[0], payload[1]);
    }
}

/// <summary>
/// Sidecar -> host async event: the editor window for a slot was closed
/// (window-manager DELETE button or plugin-driven close). Not a reply
/// to any pending request — dispatch via event handler.
/// </summary>
public sealed record EditorClosedEvent(byte SlotIdx)
{
    public byte[] Encode() => new[] { SlotIdx };

    public static EditorClosedEvent Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 1)
        {
            throw new ArgumentException(
                $"EditorClosedEvent payload must be 1 byte, got {payload.Length}");
        }
        return new EditorClosedEvent(payload[0]);
    }
}

/// <summary>
/// Sidecar -> host async event: the plugin asked the host to resize its
/// editor window. Informational — the host doesn't need to reply.
/// </summary>
public sealed record EditorResizedEvent(byte SlotIdx, uint Width, uint Height)
{
    public byte[] Encode()
    {
        var buf = new byte[1 + 4 + 4];
        buf[0] = SlotIdx;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(1, 4), Width);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(5, 4), Height);
        return buf;
    }

    public static EditorResizedEvent Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 9)
        {
            throw new ArgumentException(
                $"EditorResizedEvent payload must be 9 bytes, got {payload.Length}");
        }
        var slot = payload[0];
        var w = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(1, 4));
        var h = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(5, 4));
        return new EditorResizedEvent(slot, w, h);
    }
}

/// <summary>
/// Sidecar -> host async event: a plugin's IComponentHandler reported
/// performEdit for a parameter — typically from an editor knob drag, but
/// can also be plugin-internal automation. Wave 7 / wire-spec tag 0x36.
/// Payload: u8 slot + u32 paramId + f64 normalizedValue (13 bytes).
/// </summary>
public sealed record ParamChangedEvent(byte SlotIdx, uint ParamId, double NormalizedValue)
{
    public byte[] Encode()
    {
        var buf = new byte[1 + 4 + 8];
        buf[0] = SlotIdx;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(1, 4), ParamId);
        BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(5, 8), NormalizedValue);
        return buf;
    }

    public static ParamChangedEvent Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 13)
        {
            throw new ArgumentException(
                $"ParamChangedEvent payload must be 13 bytes, got {payload.Length}");
        }
        var slot  = payload[0];
        var id    = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(1, 4));
        var value = BinaryPrimitives.ReadDoubleLittleEndian(payload.Slice(5, 8));
        return new ParamChangedEvent(slot, id, value);
    }
}

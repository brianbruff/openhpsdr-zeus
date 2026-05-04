// SPDX-License-Identifier: GPL-2.0-or-later
//
// VstHostEventFrame — server → client notification carrying a small
// VST-host event tag. The browser uses the tag to decide whether to
// re-fetch /api/plughost/state, refresh a single slot, or show a
// transient notification (e.g. sidecarExited).
//
// Wire format: [0x1A][utf8 payload, max 256 bytes]. No phase byte, no
// length prefix beyond what the WS message itself carries — the
// client-side decoder reads bytes 1..end as the UTF-8 string. Keeping
// the frame deliberately minimal: Wave 6a only needs the tag-and-args
// shape; richer per-event payloads can land in a v2 frame later without
// breaking older clients.

using System.Buffers;
using System.Text;

namespace Zeus.Contracts;

public readonly record struct VstHostEventFrame(string Event)
{
    public const int MinByteLength = 1;
    public const int MaxEventBytes = 256;
    public const int MaxByteLength = MinByteLength + MaxEventBytes;

    public void Serialize(IBufferWriter<byte> writer)
    {
        var bytes = string.IsNullOrEmpty(Event)
            ? Array.Empty<byte>()
            : Encoding.UTF8.GetBytes(Event);
        var trimmedLen = Math.Min(bytes.Length, MaxEventBytes);
        int total = MinByteLength + trimmedLen;

        var span = writer.GetSpan(total);
        span[0] = (byte)MsgType.VstHostEvent;
        if (trimmedLen > 0)
            bytes.AsSpan(0, trimmedLen).CopyTo(span.Slice(1));
        writer.Advance(total);
    }

    public int ByteLength
    {
        get
        {
            if (string.IsNullOrEmpty(Event)) return MinByteLength;
            var len = Encoding.UTF8.GetByteCount(Event);
            return MinByteLength + Math.Min(len, MaxEventBytes);
        }
    }

    public static VstHostEventFrame Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < MinByteLength)
            throw new InvalidDataException(
                $"VstHostEventFrame requires {MinByteLength} bytes, got {bytes.Length}");
        if (bytes[0] != (byte)MsgType.VstHostEvent)
            throw new InvalidDataException(
                $"expected VstHostEvent (0x{(byte)MsgType.VstHostEvent:X2}), got 0x{bytes[0]:X2}");
        var ev = bytes.Length > MinByteLength
            ? Encoding.UTF8.GetString(bytes.Slice(MinByteLength))
            : string.Empty;
        return new VstHostEventFrame(ev);
    }
}

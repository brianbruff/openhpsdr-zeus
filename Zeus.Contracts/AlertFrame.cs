using System.Buffers;
using System.Text;

namespace Zeus.Contracts;

/// <summary>
/// Alert frame carrying a kind byte (0 = SWR trip, others reserved) and a
/// UTF-8 message. Payload: [kind:u8][msgUtf8…]. Total length variable;
/// contract guarantees ≤ 256 bytes for the full frame so clients can
/// stack-allocate a decode buffer.
/// </summary>
/// <remarks>
/// Provenance: PRD FR-6 — SWR trip at 2.5:1 sustained 500 ms. The AlertFrame
/// is server-generated, sent once when the trip condition fires; clients
/// should not resend unless the user dismisses and re-triggers the fault.
/// </remarks>
public readonly record struct AlertFrame(AlertKind Kind, string Message)
{
    public const int MaxByteLength = 1 + 1 + 254; // type + kind + msg

    public void Serialize(IBufferWriter<byte> writer)
    {
        var msgBytes = Encoding.UTF8.GetBytes(Message);
        int totalLen = 1 + 1 + msgBytes.Length;
        if (totalLen > MaxByteLength)
            throw new InvalidOperationException($"AlertFrame message too long: {msgBytes.Length} bytes");

        var span = writer.GetSpan(totalLen);
        span[0] = (byte)MsgType.Alert;
        span[1] = (byte)Kind;
        msgBytes.CopyTo(span.Slice(2));
        writer.Advance(totalLen);
    }

    public static AlertFrame Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 2)
            throw new InvalidDataException($"AlertFrame requires ≥2 bytes, got {bytes.Length}");
        if (bytes[0] != (byte)MsgType.Alert)
            throw new InvalidDataException($"expected Alert (0x{(byte)MsgType.Alert:X2}), got 0x{bytes[0]:X2}");

        var kind = (AlertKind)bytes[1];
        var msg = Encoding.UTF8.GetString(bytes.Slice(2));
        return new AlertFrame(kind, msg);
    }
}

/// <summary>
/// Alert kind enum. Kind 0 = SWR trip, Kind 1 = TX timeout (MOX or TUN keyed
/// for &gt; 120 s). Additional kinds reserved for future protection events
/// (ADC overload, WS-drop-while-keyed, etc.).
/// </summary>
public enum AlertKind : byte
{
    SwrTrip = 0,
    TxTimeout = 1,
}

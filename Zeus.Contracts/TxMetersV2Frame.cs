using System.Buffers;
using System.Buffers.Binary;

namespace Zeus.Contracts;

// Extended TX-telemetry frame (v2). 81 bytes total:
//
//   [0x16] [fwdW:f32] [refW:f32] [swr:f32]
//          [micPk:f32] [micAv:f32]
//          [eqPk:f32]  [eqAv:f32]
//          [lvlrPk:f32][lvlrAv:f32][lvlrGr:f32]
//          [cfcPk:f32] [cfcAv:f32] [cfcGr:f32]
//          [compPk:f32][compAv:f32]
//          [alcPk:f32] [alcAv:f32] [alcGr:f32]
//          [outPk:f32] [outAv:f32]
//
// Compatible additive extension of TxMetersFrame (0x11): carries average
// readings alongside peak for every stage, plus CFC / COMP stages that v1
// omitted. The operator needs the average to judge level and the peak to
// catch clipping that hides inside the ~100 ms meter window.
//
// Deliberately does NOT use the 16-byte WireFormat header: TX meters fire
// at 10 Hz during key-down and a per-frame header would more than double
// the wire cost without adding anything the client needs.
//
// Level meters are dBFS (typically −60..0). The *Gr fields are dB of gain
// reduction (≥0, with 0 meaning "no reduction"); CFC and COMP stages sit
// at the WDSP silence sentinel (≈ −400 dBFS) until those stages are
// engaged, which the frontend treats as "bypassed" (P1.4).
public readonly record struct TxMetersV2Frame(
    float FwdWatts,
    float RefWatts,
    float Swr,
    float MicPk,
    float MicAv,
    float EqPk,
    float EqAv,
    float LvlrPk,
    float LvlrAv,
    float LvlrGr,
    float CfcPk,
    float CfcAv,
    float CfcGr,
    float CompPk,
    float CompAv,
    float AlcPk,
    float AlcAv,
    float AlcGr,
    float OutPk,
    float OutAv)
{
    public const int ByteLength = 1 + 4 * 20;

    public void Serialize(IBufferWriter<byte> writer)
    {
        var span = writer.GetSpan(ByteLength);
        span[0] = (byte)MsgType.TxMetersV2;
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(1, 4), FwdWatts);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(5, 4), RefWatts);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(9, 4), Swr);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(13, 4), MicPk);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(17, 4), MicAv);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(21, 4), EqPk);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(25, 4), EqAv);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(29, 4), LvlrPk);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(33, 4), LvlrAv);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(37, 4), LvlrGr);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(41, 4), CfcPk);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(45, 4), CfcAv);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(49, 4), CfcGr);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(53, 4), CompPk);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(57, 4), CompAv);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(61, 4), AlcPk);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(65, 4), AlcAv);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(69, 4), AlcGr);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(73, 4), OutPk);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(77, 4), OutAv);
        writer.Advance(ByteLength);
    }

    public static TxMetersV2Frame Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < ByteLength)
            throw new InvalidDataException($"TxMetersV2Frame requires {ByteLength} bytes, got {bytes.Length}");
        if (bytes[0] != (byte)MsgType.TxMetersV2)
            throw new InvalidDataException($"expected TxMetersV2 (0x{(byte)MsgType.TxMetersV2:X2}), got 0x{bytes[0]:X2}");
        return new TxMetersV2Frame(
            FwdWatts: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(1, 4)),
            RefWatts: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(5, 4)),
            Swr: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(9, 4)),
            MicPk: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(13, 4)),
            MicAv: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(17, 4)),
            EqPk: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(21, 4)),
            EqAv: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(25, 4)),
            LvlrPk: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(29, 4)),
            LvlrAv: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(33, 4)),
            LvlrGr: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(37, 4)),
            CfcPk: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(41, 4)),
            CfcAv: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(45, 4)),
            CfcGr: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(49, 4)),
            CompPk: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(53, 4)),
            CompAv: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(57, 4)),
            AlcPk: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(61, 4)),
            AlcAv: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(65, 4)),
            AlcGr: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(69, 4)),
            OutPk: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(73, 4)),
            OutAv: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(77, 4)));
    }
}

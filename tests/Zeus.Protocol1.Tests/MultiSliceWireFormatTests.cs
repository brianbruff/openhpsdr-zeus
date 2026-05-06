// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Buffers.Binary;
using Zeus.Contracts;
using Zeus.Protocol1.Discovery;

namespace Zeus.Protocol1.Tests;

/// <summary>
/// Phase-1 multi-slice (multi-RX) wire-format pins. Single-slice path must
/// stay bit-identical; multi-slice ON must populate per-slice NCO frequencies
/// in the RxFreq2/3/4 frames; the EP6 multi-RX parser layout must follow
/// (6 × nddc + 2) bytes per sample slot, matching mi0bot networkproto1.c.
/// </summary>
public class MultiSliceWireFormatTests
{
    private static ControlFrame.CcState BaseHl2() => new(
        VfoAHz: 14_200_000,
        Rate: HpsdrSampleRate.Rate48k,
        PreampOn: false,
        Atten: HpsdrAtten.Zero,
        RxAntenna: HpsdrAntenna.Ant1,
        Mox: false,
        EnableHl2Dither: false,
        Board: HpsdrBoardKind.HermesLite2);

    // ---- RxFreqN frequency payload uses VfoB/C/DHz (multi-slice path) ----

    [Fact]
    public void RxFreq_Always_Carries_VfoAHz()
    {
        Span<byte> cc = stackalloc byte[5];
        var s = BaseHl2() with { VfoBHz = 7_100_000, VfoCHz = 28_000_000 };
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.RxFreq, s);
        uint hz = BinaryPrimitives.ReadUInt32BigEndian(cc[1..5]);
        Assert.Equal((uint)s.VfoAHz, hz);
    }

    [Fact]
    public void RxFreq2_Carries_VfoBHz()
    {
        Span<byte> cc = stackalloc byte[5];
        var s = BaseHl2() with { VfoBHz = 7_100_000 };
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.RxFreq2, s);
        uint hz = BinaryPrimitives.ReadUInt32BigEndian(cc[1..5]);
        Assert.Equal(7_100_000u, hz);
    }

    [Fact]
    public void RxFreq3_Carries_VfoCHz()
    {
        Span<byte> cc = stackalloc byte[5];
        var s = BaseHl2() with { VfoCHz = 21_300_000 };
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.RxFreq3, s);
        uint hz = BinaryPrimitives.ReadUInt32BigEndian(cc[1..5]);
        Assert.Equal(21_300_000u, hz);
    }

    [Fact]
    public void RxFreq4_Carries_VfoDHz()
    {
        Span<byte> cc = stackalloc byte[5];
        var s = BaseHl2() with { VfoDHz = 28_500_000 };
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.RxFreq4, s);
        uint hz = BinaryPrimitives.ReadUInt32BigEndian(cc[1..5]);
        Assert.Equal(28_500_000u, hz);
    }

    // ---- Single-slice bit-identity (default field values) ----

    [Fact]
    public void DefaultCcState_Config_C4_Has_Only_DuplexBit()
    {
        // Config C4 = 0b00000100 (duplex always-on). NumReceiversMinusOne = 0
        // means single-RX, which is the pre-multi-slice default.
        Span<byte> cc = stackalloc byte[5];
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.Config, BaseHl2());
        Assert.Equal(0b00000100, cc[4]);
    }

    [Fact]
    public void DefaultCcState_RxFreq2_Defaults_To_Zero()
    {
        // VfoBHz default = 0; the test pins that an unset field doesn't
        // accidentally fall back to VfoAHz when the multi-slice path didn't
        // populate it. SnapshotState sets VfoBHz = VfoAHz to avoid this in
        // production; the encoder itself must honour the field as-is.
        Span<byte> cc = stackalloc byte[5];
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.RxFreq2, BaseHl2());
        uint hz = BinaryPrimitives.ReadUInt32BigEndian(cc[1..5]);
        Assert.Equal(0u, hz);
    }

    // ---- Protocol1Client SnapshotState — PS precedence + multi-slice gate ----

    [Fact]
    public void SnapshotState_MultiSlice_Off_Default_NumRxMinus1_Is_Zero()
    {
        using var c = new Protocol1Client();
        c.SetBoardKind(HpsdrBoardKind.HermesLite2);
        // Default: not enabled.
        var snap = c.SnapshotState();
        Assert.Equal(0, snap.NumReceiversMinusOne);
    }

    [Fact]
    public void SnapshotState_MultiSlice_Enabled_Sets_NumRxMinus1()
    {
        using var c = new Protocol1Client();
        c.SetBoardKind(HpsdrBoardKind.HermesLite2);
        c.SetVfoSliceHz(1, 7_100_000);
        c.SetVfoSliceHz(2, 21_300_000);
        c.SetMultiSlice(enabled: true, numActiveSlices: 3);
        var snap = c.SnapshotState();
        Assert.Equal(2, snap.NumReceiversMinusOne); // 3 - 1
        Assert.Equal(7_100_000, snap.VfoBHz);
        Assert.Equal(21_300_000, snap.VfoCHz);
    }

    [Fact]
    public void SnapshotState_MultiSlice_With_Mox_Plus_PsArmed_Falls_Back_To_PS_Layout()
    {
        // PS-precedence: PS+MOX must keep the 4-DDC PS layout regardless of
        // any pending multi-slice request. VfoB/C/D = VfoAHz so DDC2/DDC3
        // remain on TX freq for pscc.
        using var c = new Protocol1Client();
        c.SetBoardKind(HpsdrBoardKind.HermesLite2);
        c.SetVfoAHz(14_200_000);
        c.SetMultiSlice(enabled: true, numActiveSlices: 2);
        c.SetPsEnabled(true);
        c.SetMox(true);
        var snap = c.SnapshotState();
        Assert.Equal(3, snap.NumReceiversMinusOne); // 4 DDCs forced
        Assert.Equal(14_200_000, snap.VfoBHz);
        Assert.Equal(14_200_000, snap.VfoCHz);
        Assert.Equal(14_200_000, snap.VfoDHz);
    }

    [Fact]
    public void SnapshotState_NonHl2_MultiSlice_Request_Ignored()
    {
        using var c = new Protocol1Client();
        c.SetBoardKind(HpsdrBoardKind.Hermes);
        c.SetMultiSlice(enabled: true, numActiveSlices: 2);
        var snap = c.SnapshotState();
        Assert.Equal(0, snap.NumReceiversMinusOne);
    }

    [Fact]
    public void SetMultiSlice_NumActiveSlices_Of_One_Disables_Layout()
    {
        // Operator passes Enabled=true with N=1 — the wire layer treats this
        // as "off" so the gateware never sees nddc=0 (an undefined state).
        using var c = new Protocol1Client();
        c.SetBoardKind(HpsdrBoardKind.HermesLite2);
        c.SetMultiSlice(enabled: true, numActiveSlices: 1);
        var snap = c.SnapshotState();
        Assert.Equal(0, snap.NumReceiversMinusOne);
    }

    // ---- EP6 multi-RX parser layout pin ----

    [Theory]
    [InlineData(1, 8)]
    [InlineData(2, 14)]
    [InlineData(3, 20)]
    [InlineData(4, 26)]
    public void Hl2MultiRxBytesPerSlot_Follows_6N_Plus_2(int nddc, int expectedBytes)
    {
        Assert.Equal(expectedBytes, PacketParser.Hl2MultiRxBytesPerSlot(nddc));
    }

    [Theory]
    [InlineData(2, 36)]
    [InlineData(3, 25)] // 504 / 20 = 25.2 → 25
    [InlineData(4, 19)]
    public void Hl2MultiRxSamplesPerUsbFrame_Matches_504_Div_BytesPerSlot(int nddc, int expectedSlots)
    {
        Assert.Equal(expectedSlots, PacketParser.Hl2MultiRxSamplesPerUsbFrame(nddc));
    }

    [Fact]
    public void TryParseHl2MultiRxPacket_Demultiplexes_DdcStreams()
    {
        // Synthesise a minimal valid multi-RX packet (nddc=2): each DDC
        // carries a constant-amplitude tone that's distinguishable from the
        // other (DDC0 = 0x100000, DDC1 = -0x100000) so demux correctness is
        // checkable by sign.
        const int nddc = 2;
        int slotsPerUsb = PacketParser.Hl2MultiRxSamplesPerUsbFrame(nddc);
        int bytesPerSlot = PacketParser.Hl2MultiRxBytesPerSlot(nddc);

        var packet = new byte[1032];
        // Metis header: EF FE 01 06 + BE seq.
        packet[0] = 0xEF; packet[1] = 0xFE; packet[2] = 0x01; packet[3] = 0x06;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4, 4), 42u);

        for (int frame = 0; frame < 2; frame++)
        {
            int frameStart = 8 + frame * 512;
            packet[frameStart + 0] = 0x7F;
            packet[frameStart + 1] = 0x7F;
            packet[frameStart + 2] = 0x7F;
            // C&C echo: address 0 (no telemetry), no overload bits.
            packet[frameStart + 3] = 0x00;

            int payloadStart = frameStart + 8;
            for (int g = 0; g < slotsPerUsb; g++)
            {
                int slotOff = payloadStart + g * bytesPerSlot;
                // DDC0: I=+0x100000, Q=+0x100000  (positive)
                packet[slotOff + 0] = 0x10;
                packet[slotOff + 1] = 0x00;
                packet[slotOff + 2] = 0x00;
                packet[slotOff + 3] = 0x10;
                packet[slotOff + 4] = 0x00;
                packet[slotOff + 5] = 0x00;
                // DDC1: I=-0x100000, Q=-0x100000  (negative; high bit set)
                // 24-bit signed: -0x100000 = 0xF00000.
                packet[slotOff + 6] = 0xF0;
                packet[slotOff + 7] = 0x00;
                packet[slotOff + 8] = 0x00;
                packet[slotOff + 9] = 0xF0;
                packet[slotOff + 10] = 0x00;
                packet[slotOff + 11] = 0x00;
                // 2 mic bytes (ignored).
            }
        }

        int needed = 2 * PacketParser.Hl2MultiRxSamplesPerPacket(nddc);
        var ddc0 = new double[needed];
        var ddc1 = new double[needed];
        bool ok = PacketParser.TryParseHl2MultiRxPacket(
            packet, nddc, new[] { ddc0, ddc1 },
            out uint seq, out int samples,
            out _, out _, out _);
        Assert.True(ok);
        Assert.Equal(42u, seq);
        Assert.Equal(slotsPerUsb * 2, samples);

        // DDC0 samples must be positive; DDC1 must be negative.
        for (int i = 0; i < needed; i++)
        {
            Assert.True(ddc0[i] > 0, $"ddc0[{i}] expected positive, was {ddc0[i]}");
            Assert.True(ddc1[i] < 0, $"ddc1[{i}] expected negative, was {ddc1[i]}");
        }
    }
}

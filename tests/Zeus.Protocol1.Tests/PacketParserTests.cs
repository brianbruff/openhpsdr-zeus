// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Buffers.Binary;

namespace Zeus.Protocol1.Tests;

public class PacketParserTests
{
    [Fact]
    public void TryParsePacket_NoAinEcho_TelemetryDefault()
    {
        // FramingTests.BuildValidPacket leaves the C&C echo zero → C0 byte = 0x00,
        // addr = 0. That's the Mercury/Penelope-version slot, not AIN-bearing, so
        // telemetry should stay at its default zero value.
        byte[] packet = FramingTests.BuildValidPacket(1, new (int, int)[PacketParser.ComplexSamplesPerPacket]);
        var outBuf = new double[2 * PacketParser.ComplexSamplesPerPacket];

        bool ok = PacketParser.TryParsePacket(packet, outBuf, out _, out _, out var telemetry);
        Assert.True(ok);
        Assert.Equal(default, telemetry);
    }

    [Theory]
    // Addr 1 (C0=0x08): Ain0 at C1..C2, Ain1 at C3..C4 — HL2 exciter/temp + FWD pwr.
    // Addr 2 (C0=0x10): REV pwr at C1..C2, ADC0 bias at C3..C4.
    // Addr 3 (C0=0x18): ADC1 bias at C1..C2.
    [InlineData((byte)0x08, (ushort)0x1234, (ushort)0x5678)]
    [InlineData((byte)0x10, (ushort)0x00AB, (ushort)0xFFEE)]
    [InlineData((byte)0x18, (ushort)0x8000, (ushort)0x0001)]
    public void TryParsePacket_AinEcho_PopulatesTelemetry(byte c0, ushort ain0, ushort ain1)
    {
        byte[] packet = FramingTests.BuildValidPacket(42, new (int, int)[PacketParser.ComplexSamplesPerPacket]);

        // Inject echo on the SECOND USB frame (the last AIN-bearing slot wins —
        // so this also covers the "last wins" ordering we documented).
        int usbStart = 8 + 512;
        packet[usbStart + 3] = c0;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(usbStart + 4, 2), ain0);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(usbStart + 6, 2), ain1);

        var outBuf = new double[2 * PacketParser.ComplexSamplesPerPacket];
        bool ok = PacketParser.TryParsePacket(packet, outBuf, out _, out _, out var telemetry);

        Assert.True(ok);
        Assert.Equal(c0, telemetry.C0Address);
        Assert.Equal(ain0, telemetry.Ain0);
        Assert.Equal(ain1, telemetry.Ain1);
    }

    [Fact]
    public void TryParsePacket_BothFramesAinEchoes_LastWins()
    {
        byte[] packet = FramingTests.BuildValidPacket(7, new (int, int)[PacketParser.ComplexSamplesPerPacket]);

        // Frame 0 → addr 1, Frame 1 → addr 2. Parser should return frame-1 data.
        int f0 = 8;
        packet[f0 + 3] = 0x08;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(f0 + 4, 2), 0x1111);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(f0 + 6, 2), 0x2222);

        int f1 = 8 + 512;
        packet[f1 + 3] = 0x10;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(f1 + 4, 2), 0xAAAA);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(f1 + 6, 2), 0xBBBB);

        var outBuf = new double[2 * PacketParser.ComplexSamplesPerPacket];
        Assert.True(PacketParser.TryParsePacket(packet, outBuf, out _, out _, out var telemetry));

        Assert.Equal(0x10, telemetry.C0Address);
        Assert.Equal(0xAAAA, telemetry.Ain0);
        Assert.Equal(0xBBBB, telemetry.Ain1);
    }

    [Fact]
    public void TryParsePacket_NonAinAddress_LeavesTelemetryDefault()
    {
        // Addr 4 (C0 = 0x20) is Mercury-version / overload info, not AIN.
        byte[] packet = FramingTests.BuildValidPacket(3, new (int, int)[PacketParser.ComplexSamplesPerPacket]);
        int usbStart = 8 + 512;
        packet[usbStart + 3] = 0x20;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(usbStart + 4, 2), 0xDEAD);

        var outBuf = new double[2 * PacketParser.ComplexSamplesPerPacket];
        Assert.True(PacketParser.TryParsePacket(packet, outBuf, out _, out _, out var telemetry));
        Assert.Equal(default, telemetry);
    }

    [Fact]
    public void TryParsePacket_BothFramesClear_OverloadBitsZero()
    {
        byte[] packet = FramingTests.BuildValidPacket(1, new (int, int)[PacketParser.ComplexSamplesPerPacket]);
        var outBuf = new double[2 * PacketParser.ComplexSamplesPerPacket];

        Assert.True(PacketParser.TryParsePacket(packet, outBuf, out _, out _, out _, out byte bits));
        Assert.Equal(0, bits);
    }

    [Fact]
    public void TryParsePacket_FirstFrameSetsAdc0_BitZero()
    {
        byte[] packet = FramingTests.BuildValidPacket(1, new (int, int)[PacketParser.ComplexSamplesPerPacket]);
        int f0 = 8;
        packet[f0 + 4] = 0x01; // C1[0] — ADC0 overload
        var outBuf = new double[2 * PacketParser.ComplexSamplesPerPacket];

        Assert.True(PacketParser.TryParsePacket(packet, outBuf, out _, out _, out _, out byte bits));
        Assert.Equal(0x01, bits);
    }

    [Fact]
    public void TryParsePacket_SecondFrameSetsAdc1_BitOne()
    {
        byte[] packet = FramingTests.BuildValidPacket(1, new (int, int)[PacketParser.ComplexSamplesPerPacket]);
        int f1 = 8 + 512;
        packet[f1 + 5] = 0x01; // C2[0] — ADC1 overload
        var outBuf = new double[2 * PacketParser.ComplexSamplesPerPacket];

        Assert.True(PacketParser.TryParsePacket(packet, outBuf, out _, out _, out _, out byte bits));
        Assert.Equal(0x02, bits);
    }

    [Fact]
    public void TryParsePacket_BothFramesBothAdcs_AllBitsSet()
    {
        byte[] packet = FramingTests.BuildValidPacket(1, new (int, int)[PacketParser.ComplexSamplesPerPacket]);
        int f0 = 8;
        int f1 = 8 + 512;
        packet[f0 + 4] = 0x01;
        packet[f0 + 5] = 0x01;
        packet[f1 + 4] = 0x01;
        packet[f1 + 5] = 0x01;
        var outBuf = new double[2 * PacketParser.ComplexSamplesPerPacket];

        Assert.True(PacketParser.TryParsePacket(packet, outBuf, out _, out _, out _, out byte bits));
        Assert.Equal(0x03, bits);
    }

    [Fact]
    public void TryParsePacket_OverloadBitsOrAcrossFrames()
    {
        // ADC0 set on first frame only; ADC1 set on second frame only. Packet-level
        // result must report both bits.
        byte[] packet = FramingTests.BuildValidPacket(1, new (int, int)[PacketParser.ComplexSamplesPerPacket]);
        int f0 = 8;
        int f1 = 8 + 512;
        packet[f0 + 4] = 0x01;
        packet[f1 + 5] = 0x01;
        var outBuf = new double[2 * PacketParser.ComplexSamplesPerPacket];

        Assert.True(PacketParser.TryParsePacket(packet, outBuf, out _, out _, out _, out byte bits));
        Assert.Equal(0x03, bits);
    }

    [Fact]
    public void TryParsePacket_HighBitsOnC1AndC2_Ignored()
    {
        // Only bit 0 is the overload bit. Hermes IOx, TX-FIFO count, etc share
        // the byte — must not leak into our overload reading.
        byte[] packet = FramingTests.BuildValidPacket(1, new (int, int)[PacketParser.ComplexSamplesPerPacket]);
        int f0 = 8;
        packet[f0 + 4] = 0xFE; // everything except bit 0
        packet[f0 + 5] = 0xFE;
        var outBuf = new double[2 * PacketParser.ComplexSamplesPerPacket];

        Assert.True(PacketParser.TryParsePacket(packet, outBuf, out _, out _, out _, out byte bits));
        Assert.Equal(0, bits);
    }

    [Fact]
    public void TryParsePacket_AddressMask_IgnoresStatusBits()
    {
        // Low 3 bits of the echoed C0 carry PTT/DOT/DASH/ADC0-overload — they
        // must not perturb address decoding. C0 = 0x08 | 0x01 (PTT set) should
        // still be recognised as addr-1.
        byte[] packet = FramingTests.BuildValidPacket(3, new (int, int)[PacketParser.ComplexSamplesPerPacket]);
        int usbStart = 8 + 512;
        packet[usbStart + 3] = 0x08 | 0x01; // addr 1 + PTT
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(usbStart + 4, 2), 0x0042);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(usbStart + 6, 2), 0x0043);

        var outBuf = new double[2 * PacketParser.ComplexSamplesPerPacket];
        Assert.True(PacketParser.TryParsePacket(packet, outBuf, out _, out _, out var telemetry));

        Assert.Equal(0x09, telemetry.C0Address);
        Assert.Equal(0x0042, telemetry.Ain0);
        Assert.Equal(0x0043, telemetry.Ain1);
    }
}

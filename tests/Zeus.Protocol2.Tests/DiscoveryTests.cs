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

using System.Net;
using System.Net.NetworkInformation;
using Zeus.Protocol2.Discovery;

namespace Zeus.Protocol2.Tests;

public class DiscoveryTests
{
    private static readonly IPAddress FromIp = IPAddress.Parse("192.168.1.42");

    [Fact]
    public void Parses_OrionMkII_Reply()
    {
        var reply = BuildReply(new ReplyFields(
            Status: 0x02,
            Mac: new byte[] { 0x00, 0x1C, 0xC0, 0xDE, 0xCA, 0xFE },
            BoardId: 0x0A,
            ProtocolSupported: 38,
            CodeVersion: 21,
            NumReceivers: 2));

        Assert.True(ReplyParser.TryParse(reply, FromIp, out var radio));
        Assert.Equal(HpsdrBoardKind.OrionMkII, radio.Board);
        Assert.Equal(new PhysicalAddress(new byte[] { 0x00, 0x1C, 0xC0, 0xDE, 0xCA, 0xFE }), radio.Mac);
        Assert.Equal(FromIp, radio.Ip);
        Assert.Equal((byte)21, radio.FirmwareVersion);
        Assert.Equal("2.1", radio.FirmwareString);
        Assert.Equal((byte)38, radio.Details.ProtocolSupported);
        Assert.Equal((byte)2, radio.Details.NumReceivers);
        Assert.False(radio.Details.Busy);
    }

    [Fact]
    public void Parses_Orion_Reply()
    {
        var reply = BuildReply(new ReplyFields(
            Status: 0x02,
            Mac: new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 },
            BoardId: 0x05,
            ProtocolSupported: 36,
            CodeVersion: 19,
            NumReceivers: 1));

        Assert.True(ReplyParser.TryParse(reply, FromIp, out var radio));
        Assert.Equal(HpsdrBoardKind.Orion, radio.Board);
        Assert.Equal((byte)0x05, radio.Details.RawBoardId);
    }

    [Fact]
    public void Parses_Hermes_Reply()
    {
        var reply = BuildReply(new ReplyFields(
            Status: 0x02,
            Mac: new byte[] { 0x00, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE },
            BoardId: 0x01,
            CodeVersion: 31));

        Assert.True(ReplyParser.TryParse(reply, FromIp, out var radio));
        Assert.Equal(HpsdrBoardKind.Hermes, radio.Board);
        Assert.Equal("3.1", radio.FirmwareString);
    }

    [Fact]
    public void Detects_Busy_Status()
    {
        var reply = BuildReply(new ReplyFields(
            Status: 0x03,
            Mac: new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 },
            BoardId: 0x0A));

        Assert.True(ReplyParser.TryParse(reply, FromIp, out var radio));
        Assert.True(radio.Details.Busy);
    }

    [Fact]
    public void Formats_BetaVersion_When_NonZero()
    {
        var reply = BuildReply(new ReplyFields(
            Status: 0x02,
            Mac: new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 },
            BoardId: 0x0A,
            CodeVersion: 21,
            BetaVersion: 3));

        Assert.True(ReplyParser.TryParse(reply, FromIp, out var radio));
        Assert.Equal("2.1b3", radio.FirmwareString);
        Assert.Equal((byte)3, radio.Details.BetaVersion);
    }

    [Fact]
    public void Rejects_P1_Reply()
    {
        var p1 = new byte[24];
        p1[0] = 0xEF;
        p1[1] = 0xFE;
        p1[2] = 0x02;

        Assert.False(ReplyParser.TryParse(p1, FromIp, out _));
    }

    [Fact]
    public void Rejects_ShortPacket()
    {
        var tiny = new byte[10];
        Assert.False(ReplyParser.TryParse(tiny, FromIp, out _));
    }

    [Fact]
    public void Rejects_InvalidStatus()
    {
        var bad = new byte[24];
        bad[4] = 0x99;

        Assert.False(ReplyParser.TryParse(bad, FromIp, out _));
    }

    [Fact]
    public void Maps_UnknownBoardId_To_Unknown()
    {
        var reply = BuildReply(new ReplyFields(
            Status: 0x02,
            Mac: new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 },
            BoardId: 0x7F));

        Assert.True(ReplyParser.TryParse(reply, FromIp, out var radio));
        Assert.Equal(HpsdrBoardKind.Unknown, radio.Board);
        Assert.Equal((byte)0x7F, radio.Details.RawBoardId);
    }

    private sealed record ReplyFields(
        byte Status,
        byte[] Mac,
        byte BoardId,
        byte ProtocolSupported = 0,
        byte CodeVersion = 0,
        byte NumReceivers = 1,
        byte BetaVersion = 0,
        byte MercuryVersion0 = 0,
        byte MercuryVersion1 = 0,
        byte MercuryVersion2 = 0,
        byte MercuryVersion3 = 0,
        byte PennyVersion = 0,
        byte MetisVersion = 0);

    private static byte[] BuildReply(ReplyFields f)
    {
        var reply = new byte[24];
        reply[0] = 0x00;
        reply[1] = 0x00;
        reply[2] = 0x00;
        reply[3] = 0x00;
        reply[4] = f.Status;
        Array.Copy(f.Mac, 0, reply, 5, 6);
        reply[11] = f.BoardId;
        reply[12] = f.ProtocolSupported;
        reply[13] = f.CodeVersion;
        reply[14] = f.MercuryVersion0;
        reply[15] = f.MercuryVersion1;
        reply[16] = f.MercuryVersion2;
        reply[17] = f.MercuryVersion3;
        reply[18] = f.PennyVersion;
        reply[19] = f.MetisVersion;
        reply[20] = f.NumReceivers;
        reply[23] = f.BetaVersion;
        return reply;
    }
}

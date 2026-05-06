// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

using Xunit;
using Zeus.Contracts;
using Zeus.Protocol1.Discovery;
using Zeus.Server;

namespace Zeus.Server.Tests;

/// <summary>
/// <see cref="BoardCapabilitiesTable.For"/> dispatches every recognised
/// <see cref="HpsdrBoardKind"/> to the static fingerprint Thetis records
/// in <c>clsHardwareSpecific.cs</c>. Pinned per-board so a regression in
/// the table cannot silently flip a feature panel on or off.
///
/// Source-of-truth: <c>docs/references/protocol-1/thetis-board-matrix.md</c>.
/// </summary>
public class BoardCapabilitiesTableTests
{
    [Fact]
    public void Hermes_Class_Single_RX_LRSwap_PathIllustrator_On()
    {
        // Thetis clsHardwareSpecific.cs:87-121 — HERMES, ANAN-10, ANAN-10E,
        // ANAN-100, ANAN-100B all share these facts.
        foreach (var board in new[] {
            HpsdrBoardKind.Metis,
            HpsdrBoardKind.Hermes,
            HpsdrBoardKind.HermesII,
        })
        {
            var caps = BoardCapabilitiesTable.For(board);
            Assert.Equal(1, caps.RxAdcCount);
            Assert.False(caps.MkiiBpf);
            Assert.Equal(33, caps.AdcSupplyMv);
            Assert.True(caps.LrAudioSwap);
            Assert.False(caps.HasVolts);
            Assert.False(caps.HasAmps);
            Assert.False(caps.HasAudioAmplifier);
            Assert.True(caps.SupportsPathIllustrator);
            Assert.Equal(1, caps.MaxReceivers);
        }
    }

    [Fact]
    public void Angelia_DualAdc_HermesSupply_NoMkii()
    {
        // Thetis clsHardwareSpecific.cs:122-128 — ANAN-100D was the first
        // dual-ADC board but kept the 33 mV / no-MKII Hermes-class supply.
        var caps = BoardCapabilitiesTable.For(HpsdrBoardKind.Angelia);
        Assert.Equal(2, caps.RxAdcCount);
        Assert.False(caps.MkiiBpf);
        Assert.Equal(33, caps.AdcSupplyMv);
        Assert.False(caps.LrAudioSwap);
        Assert.True(caps.HasSteppedAttenuationRx2);
        Assert.True(caps.SupportsPathIllustrator);
        Assert.Equal(2, caps.MaxReceivers);
    }

    [Fact]
    public void Orion_DualAdc_HighPowerSupply_NoMkii()
    {
        // Thetis clsHardwareSpecific.cs:136-142 — ANAN-200D first 50 mV
        // board, still no MKII BPF.
        var caps = BoardCapabilitiesTable.For(HpsdrBoardKind.Orion);
        Assert.Equal(2, caps.RxAdcCount);
        Assert.False(caps.MkiiBpf);
        Assert.Equal(50, caps.AdcSupplyMv);
        Assert.True(caps.HasSteppedAttenuationRx2);
        Assert.True(caps.SupportsPathIllustrator);
        Assert.Equal(2, caps.MaxReceivers);
    }

    [Fact]
    public void OrionMkII_Saturn_Class_Defaults_Match_G2()
    {
        // 0x0A wire byte aliases G2 / 7000DLE / 8000DLE / G2-1K /
        // ANVELINA-PRO3 / Red Pitaya. Default fingerprint = G2-class.
        // Issue #218 will fan this out per operator-selected variant.
        var caps = BoardCapabilitiesTable.For(HpsdrBoardKind.OrionMkII);
        Assert.Equal(2, caps.RxAdcCount);
        Assert.True(caps.MkiiBpf);
        Assert.Equal(50, caps.AdcSupplyMv);
        Assert.True(caps.HasVolts);
        Assert.True(caps.HasAmps);
        Assert.True(caps.HasAudioAmplifier);
        Assert.True(caps.HasSteppedAttenuationRx2);
        Assert.False(caps.SupportsPathIllustrator);
        // Saturn-class hardware advertises 8 DDCs.
        Assert.Equal(8, caps.MaxReceivers);
    }

    [Fact]
    public void HermesC10_G2E_Hybrid_SingleRx_MkiiOn()
    {
        // ANAN-G2E (N1GP firmware) — Thetis clsHardwareSpecific.cs:129-135.
        // Hybrid: single RX + 33 mV supply (Hermes-class) but MKII BPF on
        // and Saturn-class telemetry / audio amp.
        var caps = BoardCapabilitiesTable.For(HpsdrBoardKind.HermesC10);
        Assert.Equal(1, caps.RxAdcCount);
        Assert.True(caps.MkiiBpf);
        Assert.Equal(33, caps.AdcSupplyMv);
        Assert.False(caps.LrAudioSwap);
        Assert.True(caps.HasVolts);
        Assert.True(caps.HasAmps);
        Assert.True(caps.HasAudioAmplifier);
        Assert.False(caps.HasSteppedAttenuationRx2); // single RX
        Assert.False(caps.SupportsPathIllustrator);
        Assert.Equal(1, caps.MaxReceivers); // single-RX hardware
    }

    [Fact]
    public void HermesLite2_Defaults_Are_Conservative()
    {
        // HL2 is mi0bot-territory in Zeus; no Alex, no telemetry, no
        // path illustrator. Single-RX so RX2 attenuation flag is moot.
        var caps = BoardCapabilitiesTable.For(HpsdrBoardKind.HermesLite2);
        Assert.Equal(1, caps.RxAdcCount);
        Assert.False(caps.MkiiBpf);
        Assert.False(caps.HasVolts);
        Assert.False(caps.HasAmps);
        Assert.False(caps.HasAudioAmplifier);
        Assert.False(caps.SupportsPathIllustrator);
        // HL2 protocol allows up to 4 DDCs via C4 bits [5:3] per
        // docs/references/protocol-1/hermes-lite2-protocol.md:478-485.
        Assert.Equal(4, caps.MaxReceivers);
    }

    [Fact]
    public void Unknown_FallsBackTo_UnknownDefaults()
    {
        var caps = BoardCapabilitiesTable.For(HpsdrBoardKind.Unknown);
        Assert.Same(BoardCapabilities.UnknownDefaults, caps);
    }

    [Theory]
    [MemberData(nameof(EveryBoardKind))]
    public void Every_BoardKind_Returns_Sane_Fingerprint(HpsdrBoardKind board)
    {
        // Exhaustiveness pin: every enum value gets a defined fingerprint
        // (no surprise nulls), with sensible bounds for the numeric fields.
        var caps = BoardCapabilitiesTable.For(board);
        Assert.NotNull(caps);
        Assert.InRange(caps.RxAdcCount, 1, 2);
        Assert.True(caps.AdcSupplyMv == 33 || caps.AdcSupplyMv == 50,
            $"{board} has unexpected ADC supply {caps.AdcSupplyMv} mV");
        // MaxReceivers ceiling: documented per-board values fall in [1, 8].
        Assert.InRange(caps.MaxReceivers, 1, 8);
    }

    public static IEnumerable<object[]> EveryBoardKind() =>
        Enum.GetValues<HpsdrBoardKind>().Select(b => new object[] { b });
}

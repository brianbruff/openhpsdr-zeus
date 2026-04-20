using Nereus.Protocol1.Discovery;

namespace Nereus.Protocol1.Tests;

public class N2adrBandsTests
{
    // Pin masks are raw OCrx values (bits 0..6 = pins 1..7), not yet shifted
    // into C2. See docs/prd/09-n2adr-bands.md §2 and Thetis setup.cs:14655-14699.
    [Theory]
    [InlineData(1_800_000,  0x01)]   // 160m lower edge
    [InlineData(1_999_999,  0x01)]   // 160m upper edge
    [InlineData(3_500_000,  0x42)]   // 80m lower edge
    [InlineData(3_750_000,  0x42)]   // 80m centre
    [InlineData(5_300_000,  0x44)]   // 60m lower edge
    [InlineData(7_200_000,  0x44)]   // 40m common park
    [InlineData(10_100_000, 0x48)]   // 30m lower edge
    [InlineData(14_200_000, 0x48)]   // 20m common SSB
    [InlineData(18_068_000, 0x50)]   // 17m lower edge
    [InlineData(21_200_000, 0x50)]   // 15m SSB
    [InlineData(24_890_000, 0x60)]   // 12m lower edge
    [InlineData(28_500_000, 0x60)]   // 10m SSB
    [InlineData(29_700_000, 0x60)]   // 10m upper edge
    public void N2adrBands_RxOcMask_ReturnsExpectedPinMask(long vfoHz, byte expected)
    {
        Assert.Equal(expected, N2adrBands.RxOcMask(vfoHz));
    }

    [Theory]
    [InlineData(0)]                   // DC
    [InlineData(500_000)]             // MW
    [InlineData(1_799_999)]           // just below 160m
    [InlineData(29_700_001)]          // just above 10m
    [InlineData(50_000_000)]          // 6m — no N2ADR LPF
    [InlineData(144_000_000)]         // 2m
    public void N2adrBands_RxOcMask_ZeroOutsideHf(long vfoHz)
    {
        Assert.Equal(0, N2adrBands.RxOcMask(vfoHz));
    }

    [Fact]
    public void ControlFrame_ConfigC2_EmitsShiftedN2adrMask_WhenEnabled()
    {
        // deskhpsdr writes output_buffer[C2] |= rxband->OCrx << 1
        // (old_protocol.c:2550). Verify the final wire byte for a 20m park.
        Span<byte> cc = stackalloc byte[5];
        var state = new ControlFrame.CcState(
            VfoAHz: 14_200_000,
            Rate: HpsdrSampleRate.Rate192k,
            PreampOn: false,
            Atten: HpsdrAtten.Zero,
            RxAntenna: HpsdrAntenna.Ant1,
            Mox: false,
            EnableHl2Dither: false,
            Board: HpsdrBoardKind.HermesLite2,
            HasN2adr: true);
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.Config, state);

        // 20m raw mask 0x48 → C2 = 0x48 << 1 = 0x90.
        Assert.Equal(0x90, cc[2]);
    }

    [Fact]
    public void ControlFrame_ConfigC2_IsZero_WhenN2adrDisabled()
    {
        Span<byte> cc = stackalloc byte[5];
        var state = new ControlFrame.CcState(
            VfoAHz: 14_200_000,
            Rate: HpsdrSampleRate.Rate192k,
            PreampOn: false,
            Atten: HpsdrAtten.Zero,
            RxAntenna: HpsdrAntenna.Ant1,
            Mox: false,
            EnableHl2Dither: false,
            Board: HpsdrBoardKind.HermesLite2,
            HasN2adr: false);
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.Config, state);
        Assert.Equal(0, cc[2]);
    }

    [Fact]
    public void ControlFrame_ConfigC2_IsZero_WhenBoardIsNotHl2()
    {
        // N2ADR is an HL2-only filter board. Setting HasN2adr on a bare
        // Hermes must not emit any OC pin bits.
        Span<byte> cc = stackalloc byte[5];
        var state = new ControlFrame.CcState(
            VfoAHz: 14_200_000,
            Rate: HpsdrSampleRate.Rate192k,
            PreampOn: false,
            Atten: HpsdrAtten.Zero,
            RxAntenna: HpsdrAntenna.Ant1,
            Mox: false,
            EnableHl2Dither: false,
            Board: HpsdrBoardKind.Hermes,
            HasN2adr: true);
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.Config, state);
        Assert.Equal(0, cc[2]);
    }
}

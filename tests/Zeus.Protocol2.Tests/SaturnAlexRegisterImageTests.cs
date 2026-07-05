// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus - OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using Xunit;
using Zeus.Contracts;

namespace Zeus.Protocol2.Tests;

// Golden net for the P3 RF_PATH Alex register images (relay fix). The three
// registers are the P2 alex0/alex1 wire words re-sliced exactly the way
// p2app's InHighPriority.c:170-208 slices them for saturnregisters.c:
//   TX-filter = alex0[31:16], TX-antenna = alex1[31:16],
//   RX = alex0[15:0] | alex1[15:0]<<16.
// Cross-validated against the live radio: p3app's P2-parity boot defaults on
// 40 m are TxFilter=0x0120, Rx=0x00100010, TxAnt=0x0120 — this composer must
// reproduce them bit-for-bit for the same inputs.
public class SaturnAlexRegisterImageTests
{
    private static (ushort TxFilter, uint Rx, ushort TxAnt) Compose(
        uint rxHz,
        bool xmit = false,
        uint? txHz = null,
        uint rx2Hz = 0,
        bool rx2Enabled = false,
        int txAnt = 1,
        bool hasTxAntennaRelays = false,
        int rxAnt = 1,
        int rxAux = 0,
        bool mkiiBpfRxSelect = false)
        => Protocol2Client.ComposeSaturnAlexRegisterImages(
            rxFreqHz: rxHz,
            rx2FreqHz: rx2Hz,
            rx2Enabled: rx2Enabled,
            txFreqHz: txHz ?? rxHz,
            xmit: xmit,
            txAntWire: txAnt,
            hasTxAntennaRelays: hasTxAntennaRelays,
            rxAntWire: rxAnt,
            rxAuxInput: rxAux,
            mkiiBpfRxSelect: mkiiBpfRxSelect,
            board: HpsdrBoardKind.OrionMkII);

    [Fact]
    public void Rx_40m_MatchesP3BootDefaults()
    {
        // 7.2 MHz receive, ANT1, no aux — the exact register images p3app
        // seeds at boot (verified live: rfAlexTxFilter=0x120, rfAlexRx=
        // 0x100010, rfAlexTxAntenna=0x120).
        var (txFilter, rx, txAnt) = Compose(7_200_000u);
        Assert.Equal(0x0120, txFilter);          // 40/60m LPF + ANT1
        Assert.Equal(0x00100010u, rx);           // 6-10 MHz BPF, RX1 + RX2 halves
        Assert.Equal(0x0120, txAnt);
    }

    [Fact]
    public void Tx_40m_SetsTrRelayAndRx2Ground()
    {
        var (txFilter, rx, txAnt) = Compose(7_200_000u, xmit: true);
        // TX word gains the T/R relay (Saturn bit 11).
        Assert.Equal(0x0920, txFilter);
        Assert.Equal(0x0920, txAnt);
        // RX2 half gains RX-ground-on-TX (Saturn RX bit 24).
        Assert.Equal(0x01100010u, rx);
    }

    [Fact]
    public void Rx_20m_SelectsBpfAndLpfForBand()
    {
        var (txFilter, rx, txAnt) = Compose(14_200_000u);
        Assert.Equal(0x0110, txFilter);          // 30/20m LPF + ANT1
        Assert.Equal(0x00020002u, rx);           // 10-22 MHz BPF both halves
        Assert.Equal(0x0110, txAnt);
    }

    [Fact]
    public void Rx_160m_SelectsBpfAndLpfForBand()
    {
        var (txFilter, rx, txAnt) = Compose(1_850_000u);
        Assert.Equal(0x0180, txFilter);          // 160m LPF + ANT1
        Assert.Equal(0x00400040u, rx);           // 1-2.5 MHz BPF both halves
        Assert.Equal(0x0180, txAnt);
    }

    [Fact]
    public void SplitTx_LpfFollowsTxFrequency()
    {
        // RX1 on 40 m, TX DUC on 20 m, keyed: the TX LPF must follow the TX
        // frequency (30/20m LPF) while the RX BPF stays on 40 m.
        var (txFilter, rx, _) = Compose(7_200_000u, xmit: true, txHz: 14_200_000u);
        Assert.Equal(0x0910, txFilter);          // 30/20m LPF + ANT1 + T/R relay
        Assert.Equal(0x0010u, rx & 0xFFFFu);     // RX1 half still 6-10 MHz BPF
    }

    [Fact]
    public void TxAntenna2_LandsInBothTxWordsWhenRelaysPresent()
    {
        var (txFilter, _, txAnt) = Compose(
            7_200_000u, xmit: true, txAnt: 2, hasTxAntennaRelays: true);
        Assert.Equal(0x0A20, txFilter);          // ANT2 (bit 9) + 40/60 LPF + T/R
        Assert.Equal(0x0A20, txAnt);
    }

    [Fact]
    public void RxAntenna2_OnlyMovesTheStateMuxedTxFilterWord()
    {
        // Receiving on ANT2: alex0's state-muxed antenna follows the RX pick;
        // alex1 (TX-antenna register) keeps the TX antenna (ANT1).
        var (txFilter, _, txAnt) = Compose(
            7_200_000u, rxAnt: 2, hasTxAntennaRelays: true);
        Assert.Equal(0x0220, txFilter);          // ANT2 while receiving
        Assert.Equal(0x0120, txAnt);             // TX antenna stays ANT1
    }

    [Fact]
    public void Rx2OnDifferentBand_GetsOwnBpfHalf()
    {
        var (_, rx, _) = Compose(
            7_200_000u, rx2Hz: 14_200_000u, rx2Enabled: true);
        Assert.Equal(0x0010u, rx & 0xFFFFu);         // RX1: 6-10 MHz BPF
        Assert.Equal(0x0002u, (rx >> 16) & 0xFFFFu); // RX2: 10-22 MHz BPF
    }
}

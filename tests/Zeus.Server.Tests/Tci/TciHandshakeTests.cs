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
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using Zeus.Contracts;
using Zeus.Server.Tci;

namespace Zeus.Server.Tests.Tci;

public class TciHandshakeTests
{
    [Fact]
    public void BuildHandshake_ContainsProtocolIdentification()
    {
        var state = CreateTestState();
        var handshake = TciHandshake.BuildHandshake(state, 192000, false, false, 50);

        Assert.Contains("protocol:ExpertSDR3,1.8;", handshake);
        Assert.Contains("device:Zeus;", handshake);
    }

    [Fact]
    public void BuildHandshake_StartsWithProtocol()
    {
        var state = CreateTestState();
        var handshake = TciHandshake.BuildHandshake(state, 192000, false, false, 50);

        Assert.StartsWith("protocol:ExpertSDR3,1.8;", handshake);
    }

    [Fact]
    public void BuildHandshake_EndsWithReady()
    {
        var state = CreateTestState();
        var handshake = TciHandshake.BuildHandshake(state, 192000, false, false, 50);

        Assert.EndsWith("ready;", handshake);
    }

    [Fact]
    public void BuildHandshake_IncludesCapabilities()
    {
        var state = CreateTestState();
        var handshake = TciHandshake.BuildHandshake(state, 192000, false, false, 50);

        Assert.Contains("receive_only:false;", handshake);
        Assert.Contains("trx_count:1;", handshake);
        Assert.Contains("channels_count:1;", handshake);
    }

    [Fact]
    public void BuildHandshake_IncludesFrequencyLimits()
    {
        var state = CreateTestState();
        var handshake = TciHandshake.BuildHandshake(state, 192000, false, false, 50);

        Assert.Contains("vfo_limits:0,61440000;", handshake);
    }

    [Fact]
    public void BuildHandshake_IfLimitsMatchSampleRate()
    {
        var state = CreateTestState();

        // 192 kHz
        var handshake192 = TciHandshake.BuildHandshake(state, 192000, false, false, 50);
        Assert.Contains("if_limits:-96000,96000;", handshake192);

        // 96 kHz
        var handshake96 = TciHandshake.BuildHandshake(state, 96000, false, false, 50);
        Assert.Contains("if_limits:-48000,48000;", handshake96);
    }

    [Fact]
    public void BuildHandshake_IncludesSampleRates()
    {
        var state = CreateTestState();
        var handshake = TciHandshake.BuildHandshake(state, 192000, false, false, 50);

        Assert.Contains("iq_samplerate:192000;", handshake);
        Assert.Contains("audio_samplerate:48000;", handshake);
    }

    [Fact]
    public void BuildHandshake_IncludesModulationsList()
    {
        var state = CreateTestState();
        var handshake = TciHandshake.BuildHandshake(state, 192000, false, false, 50);

        Assert.Contains("modulations_list:AM,SAM,DSB,LSB,USB,FM,CWL,CWU,DIGL,DIGU;", handshake);
    }

    [Fact]
    public void BuildHandshake_IncludesVfoAndMode()
    {
        var state = CreateTestState(vfoHz: 14074000, mode: RxMode.USB);
        var handshake = TciHandshake.BuildHandshake(state, 192000, false, false, 50);

        Assert.Contains("vfo:0,0,14074000;", handshake);
        Assert.Contains("vfo:0,1,14074000;", handshake);
        Assert.Contains("modulation:0,USB;", handshake);
        Assert.Contains("dds:0,14074000;", handshake);
    }

    [Fact]
    public void BuildHandshake_IncludesFilterBand()
    {
        var state = CreateTestState(filterLow: 150, filterHigh: 2850);
        var handshake = TciHandshake.BuildHandshake(state, 192000, false, false, 50);

        Assert.Contains("rx_filter_band:0,150,2850;", handshake);
    }

    [Fact]
    public void BuildHandshake_IncludesMoxState()
    {
        var state = CreateTestState();

        // MOX off
        var handshakeOff = TciHandshake.BuildHandshake(state, 192000, false, false, 50);
        Assert.Contains("trx:0,false;", handshakeOff);
        Assert.Contains("tx_enable:0,false;", handshakeOff);

        // MOX on
        var handshakeOn = TciHandshake.BuildHandshake(state, 192000, true, false, 50);
        Assert.Contains("trx:0,true;", handshakeOn);
        Assert.Contains("tx_enable:0,true;", handshakeOn);
    }

    [Fact]
    public void BuildHandshake_IncludesTuneState()
    {
        var state = CreateTestState();

        // TUN off
        var handshakeOff = TciHandshake.BuildHandshake(state, 192000, false, false, 50);
        Assert.Contains("tune:0,false;", handshakeOff);

        // TUN on
        var handshakeOn = TciHandshake.BuildHandshake(state, 192000, false, true, 50);
        Assert.Contains("tune:0,true;", handshakeOn);
        Assert.Contains("tx_enable:0,true;", handshakeOn); // TUN also sets tx_enable
    }

    [Fact]
    public void BuildHandshake_IncludesDrivePercent()
    {
        var state = CreateTestState();
        var handshake = TciHandshake.BuildHandshake(state, 192000, false, false, 75);

        Assert.Contains("drive:0,75;", handshake);
        Assert.Contains("tune_drive:0,75;", handshake);
    }

    [Fact]
    public void BuildHandshake_IncludesTxFrequency()
    {
        var state = CreateTestState(vfoHz: 7074000);
        var handshake = TciHandshake.BuildHandshake(state, 192000, false, false, 50);

        Assert.Contains("tx_frequency:7074000;", handshake);
    }

    [Fact]
    public void BuildHandshake_AllLinesSemicolonTerminated()
    {
        var state = CreateTestState();
        var handshake = TciHandshake.BuildHandshake(state, 192000, false, false, 50);

        var lines = handshake.Split(';', StringSplitOptions.RemoveEmptyEntries);
        // Every line should be non-empty (split removes the trailing semicolons)
        Assert.All(lines, line => Assert.False(string.IsNullOrWhiteSpace(line)));
    }

    [Fact]
    public void BuildHandshake_GoldenFileRegression()
    {
        // Golden-file test: exact byte sequence for handshake stability
        var state = new StateDto(
            Status: ConnectionStatus.Connected,
            Endpoint: "192.168.1.100:1024",
            VfoHz: 14074000,
            Mode: RxMode.USB,
            FilterLowHz: 150,
            FilterHighHz: 2850,
            SampleRate: 192000,
            AgcTopDb: 80.0,
            AttenDb: 0,
            Nr: new NrConfig(),
            ZoomLevel: 1,
            AutoAttEnabled: true,
            AttOffsetDb: 0,
            AdcOverloadWarning: false);

        var handshake = TciHandshake.BuildHandshake(state, 192000, false, false, 50);

        var expected = "protocol:ExpertSDR3,1.8;" +
                       "device:Zeus;" +
                       "receive_only:false;" +
                       "trx_count:1;" +
                       "channels_count:1;" +
                       "vfo_limits:0,61440000;" +
                       "if_limits:-96000,96000;" +
                       "modulations_list:AM,SAM,DSB,LSB,USB,FM,CWL,CWU,DIGL,DIGU;" +
                       "iq_samplerate:192000;" +
                       "audio_samplerate:48000;" +
                       "volume:0;" +
                       "mute:false;" +
                       "mon_volume:-20;" +
                       "mon_enable:false;" +
                       "dds:0,14074000;" +
                       "if:0,0,0;" +
                       "if:0,1,0;" +
                       "vfo:0,0,14074000;" +
                       "vfo:0,1,14074000;" +
                       "modulation:0,USB;" +
                       "rx_enable:0,true;" +
                       "split_enable:0,false;" +
                       "tx_enable:0,false;" +
                       "trx:0,false;" +
                       "tune:0,false;" +
                       "rx_mute:0,false;" +
                       "rx_filter_band:0,150,2850;" +
                       "drive:0,50;" +
                       "tune_drive:0,50;" +
                       "tx_frequency:14074000;" +
                       "ready;";

        Assert.Equal(expected, handshake);
    }

    private static StateDto CreateTestState(
        long vfoHz = 14200000,
        RxMode mode = RxMode.USB,
        int filterLow = 150,
        int filterHigh = 2850)
    {
        return new StateDto(
            Status: ConnectionStatus.Connected,
            Endpoint: "192.168.1.100:1024",
            VfoHz: vfoHz,
            Mode: mode,
            FilterLowHz: filterLow,
            FilterHighHz: filterHigh,
            SampleRate: 192000,
            AgcTopDb: 80.0,
            AttenDb: 0,
            Nr: new NrConfig(),
            ZoomLevel: 1,
            AutoAttEnabled: true,
            AttOffsetDb: 0,
            AdcOverloadWarning: false);
    }
}

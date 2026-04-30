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
using Zeus.Dsp;
using Zeus.Server;
using Xunit;

namespace Zeus.Server.Tests;

/// <summary>
/// PR-1 (configurable Meters Panel) — broadcast-encoding tests for the
/// new <c>RxMetersV2Frame</c> (0x19). The pipeline tick fetches a raw
/// <see cref="RxStageMeters"/> snapshot from the engine and converts it
/// to a wire frame via
/// <see cref="DspPipelineService.BuildRxMetersV2(in RxStageMeters, double)"/>,
/// adding the per-board cal offset to the dBm-scale fields only. These
/// tests exercise that helper directly so the encoding rule is verified
/// without a live WDSP engine or a WebSocket client.
/// </summary>
public class RxMetersBroadcastTests
{
    [Fact]
    public void BuildRxMetersV2_AppliesCalOffsetToDbmFieldsOnly()
    {
        // Stub raw meter snapshot — uncalibrated, mirroring what
        // WdspDspEngine.GetRxStageMeters returns.
        var raw = new RxStageMeters(
            SignalPk: -73.0f,
            SignalAv: -82.0f,
            AdcPk: -32.0f,
            AdcAv: -45.0f,
            AgcGain: 18.0f,
            AgcEnvPk: -68.0f,
            AgcEnvAv: -76.0f);
        const double calOffsetDb = 0.98;

        var frame = DspPipelineService.BuildRxMetersV2(raw, calOffsetDb);

        // Cal offset added to all dBm-scale fields (signal + AGC envelope).
        Assert.Equal(-73.0f + (float)calOffsetDb, frame.SignalPk);
        Assert.Equal(-82.0f + (float)calOffsetDb, frame.SignalAv);
        Assert.Equal(-68.0f + (float)calOffsetDb, frame.AgcEnvPk);
        Assert.Equal(-76.0f + (float)calOffsetDb, frame.AgcEnvAv);

        // dBFS (ADC) and signed-dB AGC gain pass through unmodified —
        // they are board-independent units.
        Assert.Equal(-32.0f, frame.AdcPk);
        Assert.Equal(-45.0f, frame.AdcAv);
        Assert.Equal(18.0f, frame.AgcGain);
    }

    [Fact]
    public void BuildRxMetersV2_PreservesNegativeAgcGain()
    {
        // RX AGC gain swings both ways: positive when boosting a weak
        // signal, negative when cutting a hot one. Confirm a negative
        // value passes through with sign intact (does NOT get flipped or
        // shifted by the cal offset).
        var raw = new RxStageMeters(
            SignalPk: -10f, SignalAv: -12f,
            AdcPk: -5f, AdcAv: -8f,
            AgcGain: -12.5f,
            AgcEnvPk: -10f, AgcEnvAv: -12f);
        var frame = DspPipelineService.BuildRxMetersV2(raw, calOffsetDb: 0.98);
        Assert.Equal(-12.5f, frame.AgcGain);
    }

    [Fact]
    public void BuildRxMetersV2_FromSilent_ProducesSentinelFrame()
    {
        // RxStageMeters.Silent has dBm fields at −200 and AgcGain at 0.
        // After cal: dBm fields shift by the offset; AgcGain stays 0.
        // The frontend's "<= -200 → bypassed" check still fires because
        // the post-cal value is well below the threshold.
        var frame = DspPipelineService.BuildRxMetersV2(RxStageMeters.Silent, calOffsetDb: 0.98);
        Assert.Equal(-200f + 0.98f, frame.SignalPk);
        Assert.Equal(-200f + 0.98f, frame.SignalAv);
        Assert.Equal(-200f, frame.AdcPk);
        Assert.Equal(-200f, frame.AdcAv);
        Assert.Equal(0f, frame.AgcGain);
        Assert.Equal(-200f + 0.98f, frame.AgcEnvPk);
        Assert.Equal(-200f + 0.98f, frame.AgcEnvAv);
    }

    [Fact]
    public void StreamingHub_BroadcastRxMetersV2_NoOpWhenNoClients()
    {
        // Mirrors TxMetersSwrTripTests's hub-without-clients pattern: the
        // broadcast path must short-circuit cleanly when no clients are
        // attached so the pipeline tick can call it unconditionally.
        var hub = new StreamingHub(new Microsoft.Extensions.Logging.Abstractions.NullLogger<StreamingHub>());
        var frame = new RxMetersV2Frame(-73f, -80f, -30f, -40f, 12f, -70f, -78f);

        // Should not throw with zero clients attached.
        hub.Broadcast(frame);
        Assert.Equal(0, hub.ClientCount);
    }
}

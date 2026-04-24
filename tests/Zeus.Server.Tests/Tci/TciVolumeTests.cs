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

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;
using Zeus.Server.Tci;

namespace Zeus.Server.Tests.Tci;

/// <summary>
/// Round-trip coverage for TCI volume / mon_volume wiring.
/// Exercises RadioService.SetRxAfGain (state mutation + clamping) and
/// verifies TciHandshake reflects the live value so TCI clients and the
/// web UI share a single source of truth.
/// </summary>
public class TciVolumeTests : IDisposable
{
    private readonly DspSettingsStore _dspStore;
    private readonly PaSettingsStore _paStore;
    private readonly RadioService _radio;

    public TciVolumeTests()
    {
        _dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance);
        _paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance);
        _radio = new RadioService(NullLoggerFactory.Instance, _dspStore, _paStore);
    }

    public void Dispose()
    {
        _radio.Dispose();
        _dspStore.Dispose();
        _paStore.Dispose();
    }

    [Fact]
    public void SetRxAfGain_DefaultIsZeroDb()
    {
        Assert.Equal(0.0, _radio.Snapshot().RxAfGainDb);
    }

    [Theory]
    [InlineData(-10.0)]
    [InlineData(0.0)]
    [InlineData(20.0)]
    [InlineData(-50.0)]
    public void SetRxAfGain_InRange_PersistsInSnapshot(double db)
    {
        _radio.SetRxAfGain(db);
        Assert.Equal(db, _radio.Snapshot().RxAfGainDb);
    }

    [Theory]
    [InlineData(-100.0, -50.0)]  // below minimum → clamps to -50
    [InlineData(30.0, 20.0)]     // above maximum → clamps to 20
    public void SetRxAfGain_OutOfRange_ClampsToBounds(double input, double expected)
    {
        _radio.SetRxAfGain(input);
        Assert.Equal(expected, _radio.Snapshot().RxAfGainDb);
    }

    [Theory]
    [InlineData(-10.0, "volume:-10;", "mon_volume:-10;")]
    [InlineData(0.0, "volume:0;", "mon_volume:0;")]
    [InlineData(20.0, "volume:20;", "mon_volume:20;")]
    public void SetRxAfGain_ThenBuildHandshake_ReflectsLiveGain(double db, string expectedVolume, string expectedMonVolume)
    {
        _radio.SetRxAfGain(db);
        var state = _radio.Snapshot();
        var handshake = TciHandshake.BuildHandshake(state, 192000, false, false, 50);

        Assert.Contains(expectedVolume, handshake);
        Assert.Contains(expectedMonVolume, handshake);
    }
}

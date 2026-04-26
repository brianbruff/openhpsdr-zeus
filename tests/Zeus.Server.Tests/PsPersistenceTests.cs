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
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;
using Xunit;

namespace Zeus.Server.Tests;

/// <summary>
/// PureSignal settings persistence — guards the round-3 fix where SetPs and
/// SetTwoTone silently failed to call _psStore.Upsert. After the fix, every
/// Set* path that mutates a persisted PS field must drop a doc into the
/// LiteDB-backed store. Master-arm flags (PsEnabled, TwoToneEnabled) are
/// intentionally NOT persisted — same operator-action discipline as MOX/TUN.
/// </summary>
public class PsPersistenceTests : IDisposable
{
    private readonly string _dbPath;

    public PsPersistenceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zeus-prefs-pstest-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private (RadioService radio, PsSettingsStore store) BuildRadioWithStore()
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance);
        var paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath);
        var psStore = new PsSettingsStore(NullLogger<PsSettingsStore>.Instance, _dbPath);
        var radio = new RadioService(
            loggerFactory, dspStore, paStore,
            filterPresetStore: null, txIqSource: null,
            preferredRadioStore: null, psStore: psStore);
        return (radio, psStore);
    }

    [Fact]
    public void SetPs_PersistsAutoFlag()
    {
        var (radio, store) = BuildRadioWithStore();

        // Operator picks Single mode — persisted Auto field flips false.
        radio.SetPs(new PsControlSetRequest(Enabled: false, Auto: false, Single: true));

        var entry = store.Get();
        Assert.NotNull(entry);
        Assert.False(entry!.Auto);
    }

    [Fact]
    public void SetTwoTone_PersistsFreq1Freq2Mag()
    {
        var (radio, store) = BuildRadioWithStore();

        radio.SetTwoTone(new TwoToneSetRequest(
            Enabled: false, Freq1: 750.0, Freq2: 2000.0, Mag: 0.4));

        var entry = store.Get();
        Assert.NotNull(entry);
        Assert.Equal(750.0, entry!.TwoToneFreq1);
        Assert.Equal(2000.0, entry.TwoToneFreq2);
        Assert.Equal(0.4, entry.TwoToneMag);
    }

    [Fact]
    public void SetTwoTone_PartialFields_PreservesUnsetTunings()
    {
        // Operator changes only freq1 — freq2 and mag should keep their
        // existing StateDto / persisted values, not flip to defaults.
        var (radio, store) = BuildRadioWithStore();

        // First seed: full set.
        radio.SetTwoTone(new TwoToneSetRequest(
            Enabled: false, Freq1: 800.0, Freq2: 2100.0, Mag: 0.45));
        // Second call: only freq1 supplied.
        radio.SetTwoTone(new TwoToneSetRequest(
            Enabled: false, Freq1: 850.0));

        var entry = store.Get();
        Assert.NotNull(entry);
        Assert.Equal(850.0, entry!.TwoToneFreq1);
        Assert.Equal(2100.0, entry.TwoToneFreq2);
        Assert.Equal(0.45, entry.TwoToneMag);
    }

    [Fact]
    public void SetPsAdvanced_PersistsTunings()
    {
        var (radio, store) = BuildRadioWithStore();

        radio.SetPsAdvanced(new PsAdvancedSetRequest(
            Ptol: true,
            AutoAttenuate: false,
            MoxDelaySec: 0.5,
            LoopDelaySec: 0.1,
            AmpDelayNs: 200.0,
            HwPeak: null,
            IntsSpiPreset: "8/512"));

        var entry = store.Get();
        Assert.NotNull(entry);
        Assert.True(entry!.Ptol);
        Assert.False(entry.AutoAttenuate);
        Assert.Equal(0.5, entry.MoxDelaySec);
        Assert.Equal(0.1, entry.LoopDelaySec);
        Assert.Equal(200.0, entry.AmpDelayNs);
        Assert.Equal("8/512", entry.IntsSpiPreset);
    }

    [Fact]
    public void SetPsFeedbackSource_PersistsSource()
    {
        var (radio, store) = BuildRadioWithStore();

        radio.SetPsFeedbackSource(new PsFeedbackSourceSetRequest(PsFeedbackSource.External));

        var entry = store.Get();
        Assert.NotNull(entry);
        Assert.Equal(PsFeedbackSource.External, entry!.Source);
    }

    [Fact]
    public void NewRadioService_RehydratesPersistedFields()
    {
        // Round-trip: write via one RadioService, restart, read via second.
        var (radio1, _) = BuildRadioWithStore();
        radio1.SetTwoTone(new TwoToneSetRequest(
            Enabled: false, Freq1: 900.0, Freq2: 2200.0, Mag: 0.42));
        radio1.SetPsAdvanced(new PsAdvancedSetRequest(
            MoxDelaySec: 0.35,
            IntsSpiPreset: "16/512"));
        radio1.SetPsFeedbackSource(new PsFeedbackSourceSetRequest(PsFeedbackSource.External));

        // Build a fresh RadioService against the same on-disk DB (same _dbPath).
        var (radio2, _) = BuildRadioWithStore();
        var snap = radio2.Snapshot();

        Assert.Equal(900.0, snap.TwoToneFreq1);
        Assert.Equal(2200.0, snap.TwoToneFreq2);
        Assert.Equal(0.42, snap.TwoToneMag);
        Assert.Equal(0.35, snap.PsMoxDelaySec);
        Assert.Equal("16/512", snap.PsIntsSpiPreset);
        Assert.Equal(PsFeedbackSource.External, snap.PsFeedbackSource);
        // Master-arm flag should NOT survive — TwoToneEnabled stays false on
        // every fresh session even if Enabled=true had been set previously.
        Assert.False(snap.TwoToneEnabled);
    }
}

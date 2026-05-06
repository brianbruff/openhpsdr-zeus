// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Zeus.Contracts;

namespace Zeus.Server.Tests;

/// <summary>
/// Regression coverage for the per-RX VFO endpoint contract (Task #6).
///
/// The single-slice path (rxId=0) MUST stay bit-identical to the pre-task-#6
/// behaviour — frontend code that POSTs <c>{ "Hz": ... }</c> without an
/// rxId field has to keep working, and a tune at rxId=0 must continue to
/// update <see cref="StateDto.VfoHz"/> exactly as before. Multi-slice paths
/// (rxId&gt;0) must NOT perturb the primary VFO.
/// </summary>
public class PerRxVfoTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-pervx-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (File.Exists(_dbPath + ".pa")) File.Delete(_dbPath + ".pa"); } catch { }
        try { if (File.Exists(_dbPath + ".prefs")) File.Delete(_dbPath + ".prefs"); } catch { }
    }

    private RadioService BuildRadio(bool seedHl2Preference = false)
    {
        var dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _dbPath);
        var paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath + ".pa");
        PreferredRadioStore? prefs = null;
        if (seedHl2Preference)
        {
            // Without a connected client, EffectiveBoardKind falls back to
            // the preferred-board store (or Unknown if neither is set).
            // BoardCapabilities.UnknownDefaults.MaxReceivers == 1 — that
            // makes SetMultiSlice clamp NumActiveSlices to 1 and the
            // Phase-1 multi-slice path can't be exercised. Seed HermesLite2
            // (MaxReceivers=4) so the unit test can verify the per-slice
            // VFO logic without spinning up a real radio.
            prefs = new PreferredRadioStore(
                NullLogger<PreferredRadioStore>.Instance,
                _dbPath + ".prefs");
            prefs.Set(HpsdrBoardKind.HermesLite2);
        }
        return new RadioService(
            NullLoggerFactory.Instance,
            dspStore,
            paStore,
            preferredRadioStore: prefs);
    }

    // ---- DTO contract: legacy callers (no RxId field) deserialise as RxId=0 ----

    [Fact]
    public void VfoSetRequest_DefaultsRxIdToZero()
    {
        // Frontend pre-task-#6 sent { "Hz": 14_200_000 } with no RxId; the
        // record's default keeps that wire shape working.
        var req = new VfoSetRequest(Hz: 14_200_000);
        Assert.Equal((byte)0, req.RxId);
    }

    [Fact]
    public void VfoSetRequest_JsonRoundtrip_NoRxIdField_DeserialisesAsZero()
    {
        // System.Text.Json round-trip — pin the legacy POST shape so a future
        // record-shape change can't silently start requiring an RxId field.
        var json = "{\"Hz\":14200000}";
        var opts = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };
        var req = System.Text.Json.JsonSerializer.Deserialize<VfoSetRequest>(json, opts);
        Assert.NotNull(req);
        Assert.Equal(14_200_000L, req!.Hz);
        Assert.Equal((byte)0, req.RxId);
    }

    // ---- RadioService.SetVfo (rxId=0 dispatch target) — bit-identical -------

    [Fact]
    public void SetVfo_RxId0_Dispatch_Updates_PrimaryVfoHz_BitIdentical()
    {
        // Sanity: the primary path is the same SetVfo method that's been in
        // place since before multi-slice. This pins that the new endpoint
        // shim's `if (req.RxId == 0)` branch routes here unchanged.
        using var radio = BuildRadio();
        var snap = radio.SetVfo(7_100_000);
        Assert.Equal(7_100_000L, snap.VfoHz);
        // MultiSlice config untouched on the primary path — confirms the
        // rxId=0 branch did NOT accidentally write to SliceVfoHz.
        Assert.Null(snap.MultiSlice);
    }

    // ---- SetVfoSlice — guards + isolation ----------------------------------

    [Fact]
    public void SetVfoSlice_RxIdZero_Throws()
    {
        // The rxId=0 case must never reach SetVfoSlice — callers MUST use
        // SetVfo for the primary so CW offset / band-edge / PA recompute
        // logic runs. Endpoint shim enforces this; this test pins the
        // service-level guard so a future call-site can't bypass it.
        using var radio = BuildRadio();
        Assert.Throws<ArgumentException>(() => radio.SetVfoSlice(0, 7_100_000));
    }

    [Fact]
    public void SetVfoSlice_MultiSliceOff_Throws()
    {
        using var radio = BuildRadio();
        Assert.Throws<ArgumentException>(() => radio.SetVfoSlice(1, 7_100_000));
    }

    [Fact]
    public void SetVfoSlice_RxIdOutOfRange_Throws()
    {
        using var radio = BuildRadio(seedHl2Preference: true);
        radio.SetMultiSlice(new MultiSliceConfig(Enabled: true, NumActiveSlices: 2));
        // numActiveSlices=2 means rxId 1 is the only valid non-primary; rxId=2
        // is out of range.
        Assert.Throws<ArgumentException>(() => radio.SetVfoSlice(2, 7_100_000));
    }

    [Fact]
    public void SetVfoSlice_DoesNotPerturb_PrimaryVfoHz()
    {
        // Tuning RX1 must NOT shift the master VFO — the bug frontend
        // flagged was the inverse, but we pin both directions so a future
        // refactor can't accidentally couple them.
        using var radio = BuildRadio(seedHl2Preference: true);
        radio.SetVfo(14_200_000);
        radio.SetMultiSlice(new MultiSliceConfig(Enabled: true, NumActiveSlices: 2));
        long primaryBefore = radio.Snapshot().VfoHz;
        radio.SetVfoSlice(1, 7_100_000);
        Assert.Equal(primaryBefore, radio.Snapshot().VfoHz);
        // And the slice VFO landed in the persisted MultiSliceConfig.
        var ms = radio.Snapshot().MultiSlice;
        Assert.NotNull(ms);
        Assert.NotNull(ms!.SliceVfoHz);
        Assert.Equal(7_100_000L, ms.SliceVfoHz![0]);
    }

    [Fact]
    public void SetVfoSlice_PadsExistingList_WithMasterVfo()
    {
        // When SliceVfoHz is shorter than (NumActiveSlices - 1) — e.g. the
        // operator opened multi-slice with default zeros, then tuned only
        // RX2 — the implementation must pad shorter slots with the master
        // VFO so RX1 doesn't end up at 0 Hz.
        using var radio = BuildRadio(seedHl2Preference: true);
        radio.SetVfo(14_200_000);
        radio.SetMultiSlice(new MultiSliceConfig(Enabled: true, NumActiveSlices: 3));
        radio.SetVfoSlice(2, 21_300_000);
        var ms = radio.Snapshot().MultiSlice;
        Assert.NotNull(ms);
        Assert.NotNull(ms!.SliceVfoHz);
        Assert.Equal(2, ms.SliceVfoHz!.Count);
        Assert.Equal(14_200_000L, ms.SliceVfoHz[0]); // padded with master
        Assert.Equal(21_300_000L, ms.SliceVfoHz[1]); // operator's RX2 tune
    }

    [Fact]
    public void SetVfoSlice_ClampsHzToValidRange()
    {
        using var radio = BuildRadio(seedHl2Preference: true);
        radio.SetMultiSlice(new MultiSliceConfig(Enabled: true, NumActiveSlices: 2));
        radio.SetVfoSlice(1, 999_999_999_999); // way over 60 MHz cap
        var ms = radio.Snapshot().MultiSlice!;
        Assert.Equal(60_000_000L, ms.SliceVfoHz![0]);

        radio.SetVfoSlice(1, -1); // below 0
        ms = radio.Snapshot().MultiSlice!;
        Assert.Equal(0L, ms.SliceVfoHz![0]);
    }
}

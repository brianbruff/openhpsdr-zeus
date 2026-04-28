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

using System.Net;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;
using Zeus.Dsp;
using Zeus.Protocol1;
using Zeus.Protocol1.Discovery;

namespace Zeus.Server;

public sealed class RadioService : IDisposable
{
    private const int DefaultHpsdrPort = 1024;

    private readonly object _sync = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RadioService> _log;
    private readonly DspSettingsStore _dspSettingsStore;
    private readonly PaSettingsStore _paStore;
    private readonly PreferredRadioStore? _preferredRadioStore;
    private readonly PsSettingsStore? _psStore;
    private readonly FilterPresetStore? _filterPresetStore;
    // Last-known preset name per mode, preserved across mode switches.
    // Accessed only from inside Mutate (under _sync) or at init.
    private readonly Dictionary<RxMode, string?> _lastPresetPerMode = new();
    // Last-commanded slider value in UI percent (0..100). Needed here because
    // the drive byte depends on three inputs — percent, per-band PA gain, and
    // global max-watts — any of which can change independently. When a band
    // edge is crossed or a PA setting is edited, we recompute without needing
    // to wait for the next SetDrive call.
    private int _drivePct;
    // Independent TUN drive %. When TUN is keyed, the recompute uses this in
    // place of _drivePct so the operator can pre-set a lower tune level (and
    // the same per-band PA gain gives equal watts at equal percentages). piHPSDR
    // default is 10 — a 0 default would be "press TUN, nothing happens".
    private int _tunePct = 10;
    // Which drive % the next frame uses. Latched via NotifyTunActive from
    // TxService whenever the MOX/TUN keying state changes so a drag on either
    // slider during a live TX picks the right source without polling.
    private bool _tunActive;

    private StateDto _state;

    // Latched MOX bit — populated via SetMox so the auto-ATT loop can pause
    // itself during TX without a service-locator pattern back to TxService.
    private bool _mox;

    private Protocol1Client? _activeClient;
    // True while DspPipelineService has a live Protocol2 client and no P1 is
    // active. Used to resolve the effective board kind for PA defaults when
    // the user is on a G2 MkII / Saturn (P2 discovery flow skips
    // Protocol1Client entirely).
    private bool _p2Active;
    private bool _preampOn;
    // Auto-ATT defaults on; the user baseline starts at 0 dB and the control
    // loop ramps _attOffsetDb up to 31 dB on observed ADC overloads (Thetis
    // console.cs:22167-22181). The old hard-coded 15 dB masked clipping but
    // cost 15 dB of sensitivity on quiet bands.
    private HpsdrAtten _atten = new(0);

    // Auto-ATT control-loop state. Mutated only under _sync or on the RX-thread
    // overload-event path (which also takes _sync before touching state).
    private int _attOffsetDb;
    private int _adcOverloadLevel;          // 0..5, Thetis-style "red lamp" counter
    private bool _overloadSeenInWindow;     // any overload since last tick
    private long _lastTickMs = long.MinValue;
    private int _lastAppliedEffectiveDb = -1;   // so the first send always fires

    // Auto-AGC control-loop state. Similar to Auto-ATT but adjusts AGC-T
    // instead of attenuator. Tracks signal strength meter readings.
    private double _agcOffsetDb;
    private long _lastAgcTickMs = long.MinValue;

    // 100 ms between 1-dB steps. Events arrive at ~1.2 kHz (192 kSps), so
    // without throttling the offset would saturate at 31 dB in ~30 ms. At 10 Hz
    // the full-range ramp takes ~3 s — matches Thetis' feel.
    private const int TickIntervalMs = 100;

    public event Action<StateDto>? StateChanged;
    public event Action<IProtocol1Client>? Connected;
    public event Action? Disconnected;
    // Fires whenever the effective PA snapshot changes (store edit, VFO band
    // crossing, drive slider). DspPipelineService consumes this to forward the
    // same snapshot into any live Protocol2Client (byte 345 / byte 1401 /
    // CmdGeneral[58]). RadioService pushes to the P1 client directly because
    // it owns _activeClient.
    public event Action<PaRuntimeSnapshot>? PaSnapshotChanged;
    // Fires on every MOX / TUN edge. P1 side is pushed directly via
    // ActiveClient?.SetMox; these events give DspPipelineService the hook it
    // needs to forward the same bit into a live Protocol2Client, which owns
    // its own CmdHighPriority byte 4.
    public event Action<bool>? MoxChanged;
    public event Action<bool>? TunActiveChanged;
    // Fires when the operator toggles the Mercury preamp. P1 path is pushed
    // directly via ActiveClient?.SetPreamp inside SetPreamp; this event lets
    // DspPipelineService mirror the same change into a live Protocol2Client
    // (CmdHighPriority byte 1403, bit 0 = RX0 preamp). Issue #126 — the P2
    // forwarding is the missing link that left the PRE button non-functional
    // on Angelia / ANAN-100D.
    public event Action<bool>? PreampChanged;

    // Shared TX IQ source threaded through Protocol1Client. TxAudioIngest
    // writes into the same instance; this is the seam between "mic arrived
    // over WS" and "EP2 packet got real IQ". When null the client falls back
    // to its internal test-tone generator (dev / tests without a hub).
    private readonly Zeus.Protocol1.ITxIqSource? _txIqSource;

    public RadioService(ILoggerFactory loggerFactory, DspSettingsStore dspSettingsStore, PaSettingsStore paStore, FilterPresetStore? filterPresetStore = null, Zeus.Protocol1.ITxIqSource? txIqSource = null, PreferredRadioStore? preferredRadioStore = null, PsSettingsStore? psStore = null)
    {
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<RadioService>();
        _dspSettingsStore = dspSettingsStore;
        _paStore = paStore;
        _preferredRadioStore = preferredRadioStore;
        _psStore = psStore;
        _filterPresetStore = filterPresetStore;
        _paStore.Changed += RecomputePaAndPush;
        if (_preferredRadioStore is not null)
            _preferredRadioStore.Changed += RecomputePaAndPush;
        _txIqSource = txIqSource;

        // Load persisted DSP settings from the store, or use defaults if not found
        var persistedNr = _dspSettingsStore.Get() ?? new NrConfig();
        // CFC — issue #123. Persisted globally; null on a fresh install or
        // legacy DB row falls back to the default-OFF baseline so the operator
        // sees no behaviour change unless they enable.
        var persistedCfc = _dspSettingsStore.GetCfc() ?? CfcConfig.Default;

        // Seed the last-preset cache from persisted store for all modes so
        // the first mode-switch in a session recalls the correct slot.
        if (filterPresetStore != null)
        {
            foreach (RxMode m in Enum.GetValues<RxMode>())
                _lastPresetPerMode[m] = filterPresetStore.GetLastSelectedPreset(m);
        }

        // Load persisted PS settings — operator's calibration tuning. Master
        // arm and cal-mode are deliberately NOT persisted (parity with MOX);
        // only the timing/preset/auto-att tuning is. PsHwPeak is left at the
        // P1 default; ConnectAsync / ConnectP2Async overrides per-radio.
        var ps = _psStore?.Get();

        _state = new(
            Status: ConnectionStatus.Disconnected,
            Endpoint: null,
            VfoHz: 14_200_000,
            Mode: RxMode.USB,
            FilterLowHz: 150,
            FilterHighHz: 2850,
            SampleRate: 192_000,
            AgcTopDb: 80.0,
            AttenDb: 0,
            Nr: persistedNr,
            ZoomLevel: 1,
            AutoAttEnabled: true,
            AttOffsetDb: 0,
            AdcOverloadWarning: false,
            // Zeus default filter (150/2850) maps to the seeded USB VAR1 slot.
            FilterPresetName: "VAR1",
            FilterAdvancedPaneOpen: filterPresetStore?.GetAdvancedPaneOpen() ?? false,
            // PS persisted fields (or DTO defaults when not persisted yet).
            // PsEnabled NOT persisted — always starts off each session.
            PsAuto: ps?.Auto ?? true,
            PsPtol: ps?.Ptol ?? false,
            PsAutoAttenuate: ps?.AutoAttenuate ?? true,
            PsMoxDelaySec: ps?.MoxDelaySec ?? 0.2,
            PsLoopDelaySec: ps?.LoopDelaySec ?? 0.0,
            PsAmpDelayNs: ps?.AmpDelayNs ?? 150.0,
            PsFeedbackSource: ps?.Source ?? PsFeedbackSource.Internal,
            PsIntsSpiPreset: ps?.IntsSpiPreset ?? "16/256",
            // Two-tone test generator dial-in. Defaults match pihpsdr / Thetis
            // (700/1900 Hz, 0.49 each — peak ~0.98 just under WDSP IQ clip).
            TwoToneFreq1: ps?.TwoToneFreq1 ?? 700.0,
            TwoToneFreq2: ps?.TwoToneFreq2 ?? 1900.0,
            TwoToneMag: ps?.TwoToneMag ?? 0.49,
            Cfc: persistedCfc);
    }

    /// <summary>
    /// Single-source-of-truth Upsert helper for the PS settings store. Reads
    /// the current StateDto snapshot and writes the full PsSettingsEntry so
    /// callers don't drop fields by writing only what they touched. Called
    /// from SetPs, SetPsAdvanced, SetPsFeedbackSource, and SetTwoTone.
    ///
    /// PsEnabled / TwoToneEnabled (master arm flags) and PsHwPeak (per-radio
    /// derived) are intentionally NOT in the entry — same operator-action
    /// discipline as MOX/TUN.
    /// </summary>
    private void PersistPsState()
    {
        if (_psStore is null) return;
        var snap = Snapshot();
        _psStore.Upsert(new PsSettingsEntry
        {
            Auto = snap.PsAuto,
            Ptol = snap.PsPtol,
            AutoAttenuate = snap.PsAutoAttenuate,
            MoxDelaySec = snap.PsMoxDelaySec,
            LoopDelaySec = snap.PsLoopDelaySec,
            AmpDelayNs = snap.PsAmpDelayNs,
            IntsSpiPreset = snap.PsIntsSpiPreset,
            Source = snap.PsFeedbackSource,
            TwoToneFreq1 = snap.TwoToneFreq1,
            TwoToneFreq2 = snap.TwoToneFreq2,
            TwoToneMag = snap.TwoToneMag,
        });
    }

    // Ribbon-visibility setter — frontend toggles via REST, server broadcasts
    // a StateDto so other browser tabs stay in sync.
    public StateDto SetFilterAdvancedPaneOpen(bool open)
    {
        _filterPresetStore?.SetAdvancedPaneOpen(open);
        Mutate(s => s with { FilterAdvancedPaneOpen = open });
        return Snapshot();
    }

    public IProtocol1Client? ActiveClient
    {
        get { lock (_sync) return _activeClient; }
    }

    /// <summary>
    /// True when any backend (P1 or P2) has a live connection. Needed by
    /// TxService's MOX / TUN interlock — a G2 on P2 has no ActiveClient
    /// (Protocol1Client is null) but still wants to accept TX requests.
    /// </summary>
    public bool IsConnected
    {
        get { lock (_sync) return _activeClient is not null || _p2Active; }
    }

    public StateDto Snapshot() { lock (_sync) return _state; }

    /// <summary>Current operator preamp toggle. PreampOn isn't on the
    /// StateDto wire format, so DspPipelineService reads it directly when
    /// it needs to push the value into a freshly-opened Protocol2Client
    /// (issue #126). Lock-safe so a connect-time read can't tear against
    /// a concurrent SetPreamp.</summary>
    public bool PreampOn { get { lock (_sync) return _preampOn; } }

    /// <summary>Effective RX step attenuator in dB — operator baseline
    /// (<see cref="StateDto.AttenDb"/>) plus any auto-ATT overload offset
    /// (<see cref="StateDto.AttOffsetDb"/>), clamped to 0..31. This is the
    /// value that lands on the wire (CmdHighPriority byte 1443 on P2;
    /// CC0=0x14 on P1). Exposed for DspPipelineService.ConnectP2Async so a
    /// fresh P2 client is initialised with the operator's current effective
    /// atten before its first CmdHighPriority emission.</summary>
    public int EffectiveAttenDb
    {
        get
        {
            lock (_sync)
                return Math.Clamp(_atten.ClampedDb + _attOffsetDb, HpsdrAtten.MinDb, HpsdrAtten.MaxDb);
        }
    }

    public async Task<StateDto> ConnectAsync(string endpoint, int sampleRate, CancellationToken ct = default)
    {
        if (!TryParseEndpoint(endpoint, out var ipEndpoint))
            throw new ArgumentException($"Invalid endpoint '{endpoint}'.", nameof(endpoint));

        var hpsdrRate = MapSampleRate(sampleRate);

        Protocol1Client? client;
        lock (_sync)
        {
            if (_activeClient is not null)
                throw new InvalidOperationException("Already connected. Disconnect first.");

            client = new Protocol1Client(
                _loggerFactory.CreateLogger<Protocol1Client>(),
                _txIqSource);
            client.AdcOverloadObserved += OnAdcOverload;
            _activeClient = client;
            _state = _state with
            {
                Status = ConnectionStatus.Connecting,
                Endpoint = endpoint,
                SampleRate = hpsdrRate.SampleRateHz(),
            };
            // Fresh connection — reset per-session auto-ATT state so a sticky
            // offset from a previous session doesn't leak onto new hardware.
            _attOffsetDb = 0;
            _adcOverloadLevel = 0;
            _overloadSeenInWindow = false;
            _lastTickMs = long.MinValue;
            _lastAppliedEffectiveDb = -1;
        }
        StateChanged?.Invoke(Snapshot());

        try
        {
            await client.ConnectAsync(ipEndpoint, ct).ConfigureAwait(false);
            await client.StartAsync(new StreamConfig(hpsdrRate, _preampOn, _atten), ct).ConfigureAwait(false);
            client.SetVfoAHz(Snapshot().VfoHz);

            Mutate(s => s with { Status = ConnectionStatus.Connected });
            _log.LogInformation("radio.connected endpoint={Ep} rate={Rate}", ipEndpoint, hpsdrRate);
            Connected?.Invoke(client);
            // Replay PA settings into the fresh client — drive byte, OC masks,
            // and (for P2 downstream) PA-enable. Without this the client sits
            // at the protocol defaults (drive=0, OC=0) until something else
            // moves.
            RecomputePaAndPush();
            return Snapshot();
        }
        catch
        {
            lock (_sync) { _activeClient = null; }
            await TearDownClientAsync(client).ConfigureAwait(false);
            Mutate(s => s with { Status = ConnectionStatus.Error, Endpoint = null });
            throw;
        }
    }

    public async Task<StateDto> DisconnectAsync(CancellationToken ct = default)
    {
        Protocol1Client? client;
        lock (_sync)
        {
            client = _activeClient;
            _activeClient = null;
        }

        if (client is not null)
        {
            client.AdcOverloadObserved -= OnAdcOverload;
            Disconnected?.Invoke();
            await TearDownClientAsync(client, ct).ConfigureAwait(false);
            _log.LogInformation("radio.disconnected");
        }

        Mutate(s => s with
        {
            Status = ConnectionStatus.Disconnected,
            Endpoint = null,
            AttOffsetDb = 0,
            AdcOverloadWarning = false,
        });
        return Snapshot();
    }

    public StateDto SetVfo(long hz)
    {
        long clamped = Math.Clamp(hz, 0L, 60_000_000L);
        long previous;
        lock (_sync) previous = _state.VfoHz;
        Mutate(s => s with { VfoHz = clamped });
        ActiveClient?.SetVfoAHz(clamped);
        // Band edge crossed? Per-band PA gain / OC bits may have swapped — push
        // the new snapshot before the next TX frame ships. Cheap when no
        // crossing occurred (same bytes re-pushed).
        if (BandUtils.FreqToBand(previous) != BandUtils.FreqToBand(clamped))
        {
            RecomputePaAndPush();
        }
        return Snapshot();
    }

    // Per-mode-family remembered filter magnitudes. Mode switching snapshots
    // the current abs-filter into the departing family's slot and restores the
    // target family's slot on entry — so FM→USB brings back the SSB width
    // the user was using, not the 5500-Hz FM stomp the old SignedFilterForMode
    // left behind (FM overrode f_low/f_high to ±5500, and on return to USB
    // the min-abs/max-abs recomputation collapsed the passband to (5500,5500),
    // killing audio).
    private sealed record FamilyFilter(int LoAbs, int HiAbs);
    private FamilyFilter _ssbFilter = new(150, 2850);
    private FamilyFilter _amFilter = new(0, 4000);
    private FamilyFilter _fmFilter = new(0, 5500);
    private FamilyFilter _cwFilter = new(0, 125);

    // TX-side per-family filter memory. Thetis stores a single TX filter Lo/Hi
    // (setup.cs:5029-5066); pihpsdr uses hardcoded per-mode shapes
    // (transmitter.c:2108-2211). Zeus mirrors the RX per-family model so the
    // operator's USB TX width survives an AM round-trip, and LSB/USB share
    // absolute values with sign flipped at apply time. Defaults track Thetis
    // stock: SSB 150-2850, AM/DSB 0-4000, FM 0-3000 (Thetis narrowest FM TX
    // is 3 kHz half-width), CW 0-150 (150 Hz around cw_pitch is plenty).
    private FamilyFilter _ssbTxFilter = new(150, 2850);
    private FamilyFilter _amTxFilter = new(0, 4000);
    private FamilyFilter _fmTxFilter = new(0, 3000);
    private FamilyFilter _cwTxFilter = new(0, 150);

    public StateDto SetMode(RxMode mode)
    {
        RxMode departingMode = default;
        string? departingPreset = null;
        Mutate(s =>
        {
            departingMode = s.Mode;
            departingPreset = s.FilterPresetName;

            // Save departing mode's preset name to the in-memory cache.
            _lastPresetPerMode[s.Mode] = s.FilterPresetName;

            // 1) Save current abs-filter into the mode we are LEAVING (RX + TX).
            int curLoAbs = Math.Min(Math.Abs(s.FilterLowHz), Math.Abs(s.FilterHighHz));
            int curHiAbs = Math.Max(Math.Abs(s.FilterLowHz), Math.Abs(s.FilterHighHz));
            StoreFamilyFilter(s.Mode, curLoAbs, curHiAbs);
            int curTxLoAbs = Math.Min(Math.Abs(s.TxFilterLowHz), Math.Abs(s.TxFilterHighHz));
            int curTxHiAbs = Math.Max(Math.Abs(s.TxFilterLowHz), Math.Abs(s.TxFilterHighHz));
            StoreTxFamilyFilter(s.Mode, curTxLoAbs, curTxHiAbs);

            // 2) Look up the target family's remembered filter (RX + TX).
            var fam = FamilyFilterFor(mode);
            var txFam = TxFamilyFilterFor(mode);

            // 3) Re-sign per target mode's sideband convention.
            var (lo, hi) = SignedFilterForMode(mode, fam.LoAbs, fam.HiAbs);
            var (txLo, txHi) = SignedFilterForMode(mode, txFam.LoAbs, txFam.HiAbs);

            // 4) Restore the last-known preset name for the incoming mode.
            _lastPresetPerMode.TryGetValue(mode, out var restoredPreset);
            return s with
            {
                Mode = mode,
                FilterLowHz = lo, FilterHighHz = hi,
                TxFilterLowHz = txLo, TxFilterHighHz = txHi,
                FilterPresetName = restoredPreset,
            };
        });

        // Persist the departing mode's last preset outside the lock.
        if (departingPreset != null)
            _filterPresetStore?.UpsertLastSelectedPreset(departingMode, departingPreset);

        return Snapshot();
    }

    public StateDto SetFilter(int lowHz, int highHz, string? presetName = null)
    {
        if (highHz < lowHz) (lowHz, highHz) = (highHz, lowHz);
        RxMode modeAtSet = RxMode.USB;
        Mutate(s =>
        {
            modeAtSet = s.Mode;
            if (presetName != null) _lastPresetPerMode[s.Mode] = presetName;
            return s with { FilterLowHz = lowHz, FilterHighHz = highHz, FilterPresetName = presetName };
        });
        if (presetName != null)
            _filterPresetStore?.UpsertLastSelectedPreset(modeAtSet, presetName);
        return Snapshot();
    }

    // TX bandpass filter setter. Signed pair like SetFilter — caller is
    // expected to have already re-signed positive (abs) values per the current
    // mode's sideband convention. DspPipelineService picks up the state-change
    // and forwards to the engine via IDspEngine.SetTxFilter.
    public StateDto SetTxFilter(int lowHz, int highHz)
    {
        if (highHz < lowHz) (lowHz, highHz) = (highHz, lowHz);
        Mutate(s => s with { TxFilterLowHz = lowHz, TxFilterHighHz = highHz });
        return Snapshot();
    }

    public IReadOnlyList<FilterPresetDto> GetFilterPresets(RxMode mode)
    {
        var defaults = FilterPresets.DefaultsForMode(mode);
        return defaults.Select(e =>
        {
            if (e.IsVar && _filterPresetStore != null)
            {
                var stored = _filterPresetStore.GetVarOverride(mode, e.SlotName);
                if (stored.HasValue)
                    return new FilterPresetDto(e.SlotName, e.Label, stored.Value.LowHz, stored.Value.HighHz, true);
            }
            return new FilterPresetDto(e.SlotName, e.Label, e.LowHz, e.HighHz, e.IsVar);
        }).ToList();
    }

    public StateDto SetFilterPresetOverride(RxMode mode, string slotName, int loHz, int hiHz)
    {
        if (slotName is not ("VAR1" or "VAR2"))
            throw new InvalidOperationException("Only VAR1 and VAR2 slots can be overridden.");
        _filterPresetStore?.UpsertVarOverride(mode, slotName, loHz, hiHz);
        return Snapshot();
    }

    public string[] GetFavoriteFilterSlots(RxMode mode)
    {
        return _filterPresetStore?.GetFavoriteSlots(mode) ?? new[] { "F6", "F5", "F4" };
    }

    public StateDto SetFavoriteFilterSlots(RxMode mode, string[] slotNames)
    {
        if (slotNames.Length > 3)
            throw new ArgumentException("Maximum 3 favorite slots allowed", nameof(slotNames));
        _filterPresetStore?.SetFavoriteSlots(mode, slotNames);
        return Snapshot();
    }

    private void StoreFamilyFilter(RxMode mode, int loAbs, int hiAbs)
    {
        var slot = new FamilyFilter(loAbs, hiAbs);
        switch (mode)
        {
            case RxMode.USB: case RxMode.LSB: case RxMode.DIGU: case RxMode.DIGL:
                _ssbFilter = slot; break;
            case RxMode.AM: case RxMode.SAM: case RxMode.DSB:
                _amFilter = slot; break;
            case RxMode.FM:
                _fmFilter = slot; break;
            case RxMode.CWL: case RxMode.CWU:
                _cwFilter = slot; break;
        }
    }

    private void StoreTxFamilyFilter(RxMode mode, int loAbs, int hiAbs)
    {
        var slot = new FamilyFilter(loAbs, hiAbs);
        switch (mode)
        {
            case RxMode.USB: case RxMode.LSB: case RxMode.DIGU: case RxMode.DIGL:
                _ssbTxFilter = slot; break;
            case RxMode.AM: case RxMode.SAM: case RxMode.DSB:
                _amTxFilter = slot; break;
            case RxMode.FM:
                _fmTxFilter = slot; break;
            case RxMode.CWL: case RxMode.CWU:
                _cwTxFilter = slot; break;
        }
    }

    private FamilyFilter TxFamilyFilterFor(RxMode mode) => mode switch
    {
        RxMode.USB or RxMode.LSB or RxMode.DIGU or RxMode.DIGL => _ssbTxFilter,
        RxMode.AM or RxMode.SAM or RxMode.DSB => _amTxFilter,
        RxMode.FM => _fmTxFilter,
        RxMode.CWL or RxMode.CWU => _cwTxFilter,
        _ => _ssbTxFilter,
    };

    private FamilyFilter FamilyFilterFor(RxMode mode) => mode switch
    {
        RxMode.USB or RxMode.LSB or RxMode.DIGU or RxMode.DIGL => _ssbFilter,
        RxMode.AM or RxMode.SAM or RxMode.DSB => _amFilter,
        RxMode.FM => _fmFilter,
        RxMode.CWL or RxMode.CWU => _cwFilter,
        _ => _ssbFilter,
    };

    private static (int low, int high) SignedFilterForMode(RxMode mode, int loAbs, int hiAbs)
    {
        return mode switch
        {
            RxMode.USB => (+loAbs, +hiAbs),
            RxMode.DIGU => (0, +hiAbs),
            RxMode.LSB => (-hiAbs, -loAbs),
            RxMode.DIGL => (-hiAbs, 0),
            RxMode.AM or RxMode.SAM or RxMode.DSB => (-hiAbs, +hiAbs),
            RxMode.FM => (-hiAbs, +hiAbs),
            RxMode.CWL or RxMode.CWU => (-hiAbs, +hiAbs),
            _ => (+loAbs, +hiAbs),
        };
    }

    public StateDto SetSampleRate(HpsdrSampleRate rate)
    {
        int hz = rate.SampleRateHz();
        Mutate(s => s with { SampleRate = hz });
        ActiveClient?.SetSampleRate(rate);
        return Snapshot();
    }

    public StateDto SetPreamp(bool on)
    {
        lock (_sync) _preampOn = on;
        // P1 path: Protocol1Client owns the bit; SetPreamp pushes the
        // updated CcState on the next outgoing frame. ActiveClient is
        // null on a P2 connection, so the PreampChanged event below is
        // what carries the bit into Protocol2Client (issue #126).
        ActiveClient?.SetPreamp(on);
        PreampChanged?.Invoke(on);
        return Snapshot();
    }

    public StateDto SetAttenuator(HpsdrAtten atten)
    {
        _atten = atten;
        Mutate(s => s with { AttenDb = atten.ClampedDb });
        // Honour any active auto-ATT offset when the user adjusts the baseline.
        // _lastAppliedEffectiveDb is invalidated so the new sum reaches the radio
        // even if it happens to equal the previous effective value.
        int effective;
        lock (_sync)
        {
            effective = Math.Clamp(_atten.ClampedDb + _attOffsetDb, HpsdrAtten.MinDb, HpsdrAtten.MaxDb);
            _lastAppliedEffectiveDb = effective;
        }
        ActiveClient?.SetAttenuator(new HpsdrAtten(effective));
        return Snapshot();
    }

    public StateDto SetAutoAtt(bool enabled)
    {
        lock (_sync)
        {
            if (_state.AutoAttEnabled == enabled) return _state;
            _state = _state with { AutoAttEnabled = enabled };
            if (!enabled)
            {
                // Turning auto off: stop accumulating overload counters so the
                // warning lamp doesn't linger and reset the offset to zero so
                // the hardware comes back to the user's baseline immediately.
                _attOffsetDb = 0;
                _adcOverloadLevel = 0;
                _overloadSeenInWindow = false;
                _state = _state with { AttOffsetDb = 0, AdcOverloadWarning = false };
                int baseline = _atten.ClampedDb;
                if (_lastAppliedEffectiveDb != baseline)
                {
                    _lastAppliedEffectiveDb = baseline;
                    ActiveClient?.SetAttenuator(_atten);
                }
            }
        }
        var snap = Snapshot();
        StateChanged?.Invoke(snap);
        return snap;
    }

    public StateDto SetAutoAgc(bool enabled)
    {
        lock (_sync)
        {
            if (_state.AutoAgcEnabled == enabled) return _state;
            _state = _state with { AutoAgcEnabled = enabled };
            if (!enabled)
            {
                // Turning auto off: reset the offset to zero so AGC-T returns
                // to the user's baseline immediately.
                _agcOffsetDb = 0.0;
                _lastAgcTickMs = long.MinValue;
                _state = _state with { AgcOffsetDb = 0.0 };
            }
            else
            {
                // Turning auto on: reset timer
                _lastAgcTickMs = long.MinValue;
            }
        }
        var snap = Snapshot();
        StateChanged?.Invoke(snap);
        return snap;
    }

    /// <summary>
    /// Auto-AGC control loop handler. Called periodically with RX meter readings
    /// (signal strength in dBm). Adjusts AgcOffsetDb to maintain optimal signal
    /// levels. Throttled to avoid excessive AGC adjustments.
    /// </summary>
    internal void HandleRxMeterForAutoAgc(double signalDbm, long nowMs)
    {
        bool changedOffset = false;
        double newOffset = 0.0;

        lock (_sync)
        {
            if (!_state.AutoAgcEnabled) return;
            if (_mox) return;   // Pause during TX

            // Throttle adjustments - check every 500ms (slower than Auto-ATT)
            if (_lastAgcTickMs != long.MinValue && nowMs - _lastAgcTickMs < 500)
                return;
            _lastAgcTickMs = nowMs;

            // Target range: -80 to -40 dBm (typical S-meter range for comfortable listening)
            // If signal is too strong (> -40 dBm), reduce AGC gain (negative offset)
            // If signal is too weak (< -80 dBm), increase AGC gain (positive offset)
            const double TargetHigh = -40.0;
            const double TargetLow = -80.0;
            const double StepDb = 2.0;  // Larger steps than Auto-ATT for more responsive AGC

            double currentAgcTop = _state.AgcTopDb + _agcOffsetDb;

            if (signalDbm > TargetHigh && _agcOffsetDb > -40.0)
            {
                // Signal too strong - reduce AGC gain
                _agcOffsetDb = Math.Max(-40.0, _agcOffsetDb - StepDb);
                changedOffset = true;
            }
            else if (signalDbm < TargetLow && _agcOffsetDb < 40.0)
            {
                // Signal too weak - increase AGC gain
                _agcOffsetDb = Math.Min(40.0, _agcOffsetDb + StepDb);
                changedOffset = true;
            }

            if (changedOffset)
            {
                _state = _state with { AgcOffsetDb = _agcOffsetDb };
                newOffset = _agcOffsetDb;
            }
        }

        if (changedOffset)
        {
            StateChanged?.Invoke(Snapshot());
            _log.LogDebug("auto-agc offset={Offset}dB signal={Signal}dBm", newOffset, signalDbm);
        }
    }

    // MOX is transient — it belongs on the wire (CcState.Mox → C0 LSB), not in
    // the persisted RX StateDto. TxService owns the latched bool that the UI
    // reads back; this method is the P1-side fan-out only. We also stash the
    // bit locally so the auto-ATT loop can pause itself during TX (Thetis
    // console.cs:22188 — TX uses its own TxAttenData path, not the RX ramp).
    public void SetMox(bool on)
    {
        lock (_sync) _mox = on;
        ActiveClient?.SetMox(on);
        MoxChanged?.Invoke(on);
    }

    // Drive is transient like MOX — latched on the Protocol1Client so the
    // DriveFilter register on the next outgoing frame carries it. We clamp
    // here rather than at the endpoint so every entry point (REST, future
    // CAT bridge, tests) gets the same range guarantee.
    public void SetDrive(int percent)
    {
        int clamped = Math.Clamp(percent, 0, 100);
        Interlocked.Exchange(ref _drivePct, clamped);
        RecomputePaAndPush();
    }

    // Independent TUN drive %. Applies on the very next frame if TUN is already
    // keyed; otherwise it sits until TxService flips _tunActive.
    public void SetTuneDrive(int percent)
    {
        int clamped = Math.Clamp(percent, 0, 100);
        Interlocked.Exchange(ref _tunePct, clamped);
        RecomputePaAndPush();
    }

    // TxService calls this on every MOX/TUN edge. Runs the same recompute the
    // drive-slider path uses so the drive byte on the wire always reflects the
    // just-applied keying state (Thetis PreviousPWR swap, `console.cs:30094`).
    public void NotifyTunActive(bool on)
    {
        lock (_sync) _tunActive = on;
        RecomputePaAndPush();
        TunActiveChanged?.Invoke(on);
    }

    // DspPipelineService calls this right after a P2 client is created so the
    // fresh connection sees the current PA snapshot without waiting for the
    // next state change.
    public void ReplayPaSnapshot() => RecomputePaAndPush();

    // Compute the current drive byte + OC masks + PA enable from _drivePct,
    // PaSettingsStore, and the current VFO band. Push to the active P1 client
    // and fire PaSnapshotChanged for the P2 forwarder. Called on:
    //   - SetDrive (slider moved)
    //   - SetVfo when the band changes
    //   - PaSettingsStore.Changed (user edited PA Settings)
    //   - Connected (push current snapshot to fresh client)
    private void RecomputePaAndPush()
    {
        var stateSnap = Snapshot();
        // PA config uses the effective board so the operator can pre-stage
        // PA Settings for a radio not yet connected; once a radio IS on the
        // wire, EffectiveBoardKind == ConnectedBoardKind (discovery wins).
        var cfg = _paStore.GetAll(EffectiveBoardKind);
        var bandName = BandUtils.FreqToBand(stateSnap.VfoHz);
        var bandCfg = bandName is not null
            ? cfg.Bands.FirstOrDefault(b => b.Band == bandName) ?? new PaBandSettingsDto(bandName)
            : new PaBandSettingsDto("unknown");

        bool tunActive;
        lock (_sync) tunActive = _tunActive;
        int activePct = tunActive
            ? Volatile.Read(ref _tunePct)
            : Volatile.Read(ref _drivePct);
        // Route through the per-board drive-profile so HL2's 4-bit drive
        // register is respected (bottom nibble ignored by gateware). See
        // Zeus.Server.RadioDriveProfile + docs/lessons/hl2-drive-byte-
        // quantization.md. Non-HL2 boards get the straight 8-bit math via
        // FullByteDriveProfile.
        var driveProfile = RadioDriveProfiles.For(ConnectedBoardKind);
        byte driveByte = driveProfile.EncodeDriveByte(activePct, bandCfg.PaGainDb, cfg.Global.PaMaxPowerWatts);
        bool paEnabled = cfg.Global.PaEnabled && !bandCfg.DisablePa;

        _log.LogInformation(
            "pa.recompute tunActive={Tun} pct={Pct} band={Band} gainDb={Gain:F2} maxW={Max} profile={Profile} -> byte={Byte} paEn={PaEn}",
            tunActive, activePct, bandName ?? "?", bandCfg.PaGainDb, cfg.Global.PaMaxPowerWatts, driveProfile.BoardLabel, driveByte, paEnabled);

        ActiveClient?.SetDriveByte(driveByte);
        ActiveClient?.SetOcMasks(bandCfg.OcTx, bandCfg.OcRx);

        PaSnapshotChanged?.Invoke(new PaRuntimeSnapshot(
            DriveByte: driveByte,
            OcTxMask: bandCfg.OcTx,
            OcRxMask: bandCfg.OcRx,
            PaEnabled: paEnabled));
    }

    // Back-compat shim for callers/tests that predate IRadioDriveProfile.
    // Runtime RecomputePaAndPush no longer goes through here — it uses the
    // per-board RadioDriveProfiles.For(board) dispatch so HL2's 4-bit drive
    // is quantised correctly. Keep this method as the 8-bit/full-byte math
    // for tests and anything else that wants the raw value.
    internal static byte ComputeDriveByte(int drivePct, double paGainDb, int maxWatts)
        => DriveByteMath.ComputeFullByte(drivePct, paGainDb, maxWatts);

    // Thetis "AGC Top" slider — max post-AGC gain in dB. Clamped to the
    // Thetis UI range (−20..120). DspPipelineService picks this up through the
    // StateChanged event and forwards it to the active engine.
    public StateDto SetAgcTop(double topDb)
    {
        double clamped = Math.Clamp(topDb, -20.0, 120.0);
        Mutate(s => s with { AgcTopDb = clamped });
        return Snapshot();
    }

    // Master RX AF gain in dB. −50 dB is effectively silent (0.003 linear),
    // 0 dB matches the fresh-open default, +20 dB is a 10× linear boost for
    // quiet signals. Range mirrors Thetis's ptbAF (console.cs:4312-4313:
    // tbAF.Minimum = -50, Maximum = 20).
    public StateDto SetRxAfGain(double db)
    {
        double clamped = Math.Clamp(db, -50.0, 20.0);
        Mutate(s => s with { RxAfGainDb = clamped });
        return Snapshot();
    }

    public StateDto SetNr(NrConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        Mutate(s => s with { Nr = cfg });

        // Persist the new DSP settings to the store
        _dspSettingsStore.Upsert(cfg);

        return Snapshot();
    }

    // Right-click popover save for NR2 (EMNR) post2 tunables. Merges only
    // the non-null fields onto the current NrConfig so the operator can edit
    // a single knob without disturbing siblings, then re-pushes the whole
    // block through SetNr to keep persistence and engine state in lock-step.
    public StateDto SetNr2Post2(Nr2Post2ConfigSetRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        var current = Snapshot().Nr ?? new NrConfig();
        var merged = current with
        {
            EmnrPost2Run = req.Post2Run ?? current.EmnrPost2Run,
            EmnrPost2Factor = req.Post2Factor ?? current.EmnrPost2Factor,
            EmnrPost2Nlevel = req.Post2Nlevel ?? current.EmnrPost2Nlevel,
            EmnrPost2Rate = req.Post2Rate ?? current.EmnrPost2Rate,
            EmnrPost2Taper = req.Post2Taper ?? current.EmnrPost2Taper,
        };
        return SetNr(merged);
    }

    // NR2 (EMNR) core algorithm selectors + Trained-method T1/T2. Same
    // null-merge pattern as SetNr2Post2: each absent field leaves the
    // persisted value untouched. Range-checks the enum-shaped fields so
    // an out-of-range value can't push WDSP into an undefined branch.
    public StateDto SetNr2Core(Nr2CoreConfigSetRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (req.GainMethod is int gm && (gm < 0 || gm > 3))
            throw new ArgumentException($"GainMethod must be 0..3, got {gm}", nameof(req));
        if (req.NpeMethod is int npm && (npm < 0 || npm > 2))
            throw new ArgumentException($"NpeMethod must be 0..2, got {npm}", nameof(req));

        var current = Snapshot().Nr ?? new NrConfig();
        var merged = current with
        {
            EmnrGainMethod = req.GainMethod ?? current.EmnrGainMethod,
            EmnrNpeMethod = req.NpeMethod ?? current.EmnrNpeMethod,
            EmnrAeRun = req.AeRun ?? current.EmnrAeRun,
            EmnrTrainT1 = req.TrainT1 ?? current.EmnrTrainT1,
            EmnrTrainT2 = req.TrainT2 ?? current.EmnrTrainT2,
        };
        return SetNr(merged);
    }

    // Right-click popover save for NR4 (SBNR) tunables — same merge-and-
    // re-push pattern as SetNr2Post2.
    public StateDto SetNr4(Nr4ConfigSetRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        var current = Snapshot().Nr ?? new NrConfig();
        var merged = current with
        {
            Nr4ReductionAmount = req.ReductionAmount ?? current.Nr4ReductionAmount,
            Nr4SmoothingFactor = req.SmoothingFactor ?? current.Nr4SmoothingFactor,
            Nr4WhiteningFactor = req.WhiteningFactor ?? current.Nr4WhiteningFactor,
            Nr4NoiseRescale = req.NoiseRescale ?? current.Nr4NoiseRescale,
            Nr4PostFilterThreshold = req.PostFilterThreshold ?? current.Nr4PostFilterThreshold,
            Nr4NoiseScalingType = req.NoiseScalingType ?? current.Nr4NoiseScalingType,
            Nr4Position = req.Position ?? current.Nr4Position,
        };
        return SetNr(merged);
    }

    // CFC (Continuous Frequency Compressor) — issue #123. The whole 10-band
    // config travels in one POST because the operator edits the panel as a
    // single table; the engine then re-pushes the whole profile to WDSP.
    // Mirrors the SetNr shape: validate, mutate state, persist, return
    // snapshot. DspPipelineService picks up the change-detect on the next
    // OnRadioStateChanged tick and pushes through to the engine.
    public StateDto SetCfc(CfcSetRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        var cfg = req.Config ?? throw new ArgumentException("Config required", nameof(req));
        if (cfg.Bands is null || cfg.Bands.Length != 10)
            throw new ArgumentException($"Bands must have exactly 10 entries; got {cfg.Bands?.Length ?? 0}", nameof(req));

        Mutate(s => s with { Cfc = cfg });
        _dspSettingsStore.Upsert(cfg);
        _log.LogInformation(
            "radio.setCfc enabled={Enabled} peq={Peq} preComp={Pre:F1}dB prePeq={PrePeq:F1}dB",
            cfg.Enabled, cfg.PostEqEnabled, cfg.PreCompDb, cfg.PrePeqDb);
        return Snapshot();
    }

    // ---------------- PureSignal ----------------
    // SetPs flips master arm and cal-mode in a single mutate so the engine
    // sees a consistent state when DspPipelineService.OnRadioStateChanged
    // fires.
    public StateDto SetPs(PsControlSetRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        Mutate(s => s with
        {
            PsEnabled = req.Enabled,
            PsAuto = req.Auto,
            PsSingle = req.Single,
        });
        // Persist Auto/Single change so the operator's cal-mode preference
        // sticks across restarts. (PsEnabled is the master arm — not
        // persisted; same discipline as MOX/TUN.)
        PersistPsState();
        return Snapshot();
    }

    public StateDto SetPsAdvanced(PsAdvancedSetRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        Mutate(s => s with
        {
            PsPtol = req.Ptol ?? s.PsPtol,
            PsAutoAttenuate = req.AutoAttenuate ?? s.PsAutoAttenuate,
            PsMoxDelaySec = req.MoxDelaySec ?? s.PsMoxDelaySec,
            PsLoopDelaySec = req.LoopDelaySec ?? s.PsLoopDelaySec,
            PsAmpDelayNs = req.AmpDelayNs ?? s.PsAmpDelayNs,
            PsHwPeak = req.HwPeak ?? s.PsHwPeak,
            PsIntsSpiPreset = req.IntsSpiPreset ?? s.PsIntsSpiPreset,
        });
        PersistPsState();
        return Snapshot();
    }

    /// <summary>
    /// Choose Internal vs External feedback antenna for PureSignal.
    /// Mutates StateDto; DspPipelineService.OnRadioStateChanged forwards
    /// the bool into the active Protocol2Client where it flips one alex0
    /// bit on the next CmdHighPriority. WDSP cal/iqc are unaffected — the
    /// HW-Peak slider stays shared across sources (matches pihpsdr/Thetis).
    /// </summary>
    public StateDto SetPsFeedbackSource(PsFeedbackSourceSetRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        Mutate(s => s with { PsFeedbackSource = req.Source });
        PersistPsState();
        return Snapshot();
    }

    /// <summary>
    /// Toggle the "Monitor PA output" view (issue #121). When on, AND PS is
    /// armed, AND PS has converged, DspPipelineService.Tick reads pixels
    /// from the PS-feedback analyzer instead of the post-CFIR TX analyzer
    /// so the operator sees the actual on-air RF rather than the
    /// predistorted baseband. Operator viewing preference — NOT persisted
    /// across sessions (same discipline as PsEnabled / MOX).
    /// </summary>
    public StateDto SetPsMonitor(PsMonitorSetRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        _log.LogInformation("setPsMonitor enabled={Enabled}", req.Enabled);
        Mutate(s => s with { PsMonitorEnabled = req.Enabled });
        return Snapshot();
    }

    public StateDto SetTwoTone(TwoToneSetRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        Mutate(s => s with
        {
            TwoToneEnabled = req.Enabled,
            TwoToneFreq1 = req.Freq1 ?? s.TwoToneFreq1,
            TwoToneFreq2 = req.Freq2 ?? s.TwoToneFreq2,
            TwoToneMag = req.Mag ?? s.TwoToneMag,
        });
        // Persist freq1/freq2/mag — operator tunings survive restart.
        // TwoToneEnabled (master arm) is NOT persisted; same operator-action
        // discipline as MOX/TUN.
        PersistPsState();
        return Snapshot();
    }

    // Update the live state to track PS read-back from the engine. Called by
    // TxMetersService at 10 Hz while PS is armed.
    public void UpdatePsLiveReadout(double feedbackLevel, byte calState, bool correcting)
    {
        Mutate(s => s with
        {
            PsFeedbackLevel = feedbackLevel,
            PsCalState = calState,
            PsCorrecting = correcting,
        });
    }

    /// <summary>
    /// Resolves the operator-correct PS hardware-peak default for the given
    /// protocol + board kind. Sources:
    ///   - P1 Hermes / ANAN-10/100/100D/200D / Hermes-II / 10E / 100B → 0.4072
    ///   - P2 OrionMkII (G2 / Saturn) → 0.6121
    ///   - P2 ANAN-7000 / 8000 (default P2) → 0.2899
    ///   - HermesLite2 (either protocol) → 0.233 (MI0BOT special, but only if
    ///     someone connects an HL2 — Brian's Hermes is original, not HL2)
    /// Source authority: Thetis clsHardwareSpecific.cs:295-318 +
    /// pihpsdr transmitter.c:1166-1179 NEW_DEVICE_SATURN.
    /// </summary>
    public static double ResolvePsHwPeak(bool isProtocol2, HpsdrBoardKind board) =>
        // Per-protocol switch shaped so the P1 follow-up (TODO(ps-p1)) can
        // wire HW-peak per-board too. P1 today is gated off in the frontend
        // but the engine still receives the right number on connect — keeps
        // Synthetic + tests deterministic.
        (isProtocol2, board) switch
        {
            // TODO(ps-p1): P1 path is deferred — only P2 is wired through to
            // Protocol2Client.SetPsFeedbackEnabled and the feedback pump.
            (false, HpsdrBoardKind.HermesLite2)              => 0.233,
            (false, _)                                        => 0.4072,
            (true,  HpsdrBoardKind.OrionMkII)                 => 0.6121,
            (true,  HpsdrBoardKind.HermesLite2)               => 0.233,
            (true,  _)                                        => 0.2899,
        };

    /// <summary>
    /// Apply a per-radio PS hardware-peak default to the StateDto. Called by
    /// DspPipelineService after a successful connect (P1 or P2) so the
    /// engine sees the correct curve scale before the operator arms PS.
    /// Doesn't fire StateChanged unless the value actually moves.
    /// </summary>
    public void ApplyPsHwPeakForConnection(bool isProtocol2, HpsdrBoardKind board)
    {
        double peak = ResolvePsHwPeak(isProtocol2, board);
        Mutate(s => s.PsHwPeak == peak ? s : s with { PsHwPeak = peak });
        _log.LogInformation(
            "radio.applyPsHwPeak proto={Proto} board={Board} peak={Peak:F4}",
            isProtocol2 ? "P2" : "P1", board, peak);
    }

    public StateDto SetZoom(int level)
    {
        // Accepts the full DSP range (1..16); Program.cs already range-checks
        // the HTTP payload against these same bounds. A prior powers-of-two
        // guard here silently rejected 3/5/6/7 with a 500, causing the
        // frontend slider (step=1, 1..8) to appear stuck after valid steps.
        if (level < SyntheticDspEngine.MinZoomLevel || level > SyntheticDspEngine.MaxZoomLevel)
            throw new ArgumentException(
                $"zoom level must be in [{SyntheticDspEngine.MinZoomLevel},{SyntheticDspEngine.MaxZoomLevel}]; got {level}",
                nameof(level));
        Mutate(s => s with { ZoomLevel = level });
        return Snapshot();
    }

    public void Dispose()
    {
        _paStore.Changed -= RecomputePaAndPush;
        try { DisconnectAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        catch { /* best-effort */ }
    }

    private void Mutate(Func<StateDto, StateDto> fn)
    {
        StateDto next;
        lock (_sync)
        {
            next = fn(_state);
            _state = next;
        }
        StateChanged?.Invoke(next);
    }

    // Used by DspPipelineService when a Protocol 2 radio connects or
    // disconnects. RadioService's _activeClient is P1-only; this is how
    // the shared state (Status, Endpoint, SampleRate) stays coherent for
    // the UI without growing a P2 client slot here.
    public void MarkProtocol2Connected(string endpoint, int sampleRateHz)
    {
        lock (_sync) _p2Active = true;
        Mutate(s => s with
        {
            Status = ConnectionStatus.Connected,
            Endpoint = endpoint,
            SampleRate = sampleRateHz,
        });
        // P2 is alive — PA defaults should reflect G2 / Orion class so the
        // operator sees realistic numbers when they open the PA panel.
        RecomputePaAndPush();
    }

    public void MarkProtocol2Disconnected()
    {
        lock (_sync) _p2Active = false;
        Mutate(s => s with
        {
            Status = ConnectionStatus.Disconnected,
            Endpoint = null,
        });
    }

    // Resolves the board class the PA settings UI/math should seed defaults
    // from. P1 client wins when present (its BoardKind comes from discovery);
    // bare P2 connections imply Orion MkII (ANAN G2 family); everything else
    // is Unknown and falls back to 0 dB (legacy percent→byte).
    public HpsdrBoardKind ConnectedBoardKind
    {
        get
        {
            lock (_sync)
            {
                if (_activeClient is not null) return _activeClient.BoardKind;
                if (_p2Active) return HpsdrBoardKind.OrionMkII;
                return HpsdrBoardKind.Unknown;
            }
        }
    }

    // Board used to seed PA defaults / power-math tables. Discovery is
    // authoritative when a radio is on the wire — an operator's explicit
    // pick can't override what the hardware actually is. Before first
    // connect, the stored preference takes over so the PA panel shows
    // sane values for the radio the operator is about to plug in.
    public HpsdrBoardKind EffectiveBoardKind
    {
        get
        {
            var connected = ConnectedBoardKind;
            if (connected != HpsdrBoardKind.Unknown) return connected;
            return _preferredRadioStore?.Get() ?? HpsdrBoardKind.Unknown;
        }
    }

    // Protocol1 → RadioService bridge. Runs on the RX thread at ~1.2 kHz;
    // hands off to HandleAdcOverload for the logic the tests can drive.
    private void OnAdcOverload(AdcOverloadStatus status) =>
        HandleAdcOverload(status, Environment.TickCount64);

    /// <summary>
    /// Port of Thetis' handleOverload (console.cs:22167) plus the
    /// <c>_adc_overload_level</c> counter (console.cs:22093-22113). Runs every
    /// incoming EP6 packet; applies at most one +1/-1 dB step per
    /// <see cref="TickIntervalMs"/> window so the ramp is bounded and matches
    /// Thetis' perceived rate.
    /// </summary>
    internal void HandleAdcOverload(AdcOverloadStatus status, long nowMs)
    {
        bool changedWarning = false;
        int? effectiveToApply = null;
        bool newWarning = false;
        int newOffset = 0;

        lock (_sync)
        {
            if (!_state.AutoAttEnabled) return;
            if (_mox) return;   // TX-side ATT is owned by a different code path

            if (status.AnyOverload) _overloadSeenInWindow = true;

            if (_lastTickMs == long.MinValue)
            {
                _lastTickMs = nowMs;
                return;
            }

            if (nowMs - _lastTickMs < TickIntervalMs) return;
            _lastTickMs = nowMs;

            bool seen = _overloadSeenInWindow;
            _overloadSeenInWindow = false;

            if (seen)
            {
                if (_attOffsetDb < 31) _attOffsetDb++;
                _adcOverloadLevel = Math.Min(5, _adcOverloadLevel + 2);
            }
            else
            {
                if (_attOffsetDb > 0) _attOffsetDb--;
                if (_adcOverloadLevel > 0) _adcOverloadLevel--;
            }

            int effective = Math.Clamp(_atten.ClampedDb + _attOffsetDb, HpsdrAtten.MinDb, HpsdrAtten.MaxDb);
            if (effective != _lastAppliedEffectiveDb)
            {
                _lastAppliedEffectiveDb = effective;
                effectiveToApply = effective;
            }

            bool warn = _adcOverloadLevel > 3;
            if (warn != _state.AdcOverloadWarning || _attOffsetDb != _state.AttOffsetDb)
            {
                _state = _state with { AttOffsetDb = _attOffsetDb, AdcOverloadWarning = warn };
                changedWarning = true;
                newWarning = warn;
                newOffset = _attOffsetDb;
            }
        }

        if (effectiveToApply is int eff)
        {
            ActiveClient?.SetAttenuator(new HpsdrAtten(eff));
        }
        if (changedWarning)
        {
            StateChanged?.Invoke(Snapshot());
            // Debug-level — at 10 Hz this would flood logs if promoted.
            _log.LogDebug("auto-att offset={Offset}dB warn={Warn}", newOffset, newWarning);
        }
    }

    private static async Task TearDownClientAsync(Protocol1Client client, CancellationToken ct = default)
    {
        try { await client.StopAsync(ct).ConfigureAwait(false); } catch { /* best-effort */ }
        try { await client.DisconnectAsync(ct).ConfigureAwait(false); } catch { /* best-effort */ }
        client.Dispose();
    }

    internal static bool TryParseEndpoint(string endpoint, out IPEndPoint result)
    {
        result = null!;
        if (string.IsNullOrWhiteSpace(endpoint)) return false;

        if (IPEndPoint.TryParse(endpoint, out var parsed))
        {
            result = parsed.Port == 0
                ? new IPEndPoint(parsed.Address, DefaultHpsdrPort)
                : parsed;
            return true;
        }

        if (IPAddress.TryParse(endpoint, out var addr))
        {
            result = new IPEndPoint(addr, DefaultHpsdrPort);
            return true;
        }

        return false;
    }

    private static HpsdrSampleRate MapSampleRate(int hz) => hz switch
    {
        48_000 => HpsdrSampleRate.Rate48k,
        96_000 => HpsdrSampleRate.Rate96k,
        192_000 => HpsdrSampleRate.Rate192k,
        384_000 => HpsdrSampleRate.Rate384k,
        _ => throw new ArgumentException($"Unsupported sample rate {hz}.", nameof(hz)),
    };
}

internal static class HpsdrSampleRateExtensions
{
    public static int SampleRateHz(this HpsdrSampleRate rate) => rate switch
    {
        HpsdrSampleRate.Rate48k => 48_000,
        HpsdrSampleRate.Rate96k => 96_000,
        HpsdrSampleRate.Rate192k => 192_000,
        HpsdrSampleRate.Rate384k => 384_000,
        _ => throw new ArgumentOutOfRangeException(nameof(rate), rate, null),
    };
}

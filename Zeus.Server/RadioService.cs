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
using Microsoft.Extensions.Logging;
using Zeus.Contracts;
using Zeus.Protocol1;

namespace Zeus.Server;

public sealed class RadioService : IDisposable
{
    private const int DefaultHpsdrPort = 1024;

    private readonly object _sync = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RadioService> _log;

    private StateDto _state = new(
        Status: ConnectionStatus.Disconnected,
        Endpoint: null,
        VfoHz: 14_200_000,
        Mode: RxMode.USB,
        FilterLowHz: 150,
        FilterHighHz: 2850,
        SampleRate: 192_000,
        AgcTopDb: 80.0,
        AttenDb: 0,
        Nr: new NrConfig(),
        ZoomLevel: 1,
        AutoAttEnabled: true,
        AttOffsetDb: 0,
        AdcOverloadWarning: false);

    // Latched MOX bit — populated via SetMox so the auto-ATT loop can pause
    // itself during TX without a service-locator pattern back to TxService.
    private bool _mox;

    private Protocol1Client? _activeClient;
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

    // 100 ms between 1-dB steps. Events arrive at ~1.2 kHz (192 kSps), so
    // without throttling the offset would saturate at 31 dB in ~30 ms. At 10 Hz
    // the full-range ramp takes ~3 s — matches Thetis' feel.
    private const int TickIntervalMs = 100;

    public event Action<StateDto>? StateChanged;
    public event Action<IProtocol1Client>? Connected;
    public event Action? Disconnected;

    // Shared TX IQ source threaded through Protocol1Client. TxAudioIngest
    // writes into the same instance; this is the seam between "mic arrived
    // over WS" and "EP2 packet got real IQ". When null the client falls back
    // to its internal test-tone generator (dev / tests without a hub).
    private readonly Zeus.Protocol1.ITxIqSource? _txIqSource;

    public RadioService(ILoggerFactory loggerFactory, Zeus.Protocol1.ITxIqSource? txIqSource = null)
    {
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<RadioService>();
        _txIqSource = txIqSource;
    }

    public IProtocol1Client? ActiveClient
    {
        get { lock (_sync) return _activeClient; }
    }

    public StateDto Snapshot() { lock (_sync) return _state; }

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
        Mutate(s => s with { VfoHz = clamped });
        ActiveClient?.SetVfoAHz(clamped);
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

    public StateDto SetMode(RxMode mode)
    {
        Mutate(s =>
        {
            // 1) Save current abs-filter into the mode we are LEAVING.
            int curLoAbs = Math.Min(Math.Abs(s.FilterLowHz), Math.Abs(s.FilterHighHz));
            int curHiAbs = Math.Max(Math.Abs(s.FilterLowHz), Math.Abs(s.FilterHighHz));
            StoreFamilyFilter(s.Mode, curLoAbs, curHiAbs);

            // 2) Look up the target family's remembered filter.
            var fam = FamilyFilterFor(mode);

            // 3) Re-sign per target mode's sideband convention.
            var (lo, hi) = SignedFilterForMode(mode, fam.LoAbs, fam.HiAbs);
            return s with { Mode = mode, FilterLowHz = lo, FilterHighHz = hi };
        });
        return Snapshot();
    }

    public StateDto SetFilter(int lowHz, int highHz)
    {
        if (highHz < lowHz) (lowHz, highHz) = (highHz, lowHz);
        Mutate(s => s with { FilterLowHz = lowHz, FilterHighHz = highHz });
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
        _preampOn = on;
        ActiveClient?.SetPreamp(on);
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

    // MOX is transient — it belongs on the wire (CcState.Mox → C0 LSB), not in
    // the persisted RX StateDto. TxService owns the latched bool that the UI
    // reads back; this method is the P1-side fan-out only. We also stash the
    // bit locally so the auto-ATT loop can pause itself during TX (Thetis
    // console.cs:22188 — TX uses its own TxAttenData path, not the RX ramp).
    public void SetMox(bool on)
    {
        lock (_sync) _mox = on;
        ActiveClient?.SetMox(on);
    }

    // Drive is transient like MOX — latched on the Protocol1Client so the
    // DriveFilter register on the next outgoing frame carries it. We clamp
    // here rather than at the endpoint so every entry point (REST, future
    // CAT bridge, tests) gets the same range guarantee.
    public void SetDrive(int percent)
    {
        int clamped = Math.Clamp(percent, 0, 100);
        ActiveClient?.SetDrive(clamped);
    }

    // Thetis "AGC Top" slider — max post-AGC gain in dB. Clamped to the
    // Thetis UI range (−20..120). DspPipelineService picks this up through the
    // StateChanged event and forwards it to the active engine.
    public StateDto SetAgcTop(double topDb)
    {
        double clamped = Math.Clamp(topDb, -20.0, 120.0);
        Mutate(s => s with { AgcTopDb = clamped });
        return Snapshot();
    }

    public StateDto SetNr(NrConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        Mutate(s => s with { Nr = cfg });
        return Snapshot();
    }

    public StateDto SetZoom(int level)
    {
        if (level is not (1 or 2 or 4 or 8))
            throw new ArgumentException($"zoom level must be 1, 2, 4, or 8; got {level}", nameof(level));
        Mutate(s => s with { ZoomLevel = level });
        return Snapshot();
    }

    public void Dispose()
    {
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
        Mutate(s => s with
        {
            Status = ConnectionStatus.Connected,
            Endpoint = endpoint,
            SampleRate = sampleRateHz,
        });
    }

    public void MarkProtocol2Disconnected()
    {
        Mutate(s => s with
        {
            Status = ConnectionStatus.Disconnected,
            Endpoint = null,
        });
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

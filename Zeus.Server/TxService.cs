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

using Zeus.Contracts;

namespace Zeus.Server;

public sealed class TxService
{
    private readonly RadioService _radio;
    private readonly DspPipelineService _pipeline;
    private readonly StreamingHub _hub;
    private readonly ILogger<TxService> _log;
    private readonly object _sync = new();
    private bool _moxOn;
    private bool _tunOn;
    private DateTime? _moxStartedAt;
    private DateTime? _tunStartedAt;

    public TxService(RadioService radio, DspPipelineService pipeline, StreamingHub hub, ILogger<TxService> log)
    {
        _radio = radio;
        _pipeline = pipeline;
        _hub = hub;
        _log = log;
    }

    public bool IsMoxOn { get { lock (_sync) return _moxOn; } }
    public bool IsTunOn { get { lock (_sync) return _tunOn; } }

    public DateTime? MoxStartedAt { get { lock (_sync) return _moxStartedAt; } }
    public DateTime? TunStartedAt { get { lock (_sync) return _tunStartedAt; } }

    // Test seams: drive the keyed-at timestamps directly from a unit test
    // without routing through TrySetMox/TrySetTun (which require an active
    // Protocol1 client). Only the TxMetersService timeout path reads them.
    internal void SetMoxStartedAtForTest(DateTime? t) { lock (_sync) _moxStartedAt = t; }
    internal void SetTunStartedAtForTest(DateTime? t) { lock (_sync) _tunStartedAt = t; }

    public bool TrySetMox(bool on, out string? error)
    {
        // FR-1 interlock: no TX unless connected. Band-legality check deferred to a follow-up.
        if (on && _radio.ActiveClient is null) { error = "not connected"; return false; }

        bool wasTunOn;
        lock (_sync)
        {
            if (_moxOn == on) { error = null; return true; }
            wasTunOn = _tunOn;
            if (on)
            {
                _tunOn = false;  // MOX-on preempts TUN (PRD FR-7 mutual-exclusion)
                _tunStartedAt = null;
                _moxStartedAt = DateTime.UtcNow;
            }
            else
            {
                _moxStartedAt = null;
            }
            _moxOn = on;
        }

        if (wasTunOn && on)
        {
            // TUN was up and MOX came on — drop the tune carrier before keying
            // the mic chain so we don't briefly sum both.
            _pipeline.SetTxTune(false);
        }

        // Order: mute RX before keying TX on MOX-on; reverse on MOX-off.
        // Engine handles the RXA/TXA pair atomically under its own lock.
        _pipeline.SetMox(on);
        _radio.SetMox(on);
        _log.LogInformation("tx.mox on={On}", on);
        error = null;
        return true;
    }

    public bool TrySetTun(bool on, out string? error)
    {
        // Same connect-interlock as MOX: no TX of any kind without an active
        // client (the HL2 PA-enable bit is gated on MOX, but TUN flips MOX on
        // via the engine, so we reject the precondition here for symmetry).
        if (on && _radio.ActiveClient is null) { error = "not connected"; return false; }

        bool wasMoxOn;
        lock (_sync)
        {
            if (_tunOn == on) { error = null; return true; }
            wasMoxOn = _moxOn;
            if (on)
            {
                _moxOn = false;  // TUN-on preempts MOX (PRD FR-7)
                _moxStartedAt = null;
                _tunStartedAt = DateTime.UtcNow;
            }
            else
            {
                _tunStartedAt = null;
            }
            _tunOn = on;
        }

        if (wasMoxOn && on)
        {
            // MOX was engaged and TUN is taking over — stop mic-driven TX first.
            _pipeline.SetMox(false);
            _radio.SetMox(false);
        }

        // TUN is a WDSP TXA post-gen tone that needs TXA running. Engage the
        // engine MOX (which flips RXA→TXA state) whenever TUN is on, without
        // flipping TxService._moxOn — we tracked that separately above.
        _pipeline.SetMox(on);
        _radio.SetMox(on);
        _pipeline.SetTxTune(on);
        _log.LogInformation("tx.tun on={On}", on);
        error = null;
        return true;
    }

    /// <summary>
    /// Trip both MOX and TUN for a protection alert (SWR, timeout, etc.).
    /// Emits an <see cref="AlertFrame"/> over WS so the UI can inform the operator.
    /// Operator must manually re-key. PRD FR-6.
    /// </summary>
    public void TryTripForAlert(AlertKind kind, string reason)
    {
        bool wasMoxOn, wasTunOn;
        lock (_sync)
        {
            wasMoxOn = _moxOn;
            wasTunOn = _tunOn;
            _moxOn = false;
            _tunOn = false;
            // Clear the keyed-at timestamps too — otherwise EvaluateTimeoutTrip
            // would keep re-firing against the stale start time after the trip.
            _moxStartedAt = null;
            _tunStartedAt = null;
        }

        if (wasMoxOn || wasTunOn)
        {
            _pipeline.SetMox(false);
            _radio.SetMox(false);
            if (wasTunOn) _pipeline.SetTxTune(false);
            _log.LogWarning("tx.trip kind={Kind} reason={Reason}", kind, reason);
            _hub.Broadcast(new AlertFrame(kind, reason));
        }
    }
}

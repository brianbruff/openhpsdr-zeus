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

namespace Zeus.Contracts;

public enum RxMode : byte
{
    LSB, USB, CWL, CWU, AM, FM, SAM, DSB, DIGL, DIGU,
}

public enum ConnectionStatus { Disconnected, Connecting, Connected, Error }

// Thetis NR-button state: Off = no spectral NR, Anr = NR1 (time-domain LMS),
// Emnr = NR2 (Ephraim–Malah spectral). ANR and EMNR are mutually exclusive
// in Thetis, so the button carries both in one enum.
public enum NrMode : byte { Off, Anr, Emnr }

// Pre-RXA time-domain blanker. Nb1 = ANB (noise blanker), Nb2 = NOB (noise gate).
// Engine silently ignores this until the pre-RXA pipeline lands (task #4);
// kept in the contract so the UI shape doesn't churn when it does.
public enum NbMode : byte { Off, Nb1, Nb2 }

// Thetis default NbThreshold = 3.3 (WDSP units), which is `0.165 × 20` — the
// Thetis UI slider sitting at 20. Kept here so REST round-trips preserve the
// UI-space value rather than the scaled one.
public sealed record NrConfig(
    NrMode NrMode = NrMode.Off,
    bool AnfEnabled = false,
    bool SnbEnabled = false,
    bool NbpNotchesEnabled = false,
    NbMode NbMode = NbMode.Off,
    double NbThreshold = 20.0);

public sealed record StateDto(
    ConnectionStatus Status,
    string? Endpoint,
    long VfoHz,
    RxMode Mode,
    int FilterLowHz,
    int FilterHighHz,
    int SampleRate,
    double AgcTopDb = 80.0,
    // User-baseline attenuator in dB, 0..31. Hardware receives
    // <c>AttenDb + AttOffsetDb</c> (clamped to 31) while auto-ATT is engaged.
    // Default is 0 — auto-ATT ramps the offset up on observed ADC overloads.
    int AttenDb = 0,
    NrConfig? Nr = null,
    int ZoomLevel = 1,
    // Auto-attenuator control loop. When on (default), the server raises
    // AttOffsetDb by 1 per ~100 ms window in which any ADC-overload bit was
    // seen, and decays it by 1 per clean window. Ported from Thetis
    // console.cs:22167 (handleOverload).
    bool AutoAttEnabled = true,
    int AttOffsetDb = 0,
    // Red-lamp flag derived from Thetis' overload-level counter
    // (+2 per overload cycle, -1 per clean, clamped 0..5, warn when >3).
    bool AdcOverloadWarning = false);

public sealed record RadioInfo(
    string MacAddress,
    string IpAddress,
    string BoardId,
    string FirmwareVersion,
    bool Busy,
    IReadOnlyDictionary<string, string>? Details = null);

public sealed record ConnectRequest(
    string Endpoint,
    int SampleRate = 192_000,
    bool? PreampOn = null,
    int? Atten = null);

public sealed record VfoSetRequest(long Hz);

public sealed record ModeSetRequest(RxMode Mode);

public sealed record BandwidthSetRequest(int Low, int High);

public sealed record SampleRateSetRequest(int Rate);

public sealed record PreampSetRequest(bool On);

public sealed record AgcGainSetRequest(double TopDb);

public sealed record AttenuatorSetRequest(int Db);

public sealed record MoxSetRequest(bool On);

public sealed record DriveSetRequest(int Percent);

public sealed record NrSetRequest(NrConfig Nr);

// Panadapter/waterfall zoom levels. Level=1 means the analyzer covers the full
// sample-rate span; level=2 means VFO-centered half-span (×2 bins/Hz), and so
// on. The span-centering math lives in the engine; this contract just carries
// the discrete factor on the wire.
public sealed record ZoomSetRequest(int Level);

public sealed record AutoAttSetRequest(bool Enabled);

public sealed record TunSetRequest(bool On);

public sealed record MicGainSetRequest(int Db);

// Leveler max-gain ceiling in dB. Server clamps to [0, 15]; outside that is
// 400. Frontend POSTs this whenever the slider moves and on WS reconnect so
// the operator's preferred ceiling is re-applied after a server restart
// (backend holds no persistent state for this setting).
public sealed record LevelerMaxGainSetRequest(double Gain);

// Per-band memory: last-used frequency and mode for a given ham band
// (e.g. "20m"). The server keeps these in an unencrypted LiteDB file so they
// survive restarts and follow the backend (not the browser). Band buttons
// read the full map on mount and write on every tune/mode change
// (debounced on the web).
public sealed record BandMemoryDto(string Band, long Hz, RxMode Mode);

public sealed record BandMemorySetRequest(long Hz, RxMode Mode);

// UI layout: opaque flexlayout-react JSON persisted server-side so the
// operator's panel arrangement survives page reloads and reinstalls.
// The JSON is stored as a string to avoid strongly-typing the flex-layout
// tree on the wire — the frontend owns the schema.
public sealed record UiLayoutDto(string LayoutJson, long UpdatedUtc);

public sealed record UiLayoutSetRequest(string LayoutJson);

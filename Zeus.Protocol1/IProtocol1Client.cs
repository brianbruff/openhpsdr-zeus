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
using System.Threading.Channels;
using Zeus.Protocol1.Discovery;

namespace Zeus.Protocol1;

/// <summary>
/// Surface of the Protocol-1 streaming client. One instance per radio.
/// Not thread-safe for Connect/Start/Stop/Disconnect (single-writer UI model).
/// Mutation setters are thread-safe.
/// </summary>
public interface IProtocol1Client : IDisposable
{
    /// <summary>Bind the local UDP socket and remember the radio endpoint.</summary>
    Task ConnectAsync(IPEndPoint radioEndpoint, CancellationToken ct);

    /// <summary>Send Metis start, spin up the RX + TX loops, begin IQ streaming.</summary>
    Task StartAsync(StreamConfig config, CancellationToken ct);

    /// <summary>Send Metis stop, join the RX thread, drain the socket.</summary>
    Task StopAsync(CancellationToken ct);

    /// <summary>Release the socket. Idempotent.</summary>
    Task DisconnectAsync(CancellationToken ct);

    ChannelReader<IqFrame> IqFrames { get; }

    /// <summary>Monotonic count of UDP sequence gaps observed since Start.</summary>
    long DroppedFrames { get; }

    /// <summary>Monotonic count of valid RX packets parsed since Start.</summary>
    long TotalFrames { get; }

    void SetVfoAHz(long hz);
    void SetSampleRate(HpsdrSampleRate rate);
    void SetPreamp(bool on);
    void SetAttenuator(HpsdrAtten atten);
    void SetAntennaRx(HpsdrAntenna ant);

    /// <summary>
    /// Flip the outgoing C&amp;C MOX bit (C0 LSB on every register). Read from
    /// the internal CcState snapshot on the TX thread, so every register
    /// emitted after this call carries the updated bit until cleared.
    /// </summary>
    void SetMox(bool on);

    /// <summary>
    /// UI-level TX drive, 0..100 (values outside clamp). Mapped to the 0..255
    /// raw HPSDR drive byte (C0=0x12, C1) inside SnapshotState via
    /// <c>raw = percent * 255 / 100</c>, matching the Protocol-1
    /// <c>transmitter-&gt;drive_level</c> range.
    /// </summary>
    void SetDrive(int percent);

    /// <summary>
    /// Raised from the RX loop whenever a successfully parsed EP6 packet carried
    /// a C&amp;C echo on an AIN-bearing address (addresses 1/2/3 → C0 bytes
    /// 0x08/0x10/0x18). Fire-and-forget — handlers run synchronously on the RX
    /// thread and must not block.
    /// </summary>
    event Action<TelemetryReading>? TelemetryReceived;

    /// <summary>
    /// Raised once per successfully parsed EP6 packet with the OR-aggregated
    /// ADC overload flags from the echoed C&amp;C word. Fires at the packet rate
    /// (~1.2 kHz at 192 kSps); downstream is responsible for any throttling.
    /// Handlers run synchronously on the RX thread and must not block.
    /// </summary>
    event Action<AdcOverloadStatus>? AdcOverloadObserved;

    /// <summary>
    /// Select the radio's wire-level board family. Affects the extended
    /// attenuator byte layout (HL2 vs bare HPSDR) and the N2ADR filter-board
    /// OC pin encoding. Defaults to <see cref="HpsdrBoardKind.HermesLite2"/>.
    /// </summary>
    void SetBoardKind(HpsdrBoardKind board);

    /// <summary>
    /// Toggle the HL2 + N2ADR 7-relay filter board. When on, C2 bits [7:1]
    /// carry the per-band OC pin mask from <see cref="N2adrBands"/>.
    /// Defaults to <c>false</c> (bare HL2, no filter board).
    /// </summary>
    void SetHasN2adr(bool hasN2adr);

    /// <summary>
    /// Hermes-Lite 2 LT2208 DITHER bit. Defaults to <c>false</c> per doc 07 §2.1 Q#1;
    /// flip on if a bench measurement shows it's needed for a particular HL2 gateware.
    /// </summary>
    bool EnableHl2Dither { get; set; }
}

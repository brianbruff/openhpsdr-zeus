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
using System.Text;

namespace Zeus.Server.Tci;

/// <summary>
/// Builds the TCI handshake message sequence sent immediately after WebSocket
/// upgrade. The handshake advertises radio capabilities and initial state.
/// Exact literal sequence per ExpertSDR3 TCI v1.8 spec; order matters.
/// </summary>
public static class TciHandshake
{
    /// <summary>
    /// Build the complete handshake string for a single-RX configuration.
    /// Each line is semicolon-terminated. The sequence ends with "ready;".
    /// </summary>
    public static string BuildHandshake(StateDto state, int sampleRate, bool moxOn, bool tunOn, int drivePercent)
    {
        var sb = new StringBuilder();

        // Protocol identification (must be first)
        sb.Append(TciProtocol.Command("protocol", TciProtocol.ProtocolName, TciProtocol.ProtocolVersion));
        sb.Append(TciProtocol.Command("device", TciProtocol.DeviceName));

        // Capabilities
        sb.Append(TciProtocol.Command("receive_only", false));
        sb.Append(TciProtocol.Command("trx_count", 1));     // single RX for now
        sb.Append(TciProtocol.Command("channels_count", 1)); // single channel per RX

        // Frequency limits (0 Hz to 61.44 MHz, HPSDR max)
        sb.Append(TciProtocol.Command("vfo_limits", 0, 61_440_000));

        // IF limits: ±(sampleRate/2)
        int halfRate = sampleRate / 2;
        sb.Append(TciProtocol.Command("if_limits", -halfRate, halfRate));

        // Supported modulations (uppercase, CWL/CWU not bare CW)
        sb.Append(TciProtocol.Command("modulations_list", "AM,SAM,DSB,LSB,USB,FM,CWL,CWU,DIGL,DIGU"));

        // Sample rates
        sb.Append(TciProtocol.Command("iq_samplerate", sampleRate));
        sb.Append(TciProtocol.Command("audio_samplerate", 48000)); // WDSP audio is 48 kHz

        // Audio state (master volume/mute)
        sb.Append(TciProtocol.Command("volume", 0));       // 0 dB (we don't have master vol yet)
        sb.Append(TciProtocol.Command("mute", false));

        // Monitor (sidetone) — not implemented yet, report as off
        sb.Append(TciProtocol.Command("mon_volume", -20));
        sb.Append(TciProtocol.Command("mon_enable", false));

        // DDS centre frequency (rx=0)
        sb.Append(TciProtocol.Command("dds", 0, state.VfoHz));

        // IF offset (rx=0, channel=0 and channel=1) — zero for now
        sb.Append(TciProtocol.Command("if", 0, 0, 0));
        sb.Append(TciProtocol.Command("if", 0, 1, 0));

        // VFO frequencies (rx=0, channel=0 and channel=1)
        // In single-VFO mode both channels show the same freq
        sb.Append(TciProtocol.Command("vfo", 0, 0, state.VfoHz));
        sb.Append(TciProtocol.Command("vfo", 0, 1, state.VfoHz));

        // Mode
        string tciMode = TciProtocol.ModeToTci(state.Mode);
        sb.Append(TciProtocol.Command("modulation", 0, tciMode));

        // RX enable (rx=0 always true)
        sb.Append(TciProtocol.Command("rx_enable", 0, true));

        // Split, TX, TRX state
        sb.Append(TciProtocol.Command("split_enable", 0, false)); // no split yet
        sb.Append(TciProtocol.Command("tx_enable", 0, moxOn || tunOn));
        sb.Append(TciProtocol.Command("trx", 0, moxOn));
        sb.Append(TciProtocol.Command("tune", 0, tunOn));

        // RX mute (per-receiver)
        sb.Append(TciProtocol.Command("rx_mute", 0, false));

        // RX filter band
        sb.Append(TciProtocol.Command("rx_filter_band", 0, state.FilterLowHz, state.FilterHighHz));

        // TX drive
        sb.Append(TciProtocol.Command("drive", 0, drivePercent));
        sb.Append(TciProtocol.Command("tune_drive", 0, drivePercent)); // same for now

        // TX frequency (event-only in spec, but sent in handshake)
        sb.Append(TciProtocol.Command("tx_frequency", state.VfoHz));

        // Handshake complete
        sb.Append(TciProtocol.Command("ready"));

        return sb.ToString();
    }
}

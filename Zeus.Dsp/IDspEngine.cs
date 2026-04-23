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

namespace Zeus.Dsp;

public enum DisplayPixout : byte
{
    Panadapter = 0,
    Waterfall = 1,
}

public readonly record struct IqFrame(ReadOnlyMemory<double> InterleavedIq, int SampleRateHz);

public interface IDspEngine : IDisposable
{
    int OpenChannel(int sampleRateHz, int pixelWidth);
    void CloseChannel(int channelId);
    void FeedIq(int channelId, ReadOnlySpan<double> interleavedIqSamples);
    void SetMode(int channelId, RxMode mode);
    void SetFilter(int channelId, int lowHz, int highHz);
    void SetVfoHz(int channelId, long vfoHz);
    void SetAgcTop(int channelId, double topDb);
    void SetNoiseReduction(int channelId, NrConfig cfg);
    void SetZoom(int channelId, int level);
    int ReadAudio(int channelId, Span<float> output);

    bool TryGetDisplayPixels(int channelId, DisplayPixout which, Span<float> dbOut);

    /// <summary>Open the TXA channel. Idempotent — calling twice returns the existing id.
    /// Must be called after at least one OpenChannel(RXA). For Synthetic, returns -1 and is a no-op.</summary>
    int OpenTxChannel();

    /// <summary>Flip MOX. When on: SetChannelState(RXA,0,1) then SetChannelState(TXA,1,0).
    /// When off: SetChannelState(TXA,0,1) then SetChannelState(RXA,1,0). For Synthetic, no-op.
    /// If OpenTxChannel has not been called (no TXA), this is a no-op.</summary>
    void SetMox(bool moxOn);

    /// <summary>RXA signal-strength meter in dBm (Thetis rxaMeterType.RXA_S_AV, idx 1).
    /// Returns a frozen −140 dBm from the synthetic engine. Safe to call from the
    /// pipeline tick; WDSP's meter struct is lock-guarded internally.</summary>
    double GetRxaSignalDbm(int channelId);

    /// <summary>Set TXA modulator mode (USB/LSB/FM/AM/...). Calls
    /// SetTXAMode internally on WdspDspEngine; no-op for Synthetic and when no
    /// TXA is open.</summary>
    void SetTxMode(RxMode mode);

    /// <summary>Process one WDSP-sized block of mic audio through TXA and return
    /// the modulated IQ. <paramref name="micMono"/> must contain exactly
    /// <see cref="TxBlockSamples"/> float samples (48 kHz mono). <paramref name="iqInterleaved"/>
    /// receives 2 × TxBlockSamples floats ([I0, Q0, I1, Q1, …]).
    /// Returns the number of IQ complex samples produced (0 if TXA not open, MOX
    /// off, or the engine does not implement TX processing like Synthetic).</summary>
    int ProcessTxBlock(ReadOnlySpan<float> micMono, Span<float> iqInterleaved);

    /// <summary>WDSP TXA block size in mono samples. Mic ingest buffers accumulate
    /// this many samples before calling ProcessTxBlock.</summary>
    int TxBlockSamples { get; }

    /// <summary>Set TXA mic-side linear gain (Thetis audio.cs:218-224 wires the
    /// mic-gain dB slider via <c>SetTXAPanelGain1(TXA, 10^(db/20))</c>).
    /// <paramref name="linearGain"/> is already linear. No-op on Synthetic
    /// and when TXA is not open.</summary>
    void SetTxPanelGain(double linearGain);

    /// <summary>Set the TXA Leveler maximum-gain ceiling in dB. Calls
    /// <c>SetTXALevelerTop</c> (wcpAGC.c:648), which WDSP converts internally
    /// to a linear cap via <c>pow(10, maxgainDb/20)</c>. Caller is
    /// responsible for range-clamping; this method passes the value through.
    /// No-op on Synthetic and when TXA is not open.</summary>
    void SetTxLevelerMaxGain(double maxGainDb);

    /// <summary>Start or stop the TXA internal-tune post-generator tone
    /// (Thetis console.cs:18648 `chkTUN_CheckedChanged`). When on, TXA emits
    /// a steady unmodulated carrier regardless of mic input. When off, the
    /// post-generator is disabled and normal mic-driven TX resumes.</summary>
    void SetTxTune(bool on);

    /// <summary>Latest per-stage TXA peak meters sampled from the last
    /// ProcessTxBlock call. Returns <see cref="TxStageMeters.Silent"/> when
    /// TXA is not open or MOX is off (no fresh samples). Safe to poll
    /// concurrently with ProcessTxBlock — the engine publishes via an
    /// atomic snapshot so the reader sees a consistent set.</summary>
    TxStageMeters GetTxStageMeters();
}

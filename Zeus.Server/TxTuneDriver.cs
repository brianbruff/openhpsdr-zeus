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
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF), with contributions from (DH1KLM); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeus.Dsp;
using Zeus.Protocol1;

namespace Zeus.Server;

/// <summary>
/// Keeps WDSP TXA's sample pump running when TUN or TwoTone is armed but
/// the mic uplink isn't. Both PostGen modes (mode=0 TUN carrier, mode=1
/// TwoTone) live inside the TXA chain
/// (<see cref="Zeus.Dsp.Wdsp.WdspDspEngine.SetTxTune"/> /
/// <see cref="Zeus.Dsp.Wdsp.WdspDspEngine.SetTwoTone"/>), so they only
/// emit IQ when <c>fexchange2</c> is called at the block rate. During MOX
/// that call is driven by <see cref="TxAudioIngest"/> as mic frames arrive;
/// during TUN/TwoTone there's no mic, so this service synthesises silent
/// mic input at the WDSP block cadence (1024 samples @ 48 kHz ≈ 21 ms).
/// PostGen overwrites the silent midbuff regardless of mic content, so the
/// same pump path works for both modes — we just gate on either flag.
///
/// Starts and stops via <see cref="TxService.IsTunOn"/> /
/// <see cref="TxService.IsTwoToneOn"/> polling; not worth building a
/// subscription pattern for a feature that toggles at click rate.
/// </summary>
internal sealed class TxTuneDriver : BackgroundService
{
    private static readonly TimeSpan PollIdle = TimeSpan.FromMilliseconds(100);
    // Tick is derived from the engine's mic block size per loop iteration
    // (block_samples / 48 kHz, shaved slightly so we run a little faster
    // than WDSP's block clock). Fixed 20 ms fell behind on P2's 512-sample
    // block (10.67 ms) and starved the G2 DUC, producing close-in spurs.
    private const int MicRateHz = 48_000;

    private readonly TxService _tx;
    private readonly DspPipelineService _pipeline;
    private readonly TxIqRing _ring;
    private readonly ILogger<TxTuneDriver> _log;

    public TxTuneDriver(TxService tx, DspPipelineService pipeline, TxIqRing ring, ILogger<TxTuneDriver> log)
    {
        _tx = tx;
        _pipeline = pipeline;
        _ring = ring;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        float[]? micScratch = null;
        float[]? iqScratch = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_tx.IsTunOn && !_tx.IsTwoToneOn)
                {
                    await Task.Delay(PollIdle, ct).ConfigureAwait(false);
                    continue;
                }

                var engine = _pipeline.CurrentEngine;
                int micBlock = engine?.TxBlockSamples ?? 0;
                int iqOut = engine?.TxOutputSamples ?? 0;
                if (engine is null || micBlock <= 0 || iqOut <= 0)
                {
                    // No TXA yet — retry on the slow cadence.
                    await Task.Delay(PollIdle, ct).ConfigureAwait(false);
                    continue;
                }

                if (micScratch is null || micScratch.Length < micBlock)
                    micScratch = new float[micBlock];
                if (iqScratch is null || iqScratch.Length < 2 * iqOut)
                    iqScratch = new float[2 * iqOut];

                // Silent mic — the post-gen tone gets inserted after the mic
                // processing stage by WDSP, so fexchange2 still produces the
                // carrier even with zero mic input.
                Array.Clear(micScratch, 0, micBlock);
                int produced = engine.ProcessTxBlock(
                    new ReadOnlySpan<float>(micScratch, 0, micBlock),
                    new Span<float>(iqScratch, 0, 2 * iqOut));
                if (produced > 0)
                {
                    var iqSpan = new ReadOnlySpan<float>(iqScratch, 0, 2 * produced);
                    // P1 path: ring feeds the EP2 packer in Protocol1Client.
                    _ring.Write(iqSpan);
                    // P2 path: forward the same block directly to the active
                    // Protocol2Client's 1029-port DUC sender. No-op when P2
                    // isn't the active backend, so both protocols coexist
                    // without a conditional at this seam.
                    _pipeline.ForwardTxIqToP2(iqSpan);
                }

                // Tick at ~95 % of the mic-block duration so WDSP is nearly
                // always ready for the next fexchange2 call (the 5 % margin
                // keeps us ahead of the block clock without blowing a CPU
                // spin on early wakeups).
                int tickMs = Math.Max(1, (micBlock * 950) / (MicRateHz));
                await Task.Delay(TimeSpan.FromMilliseconds(tickMs), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "tx.tune driver tick failed");
                try { await Task.Delay(PollIdle, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}

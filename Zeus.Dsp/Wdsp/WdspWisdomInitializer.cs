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

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;

namespace Zeus.Dsp.Wdsp;

/// <summary>
/// Owns the one-shot WDSPwisdom FFTW plan-cache initialisation. Singleton —
/// the WDSP wisdom file at $LOCALAPPDATA/Zeus/wdspWisdom00 is process-global
/// state, so there's nothing to parallelise.
/// </summary>
public sealed class WdspWisdomInitializer
{
    private readonly ILogger _log;
    private readonly object _gate = new();
    private Task? _task;
    private int _phase = (int)WisdomPhase.Idle;

    public WdspWisdomInitializer(ILogger<WdspWisdomInitializer>? logger = null)
    {
        _log = logger ?? NullLogger<WdspWisdomInitializer>.Instance;
    }

    public WisdomPhase Phase => (WisdomPhase)Volatile.Read(ref _phase);

    public event Action<WisdomPhase>? PhaseChanged;

    /// <summary>
    /// Idempotent. First call kicks off the WDSPwisdom P/Invoke on a worker
    /// thread and returns a Task tracking it. Subsequent calls (including
    /// re-entrance from WdspDspEngine) return the same Task.
    /// </summary>
    public Task EnsureInitializedAsync()
    {
        lock (_gate)
        {
            if (_task is not null) return _task;
            SetPhase(WisdomPhase.Building);
            WdspNativeLoader.EnsureResolverRegistered();
            _task = Task.Run(RunWisdom);
            return _task;
        }
    }

    private void RunWisdom()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Zeus");
            Directory.CreateDirectory(dir);

            // WDSP's wisdom.c does a plain strcat of "wdspWisdom00" onto the
            // directory without inserting a path separator (see native/wdsp/
            // wisdom.c:49-50). Pass the directory with a trailing separator so
            // the native side produces a valid "<dir>/wdspWisdom00" path
            // instead of "<dir>wdspWisdom00" which lands the wisdom file one
            // level up.
            var dirForNative = dir + Path.DirectorySeparatorChar;

            _log.LogInformation("wdsp.wisdom initialising dir={Dir}", dirForNative);
            int result = NativeMethods.WDSPwisdom(dirForNative);
            var status = Marshal.PtrToStringUTF8(NativeMethods.wisdom_get_status()) ?? string.Empty;
            _log.LogInformation(
                "wdsp.wisdom ready result={Result} ({Source}) status={Status}",
                result, result == 0 ? "loaded" : "built", status);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "wdsp.wisdom failed — subsequent FFT planning will take the slow path");
        }
        finally
        {
            SetPhase(WisdomPhase.Ready);
        }
    }

    private void SetPhase(WisdomPhase next)
    {
        var prev = (WisdomPhase)Interlocked.Exchange(ref _phase, (int)next);
        if (prev != next) PhaseChanged?.Invoke(next);
    }
}

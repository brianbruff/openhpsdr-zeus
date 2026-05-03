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
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.

using Zeus.Contracts;
using Zeus.Protocol1.Discovery;

namespace Zeus.Server;

/// <summary>
/// Dispatch from <see cref="HpsdrBoardKind"/> to the per-board
/// <see cref="RadioCalibration"/> bucket. Mirrors
/// <c>PaDefaults.GetPaGainDb</c>'s seam — board-specific power math goes
/// through this helper rather than being special-cased inside
/// <c>TxMetersService.ComputeMeters</c>.
///
/// Constants come from Thetis <c>console.cs:25053-25118</c>
/// (<c>computeAlexFwdPower</c>). Where Thetis distinguishes flavours that
/// Zeus' single-byte board id collapses (e.g. ANAN_G2 vs ORIONMKII vs
/// ANAN-8000D, all id 0x0A), we pick the bucket the test rig uses and
/// document the caveat. Operators on the alternate hardware will see a
/// meter offset that needs maintainer review — flagged TODO in the
/// per-record summaries.
/// </summary>
internal static class RadioCalibrations
{
    /// <summary>
    /// Pick the calibration table for a given board. Falls back to
    /// <see cref="RadioCalibration.HermesLite2"/> for unknown boards so a
    /// fresh / disconnected client doesn't divide-by-zero — operator-visible
    /// only on TX, which is gated on a live radio anyway.
    /// </summary>
    public static RadioCalibration For(HpsdrBoardKind board) => board switch
    {
        HpsdrBoardKind.HermesLite2 => RadioCalibration.HermesLite2,
        HpsdrBoardKind.Hermes      => RadioCalibration.Hermes,
        HpsdrBoardKind.Metis       => RadioCalibration.Hermes,
        HpsdrBoardKind.Griffin     => RadioCalibration.Hermes,
        HpsdrBoardKind.Angelia     => RadioCalibration.Anan100,
        HpsdrBoardKind.Orion       => RadioCalibration.Anan200,
        // Board id 0x0A in HpsdrBoardKind covers both ANAN-7000DLE / G1 / G2
        // / G2-1K (Thetis ANAN_G2 enum: bridge 0.12 / ref 5.0 / offset 32)
        // AND ANAN-8000D (Thetis ORIONMKII enum: bridge 0.08 / ref 5.0 /
        // offset 18). KB2UKA's test rig is a G2 MkII, so the default
        // dispatch picks the G2 bucket. ANAN-8000D operators may see a
        // ~30 % low FWD reading until the dispatch grows a discriminator.
        // TODO(p2-cal): expose discovery byte / firmware string so the
        // ANAN-8000D bucket can be chosen automatically — see
        // RadioCalibration.OrionMkIIAnan8000.
        HpsdrBoardKind.OrionMkII   => RadioCalibration.OrionMkII,
        _                          => RadioCalibration.HermesLite2,
    };
}

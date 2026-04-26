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

namespace Zeus.Contracts;

/// <summary>
/// One 1024-sample paired feedback block destined for WDSP <c>psccF</c>.
/// TX side comes from the post-fexchange2 IQ tap inside Zeus's TXA pipeline
/// and the RX side is the radio's feedback-coupler IQ.
///
/// For Protocol 2: Both TX and RX arrive interleaved within a single UDP
/// packet on port 1035 when PS is armed (pihpsdr <c>process_ps_iq_data</c>,
/// <c>new_protocol.c:2463-2510</c>), with TX forwarded over DDC0 and RX on DDC1.
///
/// For Protocol 1 (Hermes Lite 2): When PureSignal is enabled via register
/// 0x0a[22], the feedback IQ is included in the RX stream. The exact mechanism
/// depends on HL2 gateware version and may require additional configuration.
///
/// Block size is 1024 complex samples per pihpsdr <c>receiver.c:636</c> —
/// independent of Zeus's TX analyzer 2048-sample block. Do NOT share the
/// buffers across pumps; the calcc state machine demands its own sample
/// alignment.
///
/// SeqHint is the radio's UDP sequence at the start of the block, useful
/// for diagnostic logging when the cal converges slowly. Not used by WDSP.
/// </summary>
public readonly record struct PsFeedbackFrame(
    float[] TxI,
    float[] TxQ,
    float[] RxI,
    float[] RxQ,
    ulong SeqHint);

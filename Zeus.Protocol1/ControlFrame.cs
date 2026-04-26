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

using System.Buffers.Binary;
using System.Net;
using Zeus.Protocol1.Discovery;

namespace Zeus.Protocol1;

/// <summary>
/// Encodes Protocol-1 outbound packets: the 8-byte Metis header, the per-USB-frame
/// sync + C&amp;C preamble, and the CC payloads the MVP writes.
/// See docs/prd/02-protocol1-integration.md §3–§4 for wire-byte provenance.
/// </summary>
internal static class ControlFrame
{
    public const int PacketLength = 1032;
    public const int UsbFrameLength = 512;

    /// <summary>Round-robin CC0 register address selector (doc 02 §4).</summary>
    public enum CcRegister : byte
    {
        Config = 0x00,
        TxFreq = 0x02,
        RxFreq = 0x04,
        DriveFilter = 0x12,
        // Extended RX attenuator (both bare HPSDR and HL2 firmware gain).
        // Protocol-1 writes these under C0=0x14.
        Attenuator = 0x14,
    }

    /// <summary>
    /// Immutable snapshot of the parameters a single CC frame will encode.
    /// Thread-safety: the live client updates these via atomic writes; the TX
    /// thread copies a snapshot each tick.
    /// </summary>
    public readonly record struct CcState(
        long VfoAHz,
        HpsdrSampleRate Rate,
        bool PreampOn,
        HpsdrAtten Atten,
        HpsdrAntenna RxAntenna,
        bool Mox,
        bool EnableHl2Dither,
        HpsdrBoardKind Board,
        bool HasN2adr = false,
        // Raw DriveFilter C1 payload byte (0..255). This is the transmitter
        // drive_level written directly to output_buffer[C1]. Units are
        // "hardware drive level 0..255"; UI-side percent is mapped in
        // Protocol1Client.SnapshotState.
        byte DriveLevel = 0,
        // User-configured OC pin masks (7-bit) from PaSettingsStore. OR'd with
        // the board's auto-filter output in WriteConfigPayload so the stock HL2
        // + N2ADR behavior keeps working when the user hasn't configured
        // anything. Selected by MOX: TX mask during transmit, RX mask otherwise
        // (piHPSDR `old_protocol.c:1884-1904`).
        byte UserOcTxMask = 0,
        byte UserOcRxMask = 0);

    /// <summary>
    /// Write the 5 C&amp;C bytes for <paramref name="register"/> given the current
    /// <paramref name="state"/>. Returns the number of bytes written (always 5).
    /// </summary>
    public static int WriteCcBytes(Span<byte> cc, CcRegister register, in CcState state)
    {
        if (cc.Length < 5) throw new ArgumentException("cc span < 5 bytes", nameof(cc));

        // CcRegister values are already the wire-byte encodings (pre-shifted
        // address with bit 0 cleared for MOX). Just OR the MOX bit in.
        cc[0] = (byte)(((byte)register & 0xFE) | (state.Mox ? 1 : 0));

        switch (register)
        {
            case CcRegister.Config:
                WriteConfigPayload(cc[1..], in state);
                break;

            case CcRegister.RxFreq:
            case CcRegister.TxFreq:
                // Frequency payload is a BE uint32 in C1..C4 (doc 02 §4 "Frequency payload").
                BinaryPrimitives.WriteUInt32BigEndian(cc[1..5], (uint)state.VfoAHz);
                break;

            case CcRegister.DriveFilter:
                // Protocol-1 writes C0=0x12, C1 = drive_level & 0xFF, then C2..C4
                // carry mic/filter/PA bits. On HermesLite2 that same block zeroes
                // C2/C3/C4 and lights C2[3] for PA enable when pa_enabled &&
                // !txband->disablePA. Without this bit the HL2 gateware never
                // energizes the PA regardless of drive level. We gate on MOX so
                // PA-enable is only asserted while transmitting.
                cc[1] = state.DriveLevel;
                cc[2] = 0;
                cc[3] = 0;
                cc[4] = 0;
                if (state.Board == HpsdrBoardKind.HermesLite2 && state.Mox)
                {
                    cc[2] |= 0x08;
                }
                break;

            case CcRegister.Attenuator:
                WriteAttenuatorPayload(cc[1..], in state);
                break;

            default:
                cc[1] = cc[2] = cc[3] = cc[4] = 0;
                break;
        }

        return 5;
    }

    private static void WriteAttenuatorPayload(Span<byte> c14, in CcState s)
    {
        // Bare HPSDR (Hermes/Angelia/Orion/MkII): C4 = 0x20 | (Db & 0x1F).
        // HL2: C4 = 0x40 | (60 - Db) — HL2 has no physical RX step attenuator,
        // so the UI "attenuate by N dB" maps to "reduce firmware RX gain by N
        // from its max of 60" (HL2 gateware ad9866 rxgain register).
        int db = s.Atten.ClampedDb;
        byte c4 = s.Board == HpsdrBoardKind.HermesLite2
            ? (byte)(0x40 | Math.Clamp(60 - db, 0, 60))
            : (byte)(0x20 | (db & 0x1F));

        c14[0] = 0;   // C1 — reserved on this register
        c14[1] = 0;   // C2
        c14[2] = 0;   // C3
        c14[3] = c4;
    }

    private static void WriteConfigPayload(Span<byte> c14, in CcState s)
    {
        // C1: sample rate at [1:0], clock source (Atlas-era) at [6:4] — left 0 for Hermes+.
        byte c1 = (byte)((byte)s.Rate & 0x03);
        c14[0] = c1;

        // C2: class-E PA at bit 0; OC pins (N2ADR filter board on HL2, user-
        // configured OC outputs on Orion-class) at bits 1..7. Class-E stays 0
        // for RX-only MVP. We OR three sources so stock behavior holds when
        // the user hasn't touched PA Settings:
        //   1. Board auto-filter mask (N2ADR on HL2) — legacy path
        //   2. User's per-band OC-TX mask when MOX, else OC-RX mask
        byte ocPins = 0;
        if (s.Board == HpsdrBoardKind.HermesLite2 && s.HasN2adr)
        {
            ocPins |= N2adrBands.RxOcMask(s.VfoAHz);
        }
        ocPins |= (byte)((s.Mox ? s.UserOcTxMask : s.UserOcRxMask) & 0x7F);
        byte c2 = (byte)(ocPins << 1);
        c14[1] = c2;

        // C3: Atlas step attenuator [1:0], RAND [2], DITHER [3], preamp [4],
        // RX antenna [7:5]. We leave [1:0] zero — the dedicated extended
        // attenuator register (C0=0x14) is the single source of truth for RX
        // attenuation on every board we target. Setting both would double
        // up on Atlas-era gateware.
        byte c3 = 0;
        if (s.EnableHl2Dither) c3 |= 1 << 3;      // Q#1: off by default.
        if (s.PreampOn) c3 |= 1 << 4;             // Q#2: single global preamp bit for MVP.
        c3 |= (byte)(((byte)s.RxAntenna & 0x07) << 5);
        c14[2] = c3;

        // C4: Alex TX antenna [1:0] = 0 (RX-only MVP), duplex [2] = 1 (always, per
        // old_protocol.c:2661), N-1 receivers at [5:3] = 0 (we always use 1 RX).
        byte c4 = 1 << 2;
        c14[3] = c4;
    }

    /// <summary>
    /// Build a complete 1032-byte Metis data frame with two USB frames carrying
    /// the two given registers back-to-back, an increasing sequence number, and
    /// (when MOX is on and a tone generator is supplied) an IQ test-tone payload.
    /// </summary>
    public static void BuildDataPacket(
        Span<byte> packet,
        uint sendSequence,
        CcRegister evenRegister,
        CcRegister oddRegister,
        in CcState state,
        ITxIqSource? iqSource = null)
    {
        if (packet.Length != PacketLength)
            throw new ArgumentException("packet span must be 1032 bytes", nameof(packet));

        packet.Clear();

        // Metis header: 0xEF 0xFE 0x01 0x02 + BE uint32 seq. Endpoint 0x02 = TX/audio.
        packet[0] = 0xEF;
        packet[1] = 0xFE;
        packet[2] = 0x01;
        packet[3] = 0x02;
        BinaryPrimitives.WriteUInt32BigEndian(packet[4..8], sendSequence);

        WriteUsbFrame(packet.Slice(8, UsbFrameLength), evenRegister, in state, iqSource);
        WriteUsbFrame(packet.Slice(8 + UsbFrameLength, UsbFrameLength), oddRegister, in state, iqSource);
    }

    /// <summary>
    /// Build a 64-byte Metis start/stop packet.
    /// </summary>
    public static void BuildStartStop(Span<byte> packet, bool start, bool includeWideband = false)
    {
        if (packet.Length < 64) throw new ArgumentException("packet span must be ≥ 64 bytes", nameof(packet));
        packet[..64].Clear();
        packet[0] = 0xEF;
        packet[1] = 0xFE;
        packet[2] = 0x04;
        packet[3] = start ? (byte)(includeWideband ? 0x03 : 0x01) : (byte)0x00;
    }

    /// <summary>Number of IQ samples per 504-byte EP2 USB-frame payload (63 × 8 bytes).</summary>
    internal const int IqSamplesPerUsbFrame = 63;

    private static void WriteUsbFrame(Span<byte> frame, CcRegister register, in CcState state, ITxIqSource? source)
    {
        frame[0] = 0x7F;
        frame[1] = 0x7F;
        frame[2] = 0x7F;
        WriteCcBytes(frame.Slice(3, 5), register, in state);

        // EP2 504-byte payload = 63 groups × 8 bytes, each group =
        // [L_audio s16 BE][R_audio s16 BE][I s16 BE][Q s16 BE]
        // (both the audio ring fill and the IQ ring fill write into the same
        // 8-byte slot). HL2 has no audio codec in the MVP target, so audio
        // bytes stay zero. HL2 also clears the LSB of the I and Q low bytes as
        // a CWX workaround (`isample & 0xFE`) — we mirror that.
        //
        // Pre-conditions for writing a non-zero payload: MOX engaged, board is
        // HL2 (don't accidentally drive RF on other boards whose PA bits live
        // elsewhere), and an IQ source is plumbed through.
        if (source is null || !state.Mox || state.Board != HpsdrBoardKind.HermesLite2)
        {
            // frame[8..] was cleared by BuildDataPacket; leave zero.
            return;
        }

        // The HL2's TXG stage (DriveFilter C1 = DriveLevel byte) scales the
        // transmit path by drive%. Scaling IQ here on top would double-multiply
        // (drive⁴ power response). Send at unity — WDSP's ALC already clamps
        // the TXA output to ≤ 0 dBFS and the TUN post-gen tone is a
        // fixed-amplitude single-tone carrier, so neither source can overshoot
        // +1.0 here. The prior 0.85 factor cost ~1.4 dB of achievable output
        // and was observed to leave HL2 at 1.2 W when deskHPSDR hit 6.6 W on
        // the same antenna/band; it was belt-and-suspenders on top of ALC.
        // At DriveLevel=0 the HL2 TXG is already 0 (silent), but zero the IQ
        // too so the wire bytes are silent regardless of board.
        if (state.DriveLevel == 0) return;
        const double amplitude = 1.0;

        var payload = frame[8..];
        int peak = 0;
        long sumAbs = 0;
        int firstI = 0, firstQ = 0;
        for (int s = 0; s < IqSamplesPerUsbFrame; s++)
        {
            var (iSample, qSample) = source.Next(amplitude);
            if (s == 0) { firstI = iSample; firstQ = qSample; }
            int ai = Math.Abs((int)iSample);
            int aq = Math.Abs((int)qSample);
            if (ai > peak) peak = ai;
            if (aq > peak) peak = aq;
            sumAbs += ai + aq;
            int off = s * 8;
            // Audio L/R stay zero (payload was cleared).
            payload[off + 4] = (byte)((iSample >> 8) & 0xFF);
            payload[off + 5] = (byte)(iSample & 0xFE);
            payload[off + 6] = (byte)((qSample >> 8) & 0xFF);
            payload[off + 7] = (byte)(qSample & 0xFE);
        }
        LastPeakAbs = peak;
        LastMeanAbs = (int)(sumAbs / (2 * IqSamplesPerUsbFrame));
        LastFirstI = firstI;
        LastFirstQ = firstQ;
        LastDriveByte = state.DriveLevel;
    }

    // Diagnostic tap — read by Protocol1Client.TxLoopAsync to log what's
    // actually on the wire. Each WriteUsbFrame call updates these; TxLoopAsync
    // logs them at 1 Hz so we can tell whether the IQ reaching the HL2 is
    // really at rated amplitude vs being attenuated somewhere in the chain.
    public static volatile int LastPeakAbs;
    public static volatile int LastMeanAbs;
    public static volatile int LastFirstI;
    public static volatile int LastFirstQ;
    public static volatile byte LastDriveByte;

    public static IPEndPoint Port1024(IPAddress address) => new IPEndPoint(address, 1024);
}

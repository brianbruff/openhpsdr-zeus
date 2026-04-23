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

using System.Buffers;
using System.Buffers.Binary;

namespace Zeus.Protocol1;

/// <summary>
/// EP6 C&amp;C echo slot that carries one of the HL2/Hermes ADC pairs. The raw
/// <paramref name="C0Address"/> byte is preserved (status bits in [2:0] + address
/// in [7:3]) so consumers can disambiguate which physical ADC each u16 represents.
/// <para>
/// The slot-to-ADC mapping on HL2 is:
///   addr=1 (C0=0x08): Ain0 = exciter_pwr / HL2 temperature;   Ain1 = alex_forward_power
///   addr=2 (C0=0x10): Ain0 = alex_reverse_power;              Ain1 = ADC0 bias
///   addr=3 (C0=0x18): Ain0 = ADC1 bias;                        Ain1 = 0 (unused)
/// In each case Ain0 is the BE u16 at C1..C2 and Ain1 is the BE u16 at C3..C4.
/// </para>
/// </summary>
public readonly record struct TelemetryReading(byte C0Address, ushort Ain0, ushort Ain1);

/// <summary>
/// Per-packet ADC overload flags extracted from the echoed C&amp;C word
/// (C1[0] = ADC0, C2[0] = ADC1). OR-accumulated across both USB frames of
/// one EP6 packet so a single set frame is enough to report overload.
/// </summary>
public readonly record struct AdcOverloadStatus(bool Adc0, bool Adc1)
{
    public bool AnyOverload => Adc0 || Adc1;

    public static AdcOverloadStatus FromBits(byte bits) =>
        new((bits & 0x01) != 0, (bits & 0x02) != 0);
}

/// <summary>
/// Pure, allocation-free parser for Metis EP6 RX IQ packets.
/// Kept static so unit tests exercise the wire format without sockets.
/// </summary>
internal static class PacketParser
{
    public const int PacketLength = 1032;
    public const int UsbFrameLength = 512;
    public const int MetisHeaderLength = 8;
    public const int UsbHeaderLength = 8;       // 3-byte sync + 5-byte C&C
    public const int UsbPayloadLength = 504;    // 512 − 8
    public const int BytesPerSampleGroup = 8;   // 3 I + 3 Q + 2 mic
    public const int ComplexSamplesPerUsbFrame = UsbPayloadLength / BytesPerSampleGroup; // 63
    public const int ComplexSamplesPerPacket = ComplexSamplesPerUsbFrame * 2;            // 126

    private const byte Sync = 0x7F;
    private const byte MetisMagic0 = 0xEF;
    private const byte MetisMagic1 = 0xFE;
    private const byte MetisEp6 = 0x06;
    private const byte MetisTypeDataFrame = 0x01;

    private const double Int24Scale = 1.0 / 8_388_608.0; // 1 / 2^23

    /// <summary>
    /// Read a 24-bit big-endian signed integer with sign-extension to int32.
    /// </summary>
    public static int ReadInt24BigEndian(ReadOnlySpan<byte> b)
    {
        // high byte is signed — shifts preserve sign. Middle/low are unsigned.
        int v = ((sbyte)b[0]) << 16;
        v |= b[1] << 8;
        v |= b[2];
        return v;
    }

    /// <summary>
    /// Scale a signed int24 sample to a double in [-1.0, +1.0].
    /// </summary>
    public static double ScaleInt24(int sample) => sample * Int24Scale;

    /// <summary>
    /// Parse an EP6 RX IQ packet.
    /// </summary>
    /// <param name="packet">1032-byte Metis data frame.</param>
    /// <param name="interleavedOut">
    /// Destination buffer; must be ≥ <c>2 × <see cref="ComplexSamplesPerPacket"/></c>
    /// entries long. Populated as <c>[I0,Q0,I1,Q1,…]</c>.
    /// </param>
    /// <param name="sequence">Radio-assigned monotonic seq (BE uint32 at bytes 4..7).</param>
    /// <param name="complexSamples">Number of I/Q pairs written to <paramref name="interleavedOut"/>.</param>
    /// <returns>true on valid packet; false on bad magic, wrong length, bad sync, or wrong endpoint.</returns>
    public static bool TryParsePacket(
        ReadOnlySpan<byte> packet,
        Span<double> interleavedOut,
        out uint sequence,
        out int complexSamples)
        => TryParsePacket(packet, interleavedOut, out sequence, out complexSamples, out _, out _);

    /// <summary>
    /// Back-compat overload that exposes a single telemetry reading. When both
    /// USB frames in a packet carry AIN-bearing addresses, the second is
    /// returned (legacy "last wins" semantics) — callers that need both must
    /// use the 7-arg overload below.
    /// </summary>
    public static bool TryParsePacket(
        ReadOnlySpan<byte> packet,
        Span<double> interleavedOut,
        out uint sequence,
        out int complexSamples,
        out TelemetryReading telemetry)
    {
        bool ok = TryParsePacket(packet, interleavedOut, out sequence, out complexSamples,
            out var t0, out var t1, out _);
        telemetry = t1.C0Address != 0 ? t1 : t0;
        return ok;
    }

    /// <summary>
    /// 6-arg back-compat overload without per-frame telemetry: collapses both
    /// frames into one (last-wins).
    /// </summary>
    public static bool TryParsePacket(
        ReadOnlySpan<byte> packet,
        Span<double> interleavedOut,
        out uint sequence,
        out int complexSamples,
        out TelemetryReading telemetry,
        out byte adcOverloadBits)
    {
        bool ok = TryParsePacket(packet, interleavedOut, out sequence, out complexSamples,
            out var t0, out var t1, out adcOverloadBits);
        telemetry = t1.C0Address != 0 ? t1 : t0;
        return ok;
    }

    /// <summary>
    /// Parse an EP6 RX IQ packet. Emits telemetry independently for each of the
    /// two USB frames — Protocol-1 dispatches per frame, so each frame's C&amp;C
    /// switch contributes its own ADC reading.
    /// An empty reading is indicated by <see cref="TelemetryReading.C0Address"/> == 0
    /// — valid echoes always carry a non-zero addr byte (0x08 / 0x10 / 0x18,
    /// possibly OR'd with C0[0]=MOX echo).
    /// </summary>
    /// <param name="adcOverloadBits">
    /// Bit 0 = ADC0 overload, bit 1 = ADC1 overload. OR of both USB frames so a
    /// single set frame reports the overload for the whole packet.
    /// </param>
    public static bool TryParsePacket(
        ReadOnlySpan<byte> packet,
        Span<double> interleavedOut,
        out uint sequence,
        out int complexSamples,
        out TelemetryReading telemetry0,
        out TelemetryReading telemetry1,
        out byte adcOverloadBits)
    {
        sequence = 0;
        complexSamples = 0;
        telemetry0 = default;
        telemetry1 = default;
        adcOverloadBits = 0;

        if (packet.Length != PacketLength) return false;
        if (packet[0] != MetisMagic0 || packet[1] != MetisMagic1) return false;
        if (packet[2] != MetisTypeDataFrame) return false;
        if (packet[3] != MetisEp6) return false;

        sequence = BinaryPrimitives.ReadUInt32BigEndian(packet[4..8]);

        int needed = 2 * ComplexSamplesPerPacket;
        if (interleavedOut.Length < needed) return false;

        int written = 0;
        for (int frame = 0; frame < 2; frame++)
        {
            int frameStart = MetisHeaderLength + frame * UsbFrameLength;
            ReadOnlySpan<byte> usb = packet.Slice(frameStart, UsbFrameLength);
            if (usb[0] != Sync || usb[1] != Sync || usb[2] != Sync) return false;

            // usb[3..8] are the echoed C&C bytes. addr = (C0 >> 3) & 0x1F;
            // addresses 1/2/3 carry the ADC pairs we want. Each USB frame is
            // an independent reading.
            byte c0 = usb[3];
            int addr = (c0 >> 3) & 0x1F;
            if (addr is 1 or 2 or 3)
            {
                var reading = new TelemetryReading(
                    C0Address: c0,
                    Ain0: BinaryPrimitives.ReadUInt16BigEndian(usb.Slice(4, 2)),
                    Ain1: BinaryPrimitives.ReadUInt16BigEndian(usb.Slice(6, 2)));
                if (frame == 0) telemetry0 = reading;
                else telemetry1 = reading;
            }

            adcOverloadBits |= (byte)(usb[4] & 0x01);        // C1[0] → bit 0 (ADC0)
            adcOverloadBits |= (byte)((usb[5] & 0x01) << 1); // C2[0] → bit 1 (ADC1)

            ReadOnlySpan<byte> payload = usb[UsbHeaderLength..];
            for (int g = 0; g < ComplexSamplesPerUsbFrame; g++)
            {
                int off = g * BytesPerSampleGroup;
                int i = ReadInt24BigEndian(payload.Slice(off, 3));
                int q = ReadInt24BigEndian(payload.Slice(off + 3, 3));
                // off+6, off+7 carry the 16-bit mic sample (mic/line in). Unused for RX MVP.
                interleavedOut[written++] = ScaleInt24(i);
                interleavedOut[written++] = ScaleInt24(q);
            }
        }

        complexSamples = ComplexSamplesPerPacket;
        return true;
    }

    /// <summary>
    /// Convenience overload that rents a <c>double[]</c> of the exact required
    /// size from <see cref="ArrayPool{Double}.Shared"/>. Returns <c>null</c> on
    /// parse failure; the caller owns the rented buffer on success.
    /// </summary>
    public static double[]? TryParsePacketRented(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out int complexSamples)
    {
        var buffer = ArrayPool<double>.Shared.Rent(2 * ComplexSamplesPerPacket);
        if (!TryParsePacket(packet, buffer, out sequence, out complexSamples))
        {
            ArrayPool<double>.Shared.Return(buffer);
            return null;
        }
        return buffer;
    }

    /// <summary>
    /// Sequence gap counter state; zero-initialized.
    /// </summary>
    public struct SequenceTracker
    {
        private bool _seen;
        private uint _last;
        public long DroppedFrames;
        public long TotalFrames;

        public void Observe(uint seq)
        {
            TotalFrames++;
            if (_seen)
            {
                // Monotonic wrap or radio restart (seq < _last) → reset, no drop count.
                if (seq > _last)
                {
                    long gap = (long)seq - (long)_last - 1;
                    if (gap > 0) DroppedFrames += gap;
                }
            }
            _seen = true;
            _last = seq;
        }
    }
}

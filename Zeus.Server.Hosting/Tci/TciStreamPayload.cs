// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Distributed under the GNU General Public License v2 or later. See the
// LICENSE file at the root of this repository for full text.

using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Zeus.Server.Tci;

/// <summary>
/// TCI binary stream type tag — matches Thetis TCIServer.TCIStreamType
/// (Project Files/Source/Console/TCIServer.cs:341) so on-the-wire bytes
/// are identical for ExpertSDR3-compatible clients.
/// </summary>
internal enum TciStreamType : uint
{
    IqStream = 0,
    RxAudioStream = 1,
    TxAudioStream = 2,
    TxChrono = 3,
    LineOutStream = 4,
}

/// <summary>
/// TCI sample-encoding tag — matches Thetis TCIServer.TCISampleType.
/// </summary>
internal enum TciSampleType : uint
{
    Int16 = 0,
    Int24 = 1,
    Int32 = 2,
    Float32 = 3,
}

/// <summary>
/// Builds the 64-byte fixed TCI binary stream header used for IQ, RX audio,
/// TX audio, and TX_CHRONO frames. Layout matches the SunSDR / ExpertSDR3
/// TCI v1.x specification (TCI_Protocol_Spec §7.1).
///
/// Layout (all little-endian uint32):
///   [ 0]: receiver index
///   [ 4]: sample rate (Hz)
///   [ 8]: format / sample type — see <see cref="TciSampleType"/>
///   [12]: codec id (0 = uncompressed PCM)
///   [16]: crc32 of payload, or 0 to skip
///   [20]: length — count of float values in payload (total floats, not pairs)
///   [24]: stream type — see <see cref="TciStreamType"/>
///   [28..63]: reserved zeros (9 × uint32)
///   [64..]: sample payload bytes
/// </summary>
internal static class TciStreamPayload
{
    public const int HeaderSize = 64;

    public static byte[] Build(
        int receiver,
        int sampleRate,
        TciSampleType sampleType,
        int length,
        TciStreamType streamType,
        ReadOnlySpan<byte> samplePayload)
    {
        var payload = new byte[HeaderSize + samplePayload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0), (uint)receiver);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), (uint)sampleType);
        // [12] codec, [16] crc, [28..63] reserved — all zero (cleared by `new byte[]`)
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(20), (uint)length);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(24), (uint)streamType);
        if (samplePayload.Length > 0)
            samplePayload.CopyTo(payload.AsSpan(HeaderSize));
        return payload;
    }

    public static byte[] BuildIqFromDoubles(int receiver, int sampleRate, ReadOnlySpan<double> interleavedIQ)
    {
        var floats = new float[interleavedIQ.Length];
        for (int i = 0; i < interleavedIQ.Length; i++)
            floats[i] = (float)interleavedIQ[i];
        return Build(
            receiver,
            sampleRate,
            TciSampleType.Float32,
            length: floats.Length,
            streamType: TciStreamType.IqStream,
            samplePayload: MemoryMarshal.AsBytes(floats.AsSpan()));
    }

    /// <summary>
    /// Builds an RX audio frame from mono FLOAT32 samples, duplicating each
    /// sample to L=R for stereo output. TCI v1.x mandates stereo Float32
    /// at 48 kHz on RX audio streams (spec §7.2, §7.6); a mono frame would
    /// be misinterpreted at half-rate by spec-compliant clients.
    /// </summary>
    public static byte[] BuildAudioFromFloats(int receiver, int sampleRate, ReadOnlySpan<float> samples)
    {
        var stereo = new float[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            stereo[i * 2] = samples[i];
            stereo[i * 2 + 1] = samples[i];
        }
        return Build(
            receiver,
            sampleRate,
            TciSampleType.Float32,
            length: stereo.Length,
            streamType: TciStreamType.RxAudioStream,
            samplePayload: MemoryMarshal.AsBytes(stereo.AsSpan()));
    }
}

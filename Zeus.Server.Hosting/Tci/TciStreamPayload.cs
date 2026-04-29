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
/// TX audio, and TX_CHRONO frames. Layout is byte-for-byte identical to
/// Thetis TCIServer.buildStreamPayload (TCIServer.cs:5645).
///
/// Layout (all little-endian uint32):
///   [ 0]: receiver index
///   [ 4]: sample rate (Hz)
///   [ 8]: sample type   — see <see cref="TciSampleType"/>
///   [12]: reserved 0
///   [16]: reserved 0
///   [20]: length        — count of float values for FLOAT32 IQ
///                         (= complex_samples * 2 for I+Q interleaved)
///   [24]: stream type   — see <see cref="TciStreamType"/>
///   [28]: channels      — 2 for IQ (I,Q interleaved)
///   [32..63]: 8 reserved zero uint32
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
        int channels,
        ReadOnlySpan<byte> samplePayload)
    {
        var payload = new byte[HeaderSize + samplePayload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0), (uint)receiver);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), (uint)sampleType);
        // [12], [16] reserved zero (cleared by `new byte[]`)
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(20), (uint)length);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(24), (uint)streamType);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(28), (uint)channels);
        // [32..60] reserved zero
        if (samplePayload.Length > 0)
            samplePayload.CopyTo(payload.AsSpan(HeaderSize));
        return payload;
    }

    /// <summary>
    /// Builds an IQ frame from interleaved doubles (Zeus's internal format).
    /// Samples are downcast to FLOAT32 because that's the TCI canonical IQ
    /// encoding — clients negotiate FLOAT32 and Thetis publishes FLOAT32.
    /// </summary>
    public static byte[] BuildIqFromDoubles(int receiver, int sampleRate, ReadOnlySpan<double> interleavedIQ)
    {
        // Convert double → float into a temporary buffer, then build the frame.
        // Allocating a fresh float[] here is fine; the frame allocation already
        // dominates and we'd have to copy anyway to land on FLOAT32 LE bytes.
        var floats = new float[interleavedIQ.Length];
        for (int i = 0; i < interleavedIQ.Length; i++)
            floats[i] = (float)interleavedIQ[i];
        return Build(
            receiver,
            sampleRate,
            TciSampleType.Float32,
            length: floats.Length,
            streamType: TciStreamType.IqStream,
            channels: 2,
            samplePayload: MemoryMarshal.AsBytes(floats.AsSpan()));
    }
}

// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Distributed under the GNU General Public License v2 or later. See the
// LICENSE file at the root of this repository for full text.

using System.Buffers.Binary;
using Zeus.Server.Tci;

namespace Zeus.Server.Tests.Tci;

/// <summary>
/// Wire-compatibility tests for the 64-byte TCI binary stream header.
/// Layout must match Thetis TCIServer.buildStreamPayload byte-for-byte —
/// any drift here breaks ExpertSDR3 clients.
/// </summary>
public class TciStreamPayloadTests
{
    [Fact]
    public void Build_HeaderSize_IsSixtyFourBytes()
    {
        var frame = TciStreamPayload.Build(
            receiver: 0, sampleRate: 48000, sampleType: TciSampleType.Float32,
            length: 0, streamType: TciStreamType.IqStream, channels: 2,
            samplePayload: ReadOnlySpan<byte>.Empty);

        Assert.Equal(TciStreamPayload.HeaderSize, frame.Length);
        Assert.Equal(64, frame.Length);
    }

    [Fact]
    public void Build_HeaderFields_LittleEndianAtCorrectOffsets()
    {
        var frame = TciStreamPayload.Build(
            receiver: 1, sampleRate: 192_000, sampleType: TciSampleType.Float32,
            length: 1024, streamType: TciStreamType.IqStream, channels: 2,
            samplePayload: ReadOnlySpan<byte>.Empty);

        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(0)));
        Assert.Equal(192_000u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(4)));
        Assert.Equal((uint)TciSampleType.Float32, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(8)));
        Assert.Equal(1024u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(20)));
        Assert.Equal((uint)TciStreamType.IqStream, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(24)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(28)));
    }

    [Fact]
    public void Build_ReservedFields_AreZero()
    {
        var frame = TciStreamPayload.Build(
            receiver: 0, sampleRate: 48000, sampleType: TciSampleType.Float32,
            length: 0, streamType: TciStreamType.IqStream, channels: 2,
            samplePayload: ReadOnlySpan<byte>.Empty);

        // Two reserved uint32 between sampleType and length (offsets 12, 16)
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(12)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(16)));
        // Eight reserved uint32 from offset 32..63
        for (int offset = 32; offset < 64; offset += 4)
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(offset)));
    }

    [Fact]
    public void Build_AppendsPayloadAfterHeader()
    {
        ReadOnlySpan<byte> payload = stackalloc byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var frame = TciStreamPayload.Build(
            receiver: 0, sampleRate: 48000, sampleType: TciSampleType.Float32,
            length: 4, streamType: TciStreamType.IqStream, channels: 2,
            samplePayload: payload);

        Assert.Equal(64 + 4, frame.Length);
        Assert.Equal(0xDE, frame[64]);
        Assert.Equal(0xAD, frame[65]);
        Assert.Equal(0xBE, frame[66]);
        Assert.Equal(0xEF, frame[67]);
    }

    [Fact]
    public void BuildIqFromDoubles_HeaderTypedAsFloat32IqWithTwoChannels()
    {
        ReadOnlySpan<double> samples = stackalloc double[] { 1.0, -1.0, 0.5, -0.5 };
        var frame = TciStreamPayload.BuildIqFromDoubles(0, 48000, samples);

        Assert.Equal((uint)TciSampleType.Float32, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(8)));
        Assert.Equal((uint)TciStreamType.IqStream, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(24)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(28)));
        // length is the count of float values, equal to samples.Length for IQ
        Assert.Equal((uint)samples.Length, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(20)));
    }

    [Fact]
    public void BuildIqFromDoubles_DowncastsSamplesToFloat32LittleEndian()
    {
        ReadOnlySpan<double> samples = stackalloc double[] { 1.0, -1.0, 0.5, -0.5 };
        var frame = TciStreamPayload.BuildIqFromDoubles(0, 48000, samples);

        // Each input double becomes one little-endian FLOAT32 (4 bytes) starting at offset 64.
        Assert.Equal(64 + samples.Length * 4, frame.Length);
        for (int i = 0; i < samples.Length; i++)
        {
            float expected = (float)samples[i];
            float actual = BitConverter.ToSingle(frame, 64 + i * 4);
            Assert.Equal(expected, actual);
        }
    }
}

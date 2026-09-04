// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Ngs2;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

/// <summary>
/// NGS2 voices are handed either a "VAGp" block or a RIFF/WAVE one. Only the
/// first was understood, so a title shipping plain PCM played silence.
/// </summary>
public sealed class Ngs2RiffDecoderTests
{
    [Fact]
    public void Pcm16WaveDecodesToItsOwnSamples()
    {
        var file = BuildWave(0x0001, 16, 2, 48000, [(short)0x0100, unchecked((short)0xFF00), (short)0x2000, (short)0x3000]);

        Assert.True(Ngs2RiffDecoder.TryDecode(file, out var waveform));
        Assert.Equal(48000, waveform.SampleRate);
        Assert.Equal([0x0100, unchecked((short)0xFF00), 0x2000, 0x3000], waveform.Samples);
    }

    [Fact]
    public void Float32WaveIsQuantizedAndClamped()
    {
        var payload = new byte[4 * sizeof(float)];
        foreach (var (index, value) in new[] { (0, 0.0f), (1, 1.0f), (2, -1.0f), (3, 2.0f) })
        {
            BinaryPrimitives.WriteSingleLittleEndian(
                payload.AsSpan(index * sizeof(float)),
                value);
        }

        var file = BuildWave(0x0003, 32, 1, 44100, payload);

        Assert.True(Ngs2RiffDecoder.TryDecode(file, out var waveform));
        Assert.Equal(
            [(short)0, short.MaxValue, short.MinValue, short.MaxValue],
            waveform.Samples);
    }

    [Fact]
    public void ExtensibleFormatUsesItsSubformatTag()
    {
        // WAVE_FORMAT_EXTENSIBLE hides the real tag in its subformat GUID.
        var file = BuildWave(0xFFFE, 16, 1, 48000, [(short)0x1234], subFormatTag: 0x0001);

        Assert.True(Ngs2RiffDecoder.TryDecode(file, out var waveform));
        Assert.Equal([(short)0x1234], waveform.Samples);
    }

    [Fact]
    public void SamplerChunkSuppliesLoopPoints()
    {
        var sampler = new byte[0x24 + 24];
        BinaryPrimitives.WriteUInt32LittleEndian(sampler.AsSpan(0x1C), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(sampler.AsSpan(0x24 + 8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(sampler.AsSpan(0x24 + 12), 3);

        var file = BuildWave(0x0001, 16, 1, 48000, [(short)1, 2, 3, 4], sampler: sampler);

        Assert.True(Ngs2RiffDecoder.TryDecode(file, out var waveform));
        Assert.Equal(1, waveform.LoopStart);
        Assert.Equal(3, waveform.LoopEnd);
    }

    [Fact]
    public void WaveWithoutLoopsReportsNone()
    {
        var file = BuildWave(0x0001, 16, 1, 48000, [(short)1, 2, 3, 4]);

        Assert.True(Ngs2RiffDecoder.TryDecode(file, out var waveform));
        Assert.Equal(-1, waveform.LoopStart);
        Assert.Equal(4, waveform.LoopEnd);
    }

    [Fact]
    public void UnsupportedCodecIsRefusedRatherThanGuessed()
    {
        // ATRAC9 lands here. Producing noise from an unverified configuration
        // would be worse than staying silent.
        var file = BuildWave(0xFFFF, 0, 2, 48000, [(short)1, (short)2]);

        Assert.False(Ngs2RiffDecoder.TryDecode(file, out _));
    }

    [Fact]
    public void ChunkRunningPastTheBufferDoesNotReadOutOfBounds()
    {
        var file = BuildWave(0x0001, 16, 1, 48000, [(short)1, (short)2]);
        // Claim the data chunk is far larger than what is there.
        var dataSizeOffset = file.Length - (2 * sizeof(short)) - 4;
        BinaryPrimitives.WriteUInt32LittleEndian(
            file.AsSpan(dataSizeOffset),
            0x7FFF_FFFF);

        Assert.False(Ngs2RiffDecoder.TryDecode(file, out _));
    }

    [Fact]
    public void DeclaredSizeIsBoundedBeforeAnyGuestRead()
    {
        var header = new byte[Ngs2RiffDecoder.RiffHeaderSize];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 0x7FFF_FFFF);
        "WAVE"u8.CopyTo(header.AsSpan(8));

        Assert.False(Ngs2RiffDecoder.TryGetTotalSize(header, out _));
    }

    private static byte[] BuildWave(
        ushort formatTag,
        ushort bitsPerSample,
        ushort channels,
        int sampleRate,
        short[] samples,
        ushort subFormatTag = 0,
        byte[]? sampler = null) =>
        BuildWave(
            formatTag,
            bitsPerSample,
            channels,
            sampleRate,
            ToBytes(samples),
            subFormatTag,
            sampler);

    private static byte[] BuildWave(
        ushort formatTag,
        ushort bitsPerSample,
        ushort channels,
        int sampleRate,
        byte[] payload,
        ushort subFormatTag = 0,
        byte[]? sampler = null)
    {
        var format = new byte[formatTag == 0xFFFE ? 40 : 16];
        BinaryPrimitives.WriteUInt16LittleEndian(format, formatTag);
        BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(2), channels);
        BinaryPrimitives.WriteInt32LittleEndian(format.AsSpan(4), sampleRate);
        BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(14), bitsPerSample);
        if (formatTag == 0xFFFE)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(16), 22);
            BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(24), subFormatTag);
        }

        var body = new List<byte>();
        AppendChunk(body, "fmt ", format);
        if (sampler is not null)
        {
            AppendChunk(body, "smpl", sampler);
        }

        AppendChunk(body, "data", payload);

        var file = new byte[Ngs2RiffDecoder.RiffHeaderSize + body.Count];
        "RIFF"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)(4 + body.Count));
        "WAVE"u8.CopyTo(file.AsSpan(8));
        body.CopyTo(file, Ngs2RiffDecoder.RiffHeaderSize);
        return file;
    }

    private static void AppendChunk(List<byte> body, string id, byte[] payload)
    {
        body.AddRange(System.Text.Encoding.ASCII.GetBytes(id));
        var size = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)payload.Length);
        body.AddRange(size);
        body.AddRange(payload);
        if ((payload.Length & 1) != 0)
        {
            body.Add(0);
        }
    }

    private static byte[] ToBytes(short[] samples)
    {
        var bytes = new byte[samples.Length * sizeof(short)];
        for (var index = 0; index < samples.Length; index++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(
                bytes.AsSpan(index * sizeof(short)),
                samples[index]);
        }

        return bytes;
    }
}

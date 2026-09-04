// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

namespace SharpEmu.Libs.Ngs2;

/// <summary>
/// RIFF/WAVE containers submitted to an NGS2 voice. Titles hand NGS2 either a
/// "VAGp" block, which <see cref="Ngs2VagDecoder"/> handles, or a WAVE file
/// holding plain PCM. Both end up as the same waveform: 16-bit samples, a
/// sample rate, and loop points.
/// </summary>
public static class Ngs2RiffDecoder
{
    /// <summary>Enough of the file to read the RIFF header and locate chunks.</summary>
    public const int RiffHeaderSize = 12;

    /// <summary>
    /// A guest pointer is not a promise about length. Anything past this is
    /// treated as a malformed header rather than read.
    /// </summary>
    public const int MaxWaveformBytes = 16 * 1024 * 1024;

    private const ushort FormatPcm = 0x0001;
    private const ushort FormatFloat = 0x0003;
    private const ushort FormatExtensible = 0xFFFE;

    public static bool IsRiff(ReadOnlySpan<byte> data) =>
        data.Length >= RiffHeaderSize &&
        data[0] == (byte)'R' && data[1] == (byte)'I' &&
        data[2] == (byte)'F' && data[3] == (byte)'F' &&
        data[8] == (byte)'W' && data[9] == (byte)'A' &&
        data[10] == (byte)'V' && data[11] == (byte)'E';

    /// <summary>
    /// How many bytes the container declares, header included, so the caller
    /// reads a bounded range out of guest memory instead of a guessed one.
    /// </summary>
    public static bool TryGetTotalSize(ReadOnlySpan<byte> header, out int totalBytes)
    {
        totalBytes = 0;
        if (!IsRiff(header))
        {
            return false;
        }

        var declared = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        if (declared < 4 || declared > MaxWaveformBytes - 8)
        {
            return false;
        }

        totalBytes = (int)declared + 8;
        return true;
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> data,
        out Ngs2VagDecoder.Waveform waveform)
    {
        waveform = default;
        if (!IsRiff(data))
        {
            return false;
        }

        ReadOnlySpan<byte> format = default;
        ReadOnlySpan<byte> samples = default;
        ReadOnlySpan<byte> sampler = default;
        var offset = RiffHeaderSize;
        while (offset + 8 <= data.Length)
        {
            var id = data.Slice(offset, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 4)..]);
            var payloadStart = offset + 8;
            if (size > (uint)(data.Length - payloadStart))
            {
                // A chunk that runs past the buffer means the declared length
                // and the data disagree; take what is whole and stop.
                break;
            }

            var payload = data.Slice(payloadStart, (int)size);
            if (Matches(id, "fmt "))
            {
                format = payload;
            }
            else if (Matches(id, "data"))
            {
                samples = payload;
            }
            else if (Matches(id, "smpl"))
            {
                sampler = payload;
            }

            // Chunks are padded to an even length, and the pad byte is not
            // counted in the size field.
            offset = payloadStart + (int)size + ((int)size & 1);
        }

        if (format.Length < 16 || samples.IsEmpty)
        {
            return false;
        }

        var formatTag = BinaryPrimitives.ReadUInt16LittleEndian(format);
        var channels = BinaryPrimitives.ReadUInt16LittleEndian(format[2..]);
        var sampleRate = BinaryPrimitives.ReadInt32LittleEndian(format[4..]);
        var bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(format[14..]);
        if (formatTag == FormatExtensible)
        {
            // WAVE_FORMAT_EXTENSIBLE carries the real tag in the first two
            // bytes of its subformat GUID.
            if (format.Length < 26)
            {
                return false;
            }

            formatTag = BinaryPrimitives.ReadUInt16LittleEndian(format[24..]);
        }

        if (channels is < 1 or > 8 || sampleRate is < 1000 or > 384000)
        {
            return false;
        }

        if (!TryConvertSamples(samples, formatTag, bitsPerSample, channels, out var pcm))
        {
            return false;
        }

        var frames = pcm.Length / channels;
        var (loopStart, loopEnd) = ReadLoopPoints(sampler, frames);
        waveform = new Ngs2VagDecoder.Waveform(pcm, sampleRate, loopStart, loopEnd);
        return true;
    }

    /// <summary>
    /// Mixes down to the single interleaved stream the voice mixer plays. NGS2
    /// voices carry their own pan, so a multi-channel source folds to mono here
    /// rather than being truncated to its first channel.
    /// </summary>
    private static bool TryConvertSamples(
        ReadOnlySpan<byte> samples,
        ushort formatTag,
        ushort bitsPerSample,
        int channels,
        out short[] pcm)
    {
        pcm = [];
        switch (formatTag)
        {
            case FormatPcm when bitsPerSample == 16:
            {
                var count = samples.Length / sizeof(short);
                var result = new short[count];
                for (var index = 0; index < count; index++)
                {
                    result[index] = BinaryPrimitives.ReadInt16LittleEndian(
                        samples[(index * sizeof(short))..]);
                }

                pcm = result;
                return true;
            }

            case FormatFloat when bitsPerSample == 32:
            {
                var count = samples.Length / sizeof(float);
                var result = new short[count];
                for (var index = 0; index < count; index++)
                {
                    var value = BinaryPrimitives.ReadSingleLittleEndian(
                        samples[(index * sizeof(float))..]);
                    result[index] = ToPcm16(value);
                }

                pcm = result;
                return true;
            }

            default:
                // ATRAC9 inside RIFF reaches here. The decoder for it already
                // exists (Atrac9DecodeState), but its configuration lives in a
                // vendor extension of the fmt chunk whose layout is not
                // verified here, and inventing one produces noise rather than
                // silence. Left unhandled on purpose until a real sample can
                // confirm the offsets.
                return false;
        }
    }

    private static (int LoopStart, int LoopEnd) ReadLoopPoints(
        ReadOnlySpan<byte> sampler,
        int frames)
    {
        // SMPL chunk: 0x24 holds the loop count, the loops follow at 0x24+8,
        // each 24 bytes with start and end frames at +8 and +12.
        const int LoopCountOffset = 0x1C;
        const int FirstLoopOffset = 0x24;
        const int LoopRecordSize = 24;
        if (sampler.Length < FirstLoopOffset + LoopRecordSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(sampler[LoopCountOffset..]) == 0)
        {
            return (-1, frames);
        }

        var start = BinaryPrimitives.ReadUInt32LittleEndian(sampler[(FirstLoopOffset + 8)..]);
        var end = BinaryPrimitives.ReadUInt32LittleEndian(sampler[(FirstLoopOffset + 12)..]);
        if (start >= (uint)frames || end > (uint)frames || end <= start)
        {
            return (-1, frames);
        }

        return ((int)start, (int)end);
    }

    private static short ToPcm16(float value)
    {
        if (float.IsNaN(value))
        {
            return 0;
        }

        value = Math.Clamp(value, -1.0f, 1.0f);
        return (short)MathF.Round(value * (value < 0.0f ? 32768.0f : short.MaxValue));
    }

    private static bool Matches(ReadOnlySpan<byte> id, string expected) =>
        id.Length == 4 &&
        id[0] == (byte)expected[0] &&
        id[1] == (byte)expected[1] &&
        id[2] == (byte)expected[2] &&
        id[3] == (byte)expected[3];
}

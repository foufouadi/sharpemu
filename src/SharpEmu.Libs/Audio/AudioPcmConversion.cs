// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

namespace SharpEmu.Libs.Audio;

/// <summary>
/// Converts guest AudioOut submissions (mono/stereo/7.1, s16 or float32) into the
/// interleaved stereo 16-bit PCM that host audio streams accept. Platform-neutral —
/// device specifics live behind IHostAudioStream.
/// </summary>
internal static class AudioPcmConversion
{
    /// <summary>Bytes per output frame: two 16-bit channels.</summary>
    public const int OutputFrameSize = 4;

    public static void ConvertToStereoPcm16(
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        int frames,
        int channels,
        int bytesPerSample,
        bool isFloat,
        float volume)
    {
        var sourceFrameSize = checked(channels * bytesPerSample);
        // Volume is constant for the whole submission, so clamp it once here
        // rather than per sample inside the loop (this runs on every real-time
        // audio buffer, hundreds of frames at a time).
        var clampedVolume = Math.Clamp(volume, 0.0f, 1.0f);
        for (var frame = 0; frame < frames; frame++)
        {
            var sourceFrame = source.Slice(frame * sourceFrameSize, sourceFrameSize);
            var (left, right) = DownmixFrame(sourceFrame, channels, bytesPerSample, isFloat);
            left = ApplyVolume(left, clampedVolume);
            right = ApplyVolume(right, clampedVolume);
            BinaryPrimitives.WriteInt16LittleEndian(destination[(frame * OutputFrameSize)..], left);
            BinaryPrimitives.WriteInt16LittleEndian(destination[((frame * OutputFrameSize) + 2)..], right);
        }
    }

    /// <summary>
    /// Copies interleaved PCM without changing its channel layout. SDL can convert
    /// this directly to the physical device, which preserves surround mixes that
    /// would otherwise be truncated to the first two guest channels.
    /// </summary>
    public static void CopyWithVolume(
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        bool isFloat,
        float volume)
    {
        var clampedVolume = Math.Clamp(volume, 0.0f, 1.0f);
        if (clampedVolume >= 1.0f)
        {
            source.CopyTo(destination);
            return;
        }

        if (isFloat)
        {
            for (var offset = 0; offset < source.Length; offset += sizeof(float))
            {
                var sample = BinaryPrimitives.ReadSingleLittleEndian(source.Slice(offset, sizeof(float)));
                BinaryPrimitives.WriteSingleLittleEndian(
                    destination.Slice(offset, sizeof(float)),
                    sample * clampedVolume);
            }

            return;
        }

        for (var offset = 0; offset < source.Length; offset += sizeof(short))
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(source.Slice(offset, sizeof(short)));
            BinaryPrimitives.WriteInt16LittleEndian(
                destination.Slice(offset, sizeof(short)),
                ApplyVolume(sample, clampedVolume));
        }
    }

    // Surround gain for the channels that fold into both sides. -3 dB is the
    // usual coefficient: it keeps the summed power of a source panned to two
    // speakers equal to the same source on one.
    private const float SurroundGain = 0.7071068f;
    // LFE is deliberately dropped rather than folded in. It carries content an
    // octave below what most stereo playback reproduces, and summing it in at
    // unity is what makes a downmix sound like it is clipping.
    private const float LowFrequencyGain = 0.0f;
    // Summing the folded channels at their own gain overshoots: one side of a
    // 7.1 frame reaches 1 + 3 x 0.7071 = 3.12, and the conversion to PCM16
    // clamps, so loud passages came out distorted rather than merely loud.
    // Divide by the sum of the coefficients that side uses, which is what
    // ffmpeg's downmix does by default (its "normalize" option) and what makes
    // a full-scale input land exactly at full scale instead of past it.
    private const float SurroundNormalize = 1.0f / (1.0f + (2.0f * SurroundGain));
    private const float FullSurroundNormalize = 1.0f / (1.0f + (3.0f * SurroundGain));

    // Guest layouts, in the interleave order AudioOut submits:
    //   1 channel  mono
    //   2 channels front left, front right
    //   6 channels front left, front right, centre, LFE, back left, back right
    //   8 channels the same, then side left, side right
    // Anything else falls back to the first two channels, which is what a
    // layout we cannot name would have got anyway.
    private static (short Left, short Right) DownmixFrame(
        ReadOnlySpan<byte> frame,
        int channels,
        int bytesPerSample,
        bool isFloat)
    {
        var left = ReadSampleFloat(frame, 0, bytesPerSample, isFloat);
        if (channels == 1)
        {
            var mono = ConvertFloatSample(left);
            return (mono, mono);
        }

        var right = ReadSampleFloat(frame, 1, bytesPerSample, isFloat);
        if (channels is 6 or 8)
        {
            var centre = ReadSampleFloat(frame, 2, bytesPerSample, isFloat);
            var lowFrequency = ReadSampleFloat(frame, 3, bytesPerSample, isFloat);
            var backLeft = ReadSampleFloat(frame, 4, bytesPerSample, isFloat);
            var backRight = ReadSampleFloat(frame, 5, bytesPerSample, isFloat);
            var shared = (SurroundGain * centre) + (LowFrequencyGain * lowFrequency);
            left += shared + (SurroundGain * backLeft);
            right += shared + (SurroundGain * backRight);
            var normalize = SurroundNormalize;
            if (channels == 8)
            {
                left += SurroundGain * ReadSampleFloat(frame, 6, bytesPerSample, isFloat);
                right += SurroundGain * ReadSampleFloat(frame, 7, bytesPerSample, isFloat);
                normalize = FullSurroundNormalize;
            }

            left *= normalize;
            right *= normalize;
        }

        return (ConvertFloatSample(left), ConvertFloatSample(right));
    }

    private static float ReadSampleFloat(
        ReadOnlySpan<byte> frame,
        int channel,
        int bytesPerSample,
        bool isFloat)
    {
        var sample = frame.Slice(channel * bytesPerSample, bytesPerSample);
        if (isFloat)
        {
            var bits = BinaryPrimitives.ReadInt32LittleEndian(sample);
            var value = BitConverter.Int32BitsToSingle(bits);
            return float.IsNaN(value) ? 0.0f : value;
        }

        var pcm = BinaryPrimitives.ReadInt16LittleEndian(sample);
        return pcm / (pcm < 0 ? 32768.0f : short.MaxValue);
    }

    private static short ConvertFloatSample(float value)
    {
        if (float.IsNaN(value))
        {
            return 0;
        }

        value = Math.Clamp(value, -1.0f, 1.0f);
        var scale = value < 0.0f ? 32768.0f : short.MaxValue;
        return checked((short)MathF.Round(value * scale));
    }

    // <paramref name="volume"/> is expected pre-clamped to [0, 1] by the caller.
    private static short ApplyVolume(short sample, float volume)
    {
        var scaled = MathF.Round(sample * volume);
        return (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

/// <summary>
/// Folding a surround submission down to the stereo pair a host device wants.
/// Taking the first two channels drops the centre, and with it most dialogue.
/// </summary>
public sealed class AudioSurroundDownmixTests
{
    private const float SurroundGain = 0.7071068f;
    // The folded result is divided by the sum of the coefficients that side
    // uses, so a full-scale input lands at full scale instead of three times
    // past it. 5.1 folds centre and one back channel; 7.1 adds a side channel.
    private const float SurroundNormalize = 1.0f / (1.0f + (2.0f * SurroundGain));
    private const float FullSurroundNormalize = 1.0f / (1.0f + (3.0f * SurroundGain));

    private static float Normalize(int channels) =>
        channels == 8 ? FullSurroundNormalize : SurroundNormalize;

    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    public void CentreChannelReachesBothSides(int channels)
    {
        // Only the centre carries signal: a downmix that ignores it is silent.
        var frame = new float[channels];
        frame[2] = 0.5f;

        var (left, right) = Convert(frame);

        var expected = Quantize(SurroundGain * 0.5f * Normalize(channels));
        Assert.Equal(expected, left);
        Assert.Equal(expected, right);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    public void BackChannelsStayOnTheirOwnSide(int channels)
    {
        var frame = new float[channels];
        frame[4] = 0.5f;

        var (left, right) = Convert(frame);

        Assert.Equal(Quantize(SurroundGain * 0.5f * Normalize(channels)), left);
        Assert.Equal(0, right);
    }

    [Fact]
    public void SideChannelsAreFoldedOnlyForEightChannels()
    {
        var frame = new float[8];
        frame[6] = 0.5f;

        var (left, right) = Convert(frame);

        Assert.Equal(Quantize(SurroundGain * 0.5f * FullSurroundNormalize), left);
        Assert.Equal(0, right);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    public void LowFrequencyIsNotFoldedIn(int channels)
    {
        // Content an octave below what stereo playback reproduces, and summing
        // it at unity is what makes a downmix sound clipped.
        var frame = new float[channels];
        frame[3] = 1.0f;

        var (left, right) = Convert(frame);

        Assert.Equal(0, left);
        Assert.Equal(0, right);
    }

    [Fact]
    public void StereoIsPassedThroughUnchanged()
    {
        var (left, right) = Convert([0.5f, -0.25f]);

        Assert.Equal(Quantize(0.5f), left);
        Assert.Equal(Quantize(-0.25f), right);
    }

    [Fact]
    public void MonoIsDuplicatedToBothSides()
    {
        var (left, right) = Convert([0.5f]);

        Assert.Equal(Quantize(0.5f), left);
        Assert.Equal(Quantize(0.5f), right);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    public void EveryFoldedChannelAtFullScaleLandsExactlyAtFullScale(int channels)
    {
        // Without the normalisation this side summed to 2.41 (5.1) or 3.12
        // (7.1) and the conversion clamped, so a loud passage came out
        // distorted rather than loud. The whole point of dividing by the sum of
        // the coefficients is that this case reaches the ceiling and no
        // further.
        var frame = new float[channels];
        frame[0] = 1.0f;
        frame[2] = 1.0f;
        frame[4] = 1.0f;
        if (channels == 8)
        {
            frame[6] = 1.0f;
        }

        var (left, _) = Convert(frame);

        Assert.Equal(short.MaxValue, left);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    public void HalfScaleOnEveryFoldedChannelStaysBelowTheCeiling(int channels)
    {
        // The companion to the case above: clamping at full scale is only
        // meaningful if the level below it is not clamped too. Pinned so a
        // normalisation that is merely a hard limiter would fail here.
        var frame = new float[channels];
        frame[0] = 0.5f;
        frame[2] = 0.5f;
        frame[4] = 0.5f;
        if (channels == 8)
        {
            frame[6] = 0.5f;
        }

        var (left, _) = Convert(frame);

        Assert.Equal(Quantize(0.5f), left);
    }

    private static short Quantize(float value)
    {
        value = Math.Clamp(value, -1.0f, 1.0f);
        return (short)MathF.Round(value * (value < 0.0f ? 32768.0f : short.MaxValue));
    }

    private static (short Left, short Right) Convert(float[] frame)
    {
        var source = new byte[frame.Length * sizeof(float)];
        for (var index = 0; index < frame.Length; index++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(
                source.AsSpan(index * sizeof(float)),
                frame[index]);
        }

        var destination = new byte[AudioPcmConversion.OutputFrameSize];
        AudioPcmConversion.ConvertToStereoPcm16(
            source,
            destination,
            frames: 1,
            channels: frame.Length,
            bytesPerSample: sizeof(float),
            isFloat: true,
            volume: 1.0f);

        return (
            BinaryPrimitives.ReadInt16LittleEndian(destination),
            BinaryPrimitives.ReadInt16LittleEndian(destination.AsSpan(2)));
    }
}

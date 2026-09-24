// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Rendering;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Rendering;

public sealed class DispatchSplitTests
{
    private static readonly (uint X, uint Y, uint Z) CommonLimit = (65535, 65535, 65535);

    [Fact]
    public void AGridWithinTheLimitsIsOneDispatch()
    {
        Assert.True(DispatchSplit.Fits(65535, 65535, 65535, CommonLimit));
        Assert.False(DispatchSplit.Fits(65536, 1, 1, CommonLimit));
        Assert.False(DispatchSplit.Fits(1, 1, 65536, CommonLimit));
    }

    [Fact]
    public void ALongAxisSplitsIntoBasedTiles()
    {
        var chunks = DispatchSplit.Chunks(131_073, 2, 1, CommonLimit).ToArray();

        Assert.Equal(
            [
                new DispatchChunk(0, 0, 0, 65535, 2, 1),
                new DispatchChunk(65535, 0, 0, 65535, 2, 1),
                new DispatchChunk(131_070, 0, 0, 3, 2, 1),
            ],
            chunks);
    }

    [Theory]
    [InlineData(10u, 7u, 5u, 4u, 3u, 2u)]
    [InlineData(1u, 1u, 9u, 1u, 1u, 4u)]
    [InlineData(uint.MaxValue, 1u, 1u, 0x8000_0000u, 1u, 1u)]
    public void TilesCoverTheGridExactlyOnceWithinTheLimits(uint x, uint y, uint z, uint limitX, uint limitY, uint limitZ)
    {
        var limit = (limitX, limitY, limitZ);
        ulong covered = 0;
        var seen = new HashSet<(uint, uint, uint)>();
        foreach (var chunk in DispatchSplit.Chunks(x, y, z, limit))
        {
            Assert.True(DispatchSplit.Fits(chunk.CountX, chunk.CountY, chunk.CountZ, limit));
            Assert.True(chunk.CountX > 0 && chunk.CountY > 0 && chunk.CountZ > 0);
            Assert.True((ulong)chunk.BaseX + chunk.CountX <= x && (ulong)chunk.BaseY + chunk.CountY <= y && (ulong)chunk.BaseZ + chunk.CountZ <= z);
            Assert.True(seen.Add((chunk.BaseX, chunk.BaseY, chunk.BaseZ)));
            covered += (ulong)chunk.CountX * chunk.CountY * chunk.CountZ;
        }

        Assert.Equal((ulong)x * y * z, covered);
    }

    [Fact]
    public void AnEmptyGridHasNoTiles() =>
        Assert.Empty(DispatchSplit.Chunks(0, 4, 4, CommonLimit));
}

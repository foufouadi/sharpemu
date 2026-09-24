// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Rendering;

public readonly record struct DispatchChunk(uint BaseX, uint BaseY, uint BaseZ, uint CountX, uint CountY, uint CountZ);

// The guest can dispatch up to 2^32 - 1 groups on every axis, a Vulkan device only up to its
// maxComputeWorkGroupCount. A larger grid runs as vkCmdDispatchBase calls over tiles that fit;
// WorkgroupId includes the base, so every group sees the index it has in the whole grid.
public static class DispatchSplit
{
    public static bool Fits(uint groupsX, uint groupsY, uint groupsZ, (uint X, uint Y, uint Z) limit) =>
        groupsX <= limit.X && groupsY <= limit.Y && groupsZ <= limit.Z;

    public static IEnumerable<DispatchChunk> Chunks(uint groupsX, uint groupsY, uint groupsZ, (uint X, uint Y, uint Z) limit)
    {
        if (limit.X == 0 || limit.Y == 0 || limit.Z == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Every workgroup count limit must be non-zero.");
        }

        for (ulong z = 0; z < groupsZ; z += limit.Z)
        {
            for (ulong y = 0; y < groupsY; y += limit.Y)
            {
                for (ulong x = 0; x < groupsX; x += limit.X)
                {
                    yield return new DispatchChunk(
                        (uint)x, (uint)y, (uint)z,
                        (uint)Math.Min(limit.X, groupsX - x),
                        (uint)Math.Min(limit.Y, groupsY - y),
                        (uint)Math.Min(limit.Z, groupsZ - z));
                }
            }
        }
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler;

/// <summary>
/// Maps a guest compute workgroup shape onto the host axes, shared by codegen and
/// the host dispatch validator so the two cannot disagree about which axis a
/// workgroup was declared on.
/// </summary>
public static class Gen5ComputeWorkgroupLayout
{
    // Vulkan guarantees maxComputeWorkGroupSize of at least (128, 128, 64), and
    // real drivers keep X the most generous axis: NVIDIA reports 1024x1024x64.
    // A guest group is free to put a long extent on Y or Z, which the guest
    // hardware allows and the host then refuses.
    private const uint GuaranteedY = 128;
    private const uint GuaranteedZ = 64;

    /// <summary>
    /// Moves a guest workgroup's only non-unit extent onto the host X axis when it
    /// is longer than the host is guaranteed to accept on the axis the guest chose.
    /// Restricting the move to that shape is what makes it safe: with every other
    /// extent 1, guest and host linearise their invocations in the same sequence,
    /// so LocalInvocationIndex, wave membership and lane order all survive it.
    /// Any other over-long shape is left alone and keeps failing loudly rather
    /// than being silently reordered.
    /// </summary>
    /// <param name="guestAxis">Guest axis the extent came from: 1 for Y, 2 for Z.</param>
    public static bool TryMoveLongAxisToX(
        uint sizeX,
        uint sizeY,
        uint sizeZ,
        out uint hostSizeX,
        out int guestAxis)
    {
        hostSizeX = sizeX;
        guestAxis = 0;
        if (sizeX != 1)
        {
            return false;
        }

        if (sizeY == 1 && sizeZ > GuaranteedZ)
        {
            hostSizeX = sizeZ;
            guestAxis = 2;
            return true;
        }

        if (sizeZ == 1 && sizeY > GuaranteedY)
        {
            hostSizeX = sizeY;
            guestAxis = 1;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The host workgroup size a guest shape is declared with, after any move.
    /// </summary>
    public static (uint X, uint Y, uint Z) GetHostWorkgroupSize(
        uint sizeX,
        uint sizeY,
        uint sizeZ) =>
        TryMoveLongAxisToX(sizeX, sizeY, sizeZ, out var hostSizeX, out _)
            ? (hostSizeX, 1u, 1u)
            : (sizeX, sizeY, sizeZ);
}

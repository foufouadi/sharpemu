// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler;

// Runtime ABI shared by the merged hull kernel and the native domain pipeline.
public static class Gen5TessellationData
{
    public const uint DwordCount = 15;
    public const uint FirstPatch = 0, PatchCount = 1, VertexOffset = 2, InstanceId = 3;
    public const uint IndexAddress = 4, IndexSize = 6;
    public const uint FactorAddress = 7, FactorBytes = 9;
    public const uint OffchipOffset = 10, FactorOffset = 11;
    public const uint MinimumLevel = 12, MaximumLevel = 13;
    public const uint IndexByteOffset = 14;
}

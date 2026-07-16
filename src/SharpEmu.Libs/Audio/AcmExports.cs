// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Threading;

namespace SharpEmu.Libs.Audio;

// ACM (Audio Codec Memory) manages the direct-memory pool the PS5 audio
// stack decodes into. The emulator's codecs run on the host and allocate
// host-side, so a context here is bookkeeping only — but creation must
// succeed: Ghost of Yotei's audio arena bootstrap aborts when any step of
// the ACM bring-up chain fails, leaving its global arena table null, and the
// mastering path dereferences that table unconditionally later.
public static class AcmExports
{
    private static int _nextContextId;

    // sceAcmContextCreate(context* out, config*, flags, directMemoryStart,
    // directMemorySize). The caller reads back a 32-bit context id from *out
    // and treats any nonzero return as fatal.
    [SysAbiExport(
        Nid = "ZIXln2K3XMk",
        ExportName = "sceAcmContextCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int AcmContextCreate(CpuContext ctx)
    {
        var contextAddress = ctx[CpuRegister.Rdi];
        if (contextAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var contextId = unchecked((uint)Interlocked.Increment(ref _nextContextId));
        if (!ctx.TryWriteUInt32(contextAddress, contextId))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "jBgBjAj02R8",
        ExportName = "sceAcmContextDestroy",
        Target = Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int AcmContextDestroy(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Ime;

public static class ImeExports
{
    // Quake (KEX) calls this from its main loop and from the audio bring-up path with
    // an event-handler pointer. No IME session ever exists here, so report success
    // without invoking the handler ("no pending IME events"). This NID was previously
    // misbound as an sceNgs2VoiceControl alias, which fed the game NGS2 errors.
    [SysAbiExport(
        Nid = "-4GCfYdNF1s",
        ExportName = "sceImeUpdate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeUpdate(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "eaFXjfJv3xs",
        ExportName = "sceImeKeyboardOpen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeKeyboardOpen(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // Reports the state of an attached hardware keyboard. There is none, so
    // the structure is zeroed: status 0 is "not connected", which is the honest
    // answer and the one a title checks before offering keyboard entry.
    // Leaving the caller's buffer untouched would let it read whatever was
    // already there and decide a keyboard is present.
    [SysAbiExport(
        Nid = "VkqLPArfFdc",
        ExportName = "sceImeKeyboardGetInfo",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeKeyboardGetInfo(CpuContext ctx)
    {
        var infoAddress = ctx[CpuRegister.Rsi];
        if (infoAddress != 0)
        {
            Span<byte> info = stackalloc byte[KeyboardInfoBytes];
            info.Clear();
            _ = ctx.Memory.TryWrite(infoAddress, info);
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // Size of OrbisImeKeyboardInfo. Not derived from a published header; it is
    // the extent this stub clears.
    private const int KeyboardInfoBytes = 0x88;

    [SysAbiExport(
        Nid = "dKadqZFgKKQ",
        ExportName = "sceImeKeyboardGetResourceId",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeKeyboardGetResourceId(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
}

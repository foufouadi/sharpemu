// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using System.Buffers.Binary;

namespace SharpEmu.Libs.SystemService;

public static class SystemServiceExports
{
    private const int OrbisSystemServiceErrorParameter = unchecked((int)0x80A10003);
    // No system-service event pending. Numeric value and the struct-zeroing/
    // event_type=-1 behavior below are cross-checked against Kyty
    // (https://github.com/KytyPS5/KytyPS5, GPL-2.0-or-later, same as this
    // project -- commit 05d14f54210d6742afa845f7f1a974d7af8b47e1,
    // src/libs/libSystemService.cpp SystemServiceReceiveEvent() and
    // src/libs/errno.h SYSTEM_SERVICE_ERROR_NO_EVENT) -- SharpEmu had no
    // reference for this value; this codebase's other libSceSystemService
    // error constants (e.g. OrbisSystemServiceErrorParameter above) already
    // follow the same 0x80A100xx family, so 0x80A10004 fits.
    private const int OrbisSystemServiceErrorNoEvent = unchecked((int)0x80A10004);
    private const int SystemServiceEventSize = 4 + 8192; // int32 event_type + uint8[8192] data
    private const int SystemServiceStatusSize = 0x0C;
    private const int DisplaySafeAreaInfoSize = sizeof(float) + 128;
    private const int HdrToneMapLuminanceSize = sizeof(float) * 3;

    private const int TitleIdFieldSize = 0x10;

    private static string? _mainAppTitleId;
    private static int _noticeScreenSkipFlag;

    public static void ConfigureApplicationInfo(string? titleId)
    {
        _mainAppTitleId = string.IsNullOrWhiteSpace(titleId) ? null : titleId.Trim();
    }

    [SysAbiExport(
        Nid = "3RQ5aQfnstU",
        ExportName = "sceSystemServiceGetNoticeScreenSkipFlag",
        Target = Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceGetNoticeScreenSkipFlag(CpuContext ctx)
    {
        var flagAddress = ctx[CpuRegister.Rdi];
        if (flagAddress == 0)
        {
            return ctx.SetReturn(OrbisSystemServiceErrorParameter);
        }

        // Keep the flag state even though the emulator does not display the
        // system notice screen. Titles use this service as a normal preference
        // store and expect a later get to observe the value they set.
        Span<byte> flagBytes = stackalloc byte[1];
        flagBytes[0] = unchecked((byte)Volatile.Read(ref _noticeScreenSkipFlag));
        return ctx.Memory.TryWrite(flagAddress, flagBytes)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "8Lo6Zv94aho",
        ExportName = "sceSystemServiceDisableNoticeScreenSkipFlagAutoSet",
        Target = Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceDisableNoticeScreenSkipFlagAutoSet(CpuContext ctx) =>
        ctx.SetReturn(0);

    // Settings entry calls this immediately before spawning SaveModTime/Load
    // threads. An unresolved stub returns NOT_FOUND and the title can stall in
    // that path; accept the write and report success.
    [SysAbiExport(
        Nid = "Q3utJvma4Mo",
        ExportName = "sceSystemServiceSetNoticeScreenSkipFlag",
        Target = Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceSetNoticeScreenSkipFlag(CpuContext ctx)
    {
        // The native API takes the flag value in the first argument. Treat any
        // non-zero value as true, matching the bool-like PS5 ABI.
        Volatile.Write(ref _noticeScreenSkipFlag, ctx[CpuRegister.Rdi] != 0 ? 1 : 0);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "4veE0XiIugA",
        ExportName = "sceSystemServiceGetMainAppTitleId",
        Target = Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceGetMainAppTitleId(CpuContext ctx)
    {
        var titleIdAddress = ctx[CpuRegister.Rdi];
        if (titleIdAddress == 0)
        {
            return ctx.SetReturn(OrbisSystemServiceErrorParameter);
        }

        // Title IDs are a fixed 9-char format written into a 0x10-byte field;
        // bound the length so a malformed param.json cannot drive an unbounded
        // stack allocation or overrun the guest buffer.
        var titleId = _mainAppTitleId ?? "PPSA00000";
        var length = Math.Min(titleId.Length, TitleIdFieldSize - 1);
        Span<byte> titleIdBytes = stackalloc byte[TitleIdFieldSize];
        titleIdBytes.Clear();
        System.Text.Encoding.ASCII.GetBytes(titleId.AsSpan(0, length), titleIdBytes);
        return ctx.Memory.TryWrite(titleIdAddress, titleIdBytes[..(length + 1)])
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "fZo48un7LK4",
        ExportName = "sceSystemServiceParamGetInt",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceParamGetInt(CpuContext ctx)
    {
        var parameterId = unchecked((int)ctx[CpuRegister.Rdi]);
        var valueAddress = ctx[CpuRegister.Rsi];
        if (valueAddress == 0)
        {
            return ctx.SetReturn(OrbisSystemServiceErrorParameter);
        }

        var value = parameterId switch
        {
            1 or 2 or 3 or 1000 => 1,
            4 => 180,
            _ => 0,
        };

        Span<byte> valueBytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(valueBytes, value);
        return ctx.Memory.TryWrite(valueAddress, valueBytes)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "SsC-m-S9JTA",
        ExportName = "sceSystemServiceParamGetString",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceParamGetString(CpuContext ctx)
    {
        _ = unchecked((int)ctx[CpuRegister.Rdi]); // parameter id (nickname, etc.)
        var bufferAddress = ctx[CpuRegister.Rsi];
        var bufferSize = unchecked((int)ctx[CpuRegister.Rdx]);
        if (bufferAddress == 0 || bufferSize <= 0)
        {
            return ctx.SetReturn(OrbisSystemServiceErrorParameter);
        }

        // String params are typically the user nickname. Callers that gate UI
        // or text setup on a successful read (and skip it on failure) stall on
        // a black screen when this returns NOT_FOUND, so return a neutral
        // non-empty default and success.
        var value = System.Text.Encoding.UTF8.GetBytes("SharpEmu");
        var writeLength = Math.Min(value.Length, bufferSize - 1);
        Span<byte> output = stackalloc byte[writeLength + 1];
        value.AsSpan(0, writeLength).CopyTo(output);
        output[writeLength] = 0;
        return ctx.Memory.TryWrite(bufferAddress, output)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "rPo6tV8D9bM",
        ExportName = "sceSystemServiceGetStatus",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceGetStatus(CpuContext ctx)
    {
        var statusAddress = ctx[CpuRegister.Rdi];
        if (statusAddress == 0)
        {
            return ctx.SetReturn(OrbisSystemServiceErrorParameter);
        }

        Span<byte> status = stackalloc byte[SystemServiceStatusSize];
        status.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(status, 0);
        status[0x06] = 1;

        return ctx.Memory.TryWrite(statusAddress, status)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "656LMQSrg6U",
        ExportName = "sceSystemServiceReceiveEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceReceiveEvent(CpuContext ctx)
    {
        var eventAddress = ctx[CpuRegister.Rdi];
        if (eventAddress == 0)
        {
            return ctx.SetReturn(OrbisSystemServiceErrorParameter);
        }

        // SharpEmu doesn't generate any real system-service events (suspend/
        // resume, notice screens, etc.) yet -- "no event pending" is correct
        // in every case until one is actually implemented. Zeroing the whole
        // struct and setting event_type=-1 matches the real SDK's own
        // documented idiom of leaving *event untouched/cleared on this
        // specific error, cross-checked against Kyty's implementation.
        Span<byte> guestEvent = stackalloc byte[SystemServiceEventSize];
        guestEvent.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(guestEvent, -1);

        return ctx.Memory.TryWrite(eventAddress, guestEvent)
            ? ctx.SetReturn(OrbisSystemServiceErrorNoEvent)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "1n37q1Bvc5Y",
        ExportName = "sceSystemServiceGetDisplaySafeAreaInfo",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceGetDisplaySafeAreaInfo(CpuContext ctx)
    {
        var infoAddress = ctx[CpuRegister.Rdi];
        if (infoAddress == 0)
        {
            return ctx.SetReturn(OrbisSystemServiceErrorParameter);
        }

        Span<byte> info = stackalloc byte[DisplaySafeAreaInfoSize];
        info.Clear();
        BinaryPrimitives.WriteSingleLittleEndian(info, 1.0f);

        return ctx.Memory.TryWrite(infoAddress, info)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "mPpPxv5CZt4",
        ExportName = "sceSystemServiceGetHdrToneMapLuminance",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceGetHdrToneMapLuminance(CpuContext ctx)
    {
        var luminanceAddress = ctx[CpuRegister.Rdi];
        if (luminanceAddress == 0)
        {
            return ctx.SetReturn(OrbisSystemServiceErrorParameter);
        }

        Span<byte> luminance = stackalloc byte[HdrToneMapLuminanceSize];
        BinaryPrimitives.WriteSingleLittleEndian(luminance, 1000.0f);
        BinaryPrimitives.WriteSingleLittleEndian(luminance[sizeof(float)..], 1000.0f);
        BinaryPrimitives.WriteSingleLittleEndian(luminance[(sizeof(float) * 2)..], 0.01f);
        return ctx.Memory.TryWrite(luminanceAddress, luminance)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "Vo5V8KAwCmk",
        ExportName = "sceSystemServiceHideSplashScreen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceHideSplashScreen(CpuContext ctx)
    {
        GuestGpu.Current.HideSplashScreen();
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "3s8cHiCBKBE",
        ExportName = "sceSystemServiceReportAbnormalTermination",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceReportAbnormalTermination(CpuContext ctx) => ctx.SetReturn(0);

    internal static void ResetForTests() =>
        Volatile.Write(ref _noticeScreenSkipFlag, 0);
}

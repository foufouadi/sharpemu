// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using SharpEmu.HLE;

namespace SharpEmu.Libs.LibcTime;

// time(3)/localtime(3)/asctime(3) -- standard, fully-specified signatures,
// no ABI guessing (unlike sceLibcMspaceMalloc/Free elsewhere this
// session). Found needing these on Astro Bot 01.018 right after the
// sceLibcMspaceMemalign/puts/rand fixes let boot progress further.
//
// struct tm layout is assumed to be the plain POSIX/ISO C form -- 9 ints
// (tm_sec, tm_min, tm_hour, tm_mday, tm_mon, tm_year, tm_wday, tm_yday,
// tm_isdst), 36 bytes, no BSD tm_gmtoff/tm_zone extension fields. This is
// the near-universal interop layout; localtime here always treats the
// guest's time_t as UTC (no host-timezone dependency) and reports
// tm_isdst=0, so nothing here would expose a BSD-specific field to guest
// code that depends on it. Revisit if a title turns out to actually read
// tm_gmtoff/tm_zone.
public static class LibcTimeExports
{
    private const int TmFieldCount = 9;
    private const int TmSize = TmFieldCount * sizeof(int);

    [SysAbiExport(
        Nid = "wLlFkwG9UcQ",
        ExportName = "time",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Time(CpuContext ctx)
    {
        var tlocAddress = ctx[CpuRegister.Rdi];
        var seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (tlocAddress != 0)
        {
            ctx.TryWriteUInt64(tlocAddress, unchecked((ulong)seconds));
        }

        ctx[CpuRegister.Rax] = unchecked((ulong)seconds);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // Per-guest-thread static buffers, mirroring real localtime(3)/
    // asctime(3)'s own "returns a pointer to internal static storage,
    // overwritten by the next call" contract -- keyed the same way
    // strtok's saved position is (LibcStdioExports.cs) so two guest
    // threads don't stomp on each other's result.
    private static readonly ConcurrentDictionary<ulong, ulong> _tmBuffers = new();
    private static readonly ConcurrentDictionary<ulong, ulong> _asctimeBuffers = new();

    [SysAbiExport(
        Nid = "efhK-YSUYYQ",
        ExportName = "localtime",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Localtime(CpuContext ctx)
    {
        var timerAddress = ctx[CpuRegister.Rdi];
        if (timerAddress == 0 || !ctx.TryReadUInt64(timerAddress, out var rawSeconds))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        DateTime local;
        try
        {
            local = DateTimeOffset.FromUnixTimeSeconds(unchecked((long)rawSeconds)).UtcDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var bufferAddress = GetOrAllocatePerThreadBuffer(ctx, _tmBuffers, TmSize);
        if (bufferAddress == 0 || !TryWriteTm(ctx, bufferAddress, local))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = bufferAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "jT3xiGpA3B4",
        ExportName = "asctime",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Asctime(CpuContext ctx)
    {
        var tmAddress = ctx[CpuRegister.Rdi];
        if (tmAddress == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        Span<byte> bytes = stackalloc byte[TmSize];
        if (!ctx.Memory.TryRead(tmAddress, bytes))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        Span<int> fields = stackalloc int[TmFieldCount];
        for (var i = 0; i < fields.Length; i++)
        {
            fields[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(i * 4, 4));
        }

        DateTime dt;
        try
        {
            dt = new DateTime(fields[5] + 1900, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                .AddMonths(fields[4])
                .AddDays(fields[3] - 1)
                .AddHours(fields[2])
                .AddMinutes(fields[1])
                .AddSeconds(fields[0]);
        }
        catch (ArgumentOutOfRangeException)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        // Exact asctime(3) format: "Www Mmm dd hh:mm:ss yyyy\n\0" -- dd is
        // SPACE-padded (not zero-padded) per the C standard, unlike every
        // other field here.
        var dayText = dt.Day.ToString(CultureInfo.InvariantCulture).PadLeft(2);
        var text = $"{dt.ToString("ddd MMM", CultureInfo.InvariantCulture)} {dayText} " +
                   $"{dt.ToString("HH:mm:ss yyyy", CultureInfo.InvariantCulture)}\n";

        var bufferAddress = GetOrAllocatePerThreadBuffer(ctx, _asctimeBuffers, 32);
        if (bufferAddress == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var payload = Encoding.ASCII.GetBytes(text);
        Span<byte> terminator = stackalloc byte[1];
        if (!ctx.Memory.TryWrite(bufferAddress, payload) ||
            !ctx.Memory.TryWrite(bufferAddress + (ulong)payload.Length, terminator))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = bufferAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static ulong GetOrAllocatePerThreadBuffer(CpuContext ctx, ConcurrentDictionary<ulong, ulong> buffers, int size)
    {
        var threadHandle = GuestThreadExecution.CurrentGuestThreadHandle;
        return buffers.GetOrAdd(threadHandle, _ =>
        {
            if (ctx.Memory is not IGuestMemoryAllocator allocator ||
                !allocator.TryAllocateGuestMemory((ulong)size, 0x8, out var address))
            {
                return 0;
            }

            return address;
        });
    }

    private static bool TryWriteTm(CpuContext ctx, ulong address, DateTime dt)
    {
        Span<int> fields = stackalloc int[TmFieldCount];
        fields[0] = dt.Second;
        fields[1] = dt.Minute;
        fields[2] = dt.Hour;
        fields[3] = dt.Day;
        fields[4] = dt.Month - 1;
        fields[5] = dt.Year - 1900;
        fields[6] = (int)dt.DayOfWeek; // .NET Sunday=0 matches tm_wday exactly
        fields[7] = dt.DayOfYear - 1;
        fields[8] = 0; // tm_isdst -- always "not in effect", matches the UTC treatment above

        Span<byte> bytes = stackalloc byte[TmSize];
        for (var i = 0; i < fields.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(i * 4, 4), fields[i]);
        }

        return ctx.Memory.TryWrite(address, bytes);
    }
}

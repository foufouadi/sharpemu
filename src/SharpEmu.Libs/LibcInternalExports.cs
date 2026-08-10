// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE;

namespace SharpEmu.Libs.LibcInternal;

public static class LibcInternalExports
{
    private const ulong HeapTraceInfoSize = 32;
    private const int HeapTraceTableEntryCount = 64;
    private const int HeapTraceMaskOffset = 0;
    private const int HeapTraceTableOffset = HeapTraceMaskOffset + sizeof(ulong);
    private const int HeapTraceStorageSize = HeapTraceTableOffset + (HeapTraceTableEntryCount * sizeof(ulong));

    private static readonly object _heapTraceGate = new();
    private static nint _heapTraceStorage;

    [SysAbiExport(
        Nid = "NWtTN10cJzE",
        ExportName = "sceLibcHeapGetTraceInfo",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "LibcInternalExt")]
    public static int LibcHeapGetTraceInfo(CpuContext ctx)
    {
        var infoAddress = ctx[CpuRegister.Rdi];
        if (infoAddress == 0 || !ctx.TryReadUInt64(infoAddress, out var size) || size != HeapTraceInfoSize)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var storage = EnsureHeapTraceStorage();
        if (storage == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var maskAddress = unchecked((ulong)(storage + HeapTraceMaskOffset));
        var tableAddress = unchecked((ulong)(storage + HeapTraceTableOffset));
        if (!ctx.TryWriteUInt64(infoAddress + 16, maskAddress) ||
            !ctx.TryWriteUInt64(infoAddress + 24, tableAddress))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static nint EnsureHeapTraceStorage()
    {
        lock (_heapTraceGate)
        {
            if (_heapTraceStorage != 0)
            {
                return _heapTraceStorage;
            }

            var storage = Marshal.AllocHGlobal(HeapTraceStorageSize);
            if (storage == 0)
            {
                return 0;
            }

            unsafe
            {
                NativeMemory.Clear((void*)storage, (nuint)HeapTraceStorageSize);
            }

            _heapTraceStorage = storage;
            return storage;
        }
    }

    // ABI is a documented guess, not a confirmed Sony header -- see the
    // 2026-08-10 astrobot-poison-store-fix session notes. Neither Kyty
    // (InoriRus/Kyty, MIT) nor KytyPS5 (GPL-2.0-or-later) implement any
    // sceLibcMspace* export -- checked their libC.cpp, which has plenty of
    // other libc functions (memcpy, strlen, cxa_atexit, ...) but nothing
    // mspace-shaped, so there is no reference implementation to copy from.
    //
    // Astro Bot 01.018 is (so far) the only title observed calling these
    // directly rather than bundling its own static allocator, so this is
    // built entirely from live register evidence captured across many
    // calls of each NID during its boot sequence, cross-referenced against
    // the well-documented open-source dlmalloc "mspace" API these almost
    // certainly wrap (create_mspace/mspace_malloc/mspace_free, multiple
    // independent heaps sharing one allocator implementation):
    //
    //   sceLibcMspaceMalloc (nid=OJjm-QOIHlI): rdi=mspace, rsi=size,
    //   rdx=align. rdx was *exactly* 0x10 on every single call seen
    //   (sizes varied: 0x10, 0x20, 0x28, 0x30, 0x48, 0x60) -- too
    //   consistent to be a leftover/dead register, and 0x10 is exactly
    //   __STDCPP_DEFAULT_NEW_ALIGNMENT__ on x86-64, so this reads as a
    //   3-arg (mspace, size, align) allocator, likely the back end for
    //   C++'s aligned `operator new`. rdi (mspace) was always 0 across
    //   every call seen; treated as "use the default/global heap" since
    //   there's no evidence Astro Bot ever creates a private mspace.
    //
    //   sceLibcMspaceFree (nid=Vla-Z+eXlxo): rdi=mspace, rsi=ptr. Only 2
    //   args show a consistent shape across calls (rdx/rcx varied
    //   incoherently call to call, consistent with leftover registers,
    //   not real arguments). One observed call's rsi was literally this
    //   process's own poison-pointer sentinel (0xFFFFFFFF80020002) --
    //   direct confirmation this is freeing the result of an earlier
    //   failed MspaceMalloc, i.e. the two NIDs are the alloc/free pair
    //   this comment assumes.
    //
    // SHARPEMU_DISABLE_LIBC_MSPACE=1 reverts to the pre-existing behavior
    // (both NIDs stay unresolved, callers see this process's generic
    // poison return value) in case this ABI guess turns out wrong on some
    // other title -- real malloc(3) can legitimately return NULL, so a
    // disabled MspaceMalloc returning 0 is still a well-formed response
    // even though it isn't literally "unresolved" anymore.
    private static readonly bool _mspaceDisabled = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_LIBC_MSPACE"),
        "1",
        StringComparison.Ordinal);

    [SysAbiExport(
        Nid = "OJjm-QOIHlI",
        ExportName = "sceLibcMspaceMalloc",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "LibcInternal")]
    public static int LibcMspaceMalloc(CpuContext ctx)
    {
        // rdi (mspace handle) intentionally unread: every call observed so
        // far passes 0 ("default heap"), and this implementation only ever
        // backs allocations with the one shared guest allocator arena --
        // see the ABI comment above EnsureHeapTraceStorage's declaration.
        var size = ctx[CpuRegister.Rsi];
        var align = ctx[CpuRegister.Rdx];

        if (_mspaceDisabled || size == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return 0;
        }

        if (align == 0 || (align & (align - 1)) != 0)
        {
            align = 0x10; // guest passed something malformed -- fall back to the observed common case
        }

        if (ctx.Memory is not IGuestMemoryAllocator allocator ||
            !allocator.TryAllocateGuestMemory(size, align, out var address))
        {
            ctx[CpuRegister.Rax] = 0; // real malloc(3) semantics: NULL on failure, not an error code
            return 0;
        }

        if (!TryZeroGuestMemory(ctx, address, size))
        {
            allocator.TryFreeGuestMemory(address);
            ctx[CpuRegister.Rax] = 0;
            return 0;
        }

        ctx[CpuRegister.Rax] = address;
        return 0;
    }

    // malloc(3) does not itself guarantee zeroed memory, but the arena this
    // wraps reuses freed ranges, so a fresh allocation can carry stale bytes
    // from whatever guest object previously lived there. Every other title
    // touching this codebase's poison-pointer recoveries has needed a
    // zeroed "the failed allocation should have produced something usable"
    // result (see TryRecoverPoisonPointerStore/CompareRead), so this
    // matches that same assumption rather than passing through raw arena
    // contents. Chunked through a fixed-size stack buffer instead of one
    // stackalloc sized by the guest-controlled `size` -- that would be an
    // uncontrolled stack allocation driven by untrusted guest input.
    private static bool TryZeroGuestMemory(CpuContext ctx, ulong address, ulong size)
    {
        Span<byte> zeros = stackalloc byte[256];
        zeros.Clear();

        var remaining = size;
        var offset = 0UL;
        while (remaining > 0)
        {
            var chunk = (int)Math.Min(remaining, (ulong)zeros.Length);
            if (!ctx.Memory.TryWrite(address + offset, zeros[..chunk]))
            {
                return false;
            }

            offset += (ulong)chunk;
            remaining -= (ulong)chunk;
        }

        return true;
    }

    [SysAbiExport(
        Nid = "Vla-Z+eXlxo",
        ExportName = "sceLibcMspaceFree",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "LibcInternal")]
    public static int LibcMspaceFree(CpuContext ctx)
    {
        var ptr = ctx[CpuRegister.Rsi];

        // free(NULL) is always a no-op by contract; a pointer this
        // recovery didn't itself hand out (e.g. one of
        // TryRecoverPoisonPointerStore's scratch redirects, or simply
        // unowned by this allocator) is silently ignored rather than
        // guessed at -- TryFreeGuestMemory already returns false for an
        // address it doesn't recognize, with no side effect either way.
        if (!_mspaceDisabled && ptr != 0 && ctx.Memory is IGuestMemoryAllocator allocator)
        {
            allocator.TryFreeGuestMemory(ptr);
        }

        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    // Found needing this immediately after strtok/bcmp/strcpy_s/sprintf_s
    // (LibcStdioExports.cs) let boot progress far enough to reach it.
    // Argument order is (mspace, alignment, size) rather than MspaceMalloc's
    // (mspace, size, alignment) -- taken directly from the well-documented,
    // open-source dlmalloc "mspace" API this whole family almost certainly
    // wraps 1:1 (mspace_memalign(mspace, alignment, bytes)), not guessed:
    // observed rsi=0x4000 (16 KiB, a plausible large/page-ish alignment)
    // paired with rdx=0x204000 (~2 MiB, a plausible large-buffer size) is
    // consistent with that order and not the reverse.
    [SysAbiExport(
        Nid = "iF1iQHzxBJU",
        ExportName = "sceLibcMspaceMemalign",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "LibcInternal")]
    public static int LibcMspaceMemalign(CpuContext ctx)
    {
        var align = ctx[CpuRegister.Rsi];
        var size = ctx[CpuRegister.Rdx];

        if (_mspaceDisabled || size == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return 0;
        }

        if (align == 0 || (align & (align - 1)) != 0)
        {
            align = 0x10;
        }

        if (ctx.Memory is not IGuestMemoryAllocator allocator ||
            !allocator.TryAllocateGuestMemory(size, align, out var address))
        {
            ctx[CpuRegister.Rax] = 0;
            return 0;
        }

        if (!TryZeroGuestMemory(ctx, address, size))
        {
            allocator.TryFreeGuestMemory(address);
            ctx[CpuRegister.Rax] = 0;
            return 0;
        }

        ctx[CpuRegister.Rax] = address;
        return 0;
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Concurrent;
using System.Threading;
using SharpEmu.HLE;

namespace SharpEmu.Libs.CxxAbi;

public static class CxaGuardExports
{
    private const ulong GuardCompleteValue = 0x0000_0000_0000_0001;
    private const ulong GuardPendingValue = 0x0000_0000_0000_0100;
    private const ulong GuardStateMask = 0x0000_0000_0000_FFFF;

    private sealed class GuardState
    {
        public int OwnerThreadId { get; set; }
        public int RecursionDepth { get; set; }
    }

    private static readonly ConcurrentDictionary<ulong, GuardState> _inProgress = new();

    [SysAbiExport(
        Nid = "3GPpjQdAMTw",
        ExportName = "__cxa_guard_acquire",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int CxaGuardAcquire(CpuContext ctx)
    {
        var guardPtr = ctx[CpuRegister.Rdi];
        if (guardPtr == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var currentThreadId = Environment.CurrentManagedThreadId;
        var spinner = new SpinWait();
        while (true)
        {
            if (!TryReadGuardState(ctx, guardPtr, out _, out var initialized, out var inProgress))
            {
                ctx[CpuRegister.Rax] = 0;
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            LogGuardState(ctx, "guard_acquire", guardPtr, initialized, inProgress);

            if (initialized)
            {
                ctx[CpuRegister.Rax] = 0;
                LogGuardResult("guard_acquire", guardPtr, result: 0, initialized, inProgress: false, ownerThreadId: 0);
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }

            var newState = new GuardState
            {
                OwnerThreadId = currentThreadId,
                RecursionDepth = 1,
            };
            if (_inProgress.TryAdd(guardPtr, newState))
            {
                if (!TryWriteGuardState(ctx, guardPtr, GuardPendingValue))
                {
                    _inProgress.TryRemove(guardPtr, out _);
                    ctx[CpuRegister.Rax] = 0;
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
                }

                ctx[CpuRegister.Rax] = 1;
                LogGuardResult("guard_acquire", guardPtr, result: 1, initialized, inProgress: true, ownerThreadId: currentThreadId);
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }

            if (_inProgress.TryGetValue(guardPtr, out var state))
            {
                if (state.OwnerThreadId == currentThreadId)
                {
                    ctx[CpuRegister.Rax] = 0;
                    LogGuardResult("guard_acquire", guardPtr, result: 0, initialized, inProgress: true, ownerThreadId: state.OwnerThreadId);
                    return (int)OrbisGen2Result.ORBIS_GEN2_OK;
                }
            }

            spinner.SpinOnce();
            if (spinner.Count % 32 == 0)
            {
                Thread.Yield();
            }
        }
    }

    [SysAbiExport(
        Nid = "9rAeANT2tyE",
        ExportName = "__cxa_guard_release",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int CxaGuardRelease(CpuContext ctx)
    {
        var guardPtr = ctx[CpuRegister.Rdi];
        if (guardPtr == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (_inProgress.TryGetValue(guardPtr, out var state) &&
            state.OwnerThreadId != Environment.CurrentManagedThreadId)
        {
            ctx[CpuRegister.Rax] = 0;
            LogGuardResult("guard_release", guardPtr, result: 0, initialized: false, inProgress: true, ownerThreadId: state.OwnerThreadId);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (state is not null)
        {
            lock (state)
            {
                if (state.RecursionDepth > 1)
                {
                    state.RecursionDepth--;
                    ctx[CpuRegister.Rax] = 0;
                    LogGuardResult("guard_release", guardPtr, result: 0, initialized: false, inProgress: true, ownerThreadId: state.OwnerThreadId);
                    return (int)OrbisGen2Result.ORBIS_GEN2_OK;
                }
            }
        }

        if (!TryWriteGuardState(ctx, guardPtr, GuardCompleteValue))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        _inProgress.TryRemove(guardPtr, out _);
        LogGuardState(ctx, "guard_release", guardPtr, initialized: true, inProgress: false);

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "2emaaluWzUw",
        ExportName = "__cxa_guard_abort",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int CxaGuardAbort(CpuContext ctx)
    {
        var guardPtr = ctx[CpuRegister.Rdi];
        if (guardPtr == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (_inProgress.TryGetValue(guardPtr, out var state) &&
            state.OwnerThreadId != Environment.CurrentManagedThreadId)
        {
            ctx[CpuRegister.Rax] = 0;
            LogGuardResult("guard_abort", guardPtr, result: 0, initialized: false, inProgress: true, ownerThreadId: state.OwnerThreadId);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        _ = TryWriteGuardState(ctx, guardPtr, 0);
        _inProgress.TryRemove(guardPtr, out _);
        LogGuardState(ctx, "guard_abort", guardPtr, initialized: false, inProgress: false);

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static bool TryReadGuardState(CpuContext ctx, ulong guardPtr, out ulong word, out bool initialized, out bool inProgress)
    {
        word = 0;
        initialized = false;
        inProgress = false;
        if (!ctx.TryReadUInt64(guardPtr, out word))
        {
            return false;
        }

        initialized = (word & GuardCompleteValue) != 0;
        inProgress = (word & 0x0000_0000_0000_FF00) != 0;
        return true;
    }

    private static bool TryWriteGuardState(CpuContext ctx, ulong guardPtr, ulong stateValue)
    {
        if (!ctx.TryReadUInt64(guardPtr, out var word))
        {
            return false;
        }

        var newWord = (word & ~GuardStateMask) | (stateValue & GuardStateMask);
        return ctx.TryWriteUInt64(guardPtr, newWord);
    }

    private static void LogGuardState(CpuContext ctx, string op, ulong guardPtr, bool initialized, bool inProgress)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_GUARDS"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var readable = ctx.TryReadUInt64(guardPtr, out var word);
        Console.Error.WriteLine(
            $"[LOADER][TRACE] {op}: guard=0x{guardPtr:X16} init={initialized} in_progress={inProgress} word={(readable ? $"0x{word:X16}" : "<unreadable>")}");
    }

    private static void LogGuardResult(string op, ulong guardPtr, int result, bool initialized, bool inProgress, int ownerThreadId)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_GUARDS"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] {op}: guard=0x{guardPtr:X16} result={result} init={initialized} in_progress={inProgress} owner_thread={ownerThreadId}");
    }
}

// Itanium C++ ABI's global allocation operators -- the compiler emits calls
// to these (by their mangled names, hence the NIDs below) for every plain
// `new`/`delete` expression that isn't placement-new or a class with its
// own operator new/delete overload. Unlike sceLibcMspaceMalloc/Free
// (astrobot-poison-store-fix, 2026-08-10, ABI genuinely undocumented and
// guessed from register evidence), these six mangled names and their
// calling convention are exactly specified by the public Itanium C++ ABI
// (https://itanium-cxx-abi.github.io/cxx-abi/abi.html#allocation) --
// nothing to guess here, this is a straight re-implementation. Found
// needing this on Astro Bot 01.018 immediately after the strtok fix above:
// _Znwm (plain `new`) was unresolved on literally every call observed
// (thousands during boot), each one hitting this process's poison-pointer
// recoveries the same way the mspace allocator's failures did -- likely
// the single most impactful of the three allocator-shaped gaps found this
// session, given how much more often a C++ title calls global `new` than
// either mspace or strtok.
public static class CxxNewDeleteExports
{
    [SysAbiExport(
        Nid = "fJnpuVVBbKk",
        ExportName = "_Znwm",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int OperatorNew(CpuContext ctx) => AllocateCore(ctx);

    [SysAbiExport(
        Nid = "hdm0YfMa7TQ",
        ExportName = "_Znam",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int OperatorNewArray(CpuContext ctx) => AllocateCore(ctx);

    [SysAbiExport(
        Nid = "z+P+xCnWLBk",
        ExportName = "_ZdlPv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int OperatorDelete(CpuContext ctx) => FreeCore(ctx);

    [SysAbiExport(
        Nid = "MLWl90SFWNE",
        ExportName = "_ZdaPv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int OperatorDeleteArray(CpuContext ctx) => FreeCore(ctx);

    // C++14 sized-delete overloads: same ABI-visible effect as the
    // unsized forms above (the compiler passes the size for allocators
    // that want it; the guest allocator this wraps tracks its own
    // allocation sizes internally and doesn't need it).
    [SysAbiExport(
        Nid = "lYDzBVE5mZs",
        ExportName = "_ZdlPvm",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int OperatorDeleteSized(CpuContext ctx) => FreeCore(ctx);

    [SysAbiExport(
        Nid = "FOt55ZNaVJk",
        ExportName = "_ZdaPvm",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int OperatorDeleteArraySized(CpuContext ctx) => FreeCore(ctx);

    private const ulong DefaultNewAlignment = 0x10; // __STDCPP_DEFAULT_NEW_ALIGNMENT__ on x86-64

    private static int AllocateCore(CpuContext ctx)
    {
        var size = ctx[CpuRegister.Rdi];
        if (size == 0)
        {
            size = 1; // operator new(0) must still return a valid, distinct, deletable pointer -- unlike malloc(0)
        }

        if (ctx.Memory is not IGuestMemoryAllocator allocator ||
            !allocator.TryAllocateGuestMemory(size, DefaultNewAlignment, out var address))
        {
            // Real operator new throws std::bad_alloc on failure rather
            // than returning null; HLE can't synthesize a guest C++
            // exception from here, so this falls back to the same "best
            // effort, don't crash" contract as the mspace allocator above
            // instead. Not standard-conformant, but strictly better than
            // the poison-pointer crash this NID being unresolved used to
            // produce on every single call.
            ctx[CpuRegister.Rax] = 0;
            return 0;
        }

        if (!TryZeroAllocatedMemory(ctx, address, size))
        {
            allocator.TryFreeGuestMemory(address);
            ctx[CpuRegister.Rax] = 0;
            return 0;
        }

        ctx[CpuRegister.Rax] = address;
        return 0;
    }

    private static int FreeCore(CpuContext ctx)
    {
        var ptr = ctx[CpuRegister.Rdi];
        if (ptr != 0 && ctx.Memory is IGuestMemoryAllocator allocator)
        {
            // delete on a pointer this allocator didn't itself hand out
            // (e.g. a TryRecoverPoisonPointerStore scratch redirect from
            // before this fix existed, or a pointer from some other
            // allocator entirely) is silently ignored -- TryFreeGuestMemory
            // already returns false for an address it doesn't recognize,
            // with no side effect either way, matching real free(3)/
            // operator delete's "undefined behavior" case as a safe no-op
            // rather than guessing.
            allocator.TryFreeGuestMemory(ptr);
        }

        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    // operator new does not itself guarantee zeroed memory, but the arena
    // this wraps reuses freed ranges, so a fresh allocation can carry
    // stale bytes from whatever guest object previously lived there.
    // Chunked through a fixed-size stack buffer instead of one stackalloc
    // sized by the guest-controlled `size` -- that would be an
    // uncontrolled stack allocation driven by untrusted guest input.
    private static bool TryZeroAllocatedMemory(CpuContext ctx, ulong address, ulong size)
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
}

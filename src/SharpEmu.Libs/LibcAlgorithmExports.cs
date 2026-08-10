// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.LibcAlgorithm;

// qsort(3) -- standard signature, no ABI guessing (unlike
// sceLibcMspaceMalloc/Free in LibcInternalExports.cs). The one genuinely
// new piece of ground this covers for this codebase's HLE layer: it has to
// call *back into guest code* (the comparator) to do its job at all, using
// IGuestThreadScheduler.TryCallGuestFunction the same way pthread_once's
// init routine, AvPlayer's event callbacks, etc. already do -- this is
// just the first "libc" (rather than "kernel object") HLE export in this
// codebase to need it.
//
// Found needing this on Astro Bot 01.018 immediately after the
// bcmp/strcpy_s/sprintf_s fixes in LibcStdioExports.cs: with those working
// correctly, the boot sequence progresses further/differently and reaches
// a qsort call that was previously unreachable; qsort being unresolved
// left whatever it should have populated still zeroed, and a NULL vtable
// dereference followed a few calls later.
public static class LibcAlgorithmExports
{
    // Sanity cap against a garbage/corrupted size_t driving an unbounded
    // host allocation -- real element sizes are always small (struct
    // sizes in the tens/hundreds of bytes).
    private const ulong MaxElementSize = 64 * 1024 * 1024;

    [SysAbiExport(
        Nid = "AEJdIVZTEmo",
        ExportName = "qsort",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Qsort(CpuContext ctx)
    {
        var baseAddress = ctx[CpuRegister.Rdi];
        var count = ctx[CpuRegister.Rsi];
        var elementSize = ctx[CpuRegister.Rdx];
        var comparator = ctx[CpuRegister.Rcx];

        // void return -- every early-out below is "leave the array as-is"
        // rather than an error path, matching qsort's own contract (it has
        // nothing to report on failure either).
        if (baseAddress == 0 || count < 2 || elementSize == 0 || elementSize > MaxElementSize || comparator == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var scheduler = GuestThreadExecution.Scheduler;
        if (scheduler is null)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        // Iterative heapsort: in-place, O(n log n), no recursion (so no
        // host stack-depth risk from a guest-controlled element count),
        // and only ever compares/swaps elements at their live guest
        // addresses -- the comparator callback gets real, current pointers
        // into the guest array exactly like a native qsort would give it,
        // not host-side copies.
        var scratchA = new byte[elementSize];
        var scratchB = new byte[elementSize];
        var ok = true;

        for (var i = count / 2; ok && i-- > 0;)
        {
            SiftDown(ctx, scheduler, baseAddress, elementSize, comparator, i, count, scratchA, scratchB, ref ok);
        }

        for (var end = count - 1; ok && end > 0; end--)
        {
            if (!TrySwapElements(ctx, baseAddress, elementSize, 0, end, scratchA, scratchB))
            {
                ok = false;
                break;
            }

            SiftDown(ctx, scheduler, baseAddress, elementSize, comparator, 0, end, scratchA, scratchB, ref ok);
        }

        // ok==false leaves the array partially sorted rather than crashing
        // -- a comparator call or a guest memory read/write failed
        // partway through, and there's no sane way to undo prior swaps.
        // Silent partial-sort beats a hard crash on the exact same
        // "should never happen" basis as this session's other recoveries.
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static void SiftDown(
        CpuContext ctx,
        IGuestThreadScheduler scheduler,
        ulong baseAddress,
        ulong elementSize,
        ulong comparator,
        ulong start,
        ulong end,
        byte[] scratchA,
        byte[] scratchB,
        ref bool ok)
    {
        var root = start;
        while (ok)
        {
            var child = (root * 2) + 1;
            if (child >= end)
            {
                return;
            }

            if (child + 1 < end)
            {
                if (!TryGuestCompare(
                        ctx, scheduler, comparator,
                        baseAddress + (child * elementSize),
                        baseAddress + ((child + 1) * elementSize),
                        out var siblingCmp))
                {
                    ok = false;
                    return;
                }

                if (siblingCmp < 0)
                {
                    child++;
                }
            }

            if (!TryGuestCompare(
                    ctx, scheduler, comparator,
                    baseAddress + (root * elementSize),
                    baseAddress + (child * elementSize),
                    out var rootCmp))
            {
                ok = false;
                return;
            }

            if (rootCmp >= 0)
            {
                return; // heap property already holds
            }

            if (!TrySwapElements(ctx, baseAddress, elementSize, root, child, scratchA, scratchB))
            {
                ok = false;
                return;
            }

            root = child;
        }
    }

    private static bool TryGuestCompare(
        CpuContext ctx,
        IGuestThreadScheduler scheduler,
        ulong comparator,
        ulong addressA,
        ulong addressB,
        out int result)
    {
        result = 0;
        if (!scheduler.TryCallGuestFunction(
                ctx, comparator, addressA, addressB, 0, 0, 0, "qsort_compar", out var raw, out _))
        {
            return false;
        }

        // The comparator returns a plain `int` in eax; the low 32 bits of
        // the 64-bit call result carry it, sign included.
        result = unchecked((int)(uint)raw);
        return true;
    }

    private static bool TrySwapElements(
        CpuContext ctx,
        ulong baseAddress,
        ulong elementSize,
        ulong indexA,
        ulong indexB,
        byte[] scratchA,
        byte[] scratchB)
    {
        if (indexA == indexB)
        {
            return true;
        }

        var addressA = baseAddress + (indexA * elementSize);
        var addressB = baseAddress + (indexB * elementSize);
        var spanA = scratchA.AsSpan(0, (int)elementSize);
        var spanB = scratchB.AsSpan(0, (int)elementSize);

        return ctx.Memory.TryRead(addressA, spanA) &&
               ctx.Memory.TryRead(addressB, spanB) &&
               ctx.Memory.TryWrite(addressA, spanB) &&
               ctx.Memory.TryWrite(addressB, spanA);
    }
}

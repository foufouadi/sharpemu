// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

// libKernel's address-wait primitives (sceKernelSyncOnAddress*) are the PS5's
// futex-style wait/wake: a thread parks on a guest address until another thread
// wakes that address. Guest runtimes (seen driving Juicy Realm, PPSA19268)
// build their own spinlocks/queues on top of it and call the wait in a hot
// loop; left unimplemented, every wait returns immediately and the runtime
// busy-spins forever (millions of calls, no forward progress).
//
// This implements wait/wake over the existing cooperative-block scheduler,
// keyed on the address. The wait honors the guest's own compare value
// (expected) and timeout: memory is checked against expected before ever
// blocking, and a guest-specified timeout is the real deadline, propagated
// back as a real ETIMEDOUT. The WaitSelfHealTimeout constant below only
// bounds the "wait forever" case (guest timeout=0) as a rare recovery net
// for a wake this emulator itself failed to deliver -- it must never be the
// thing that decides a normal wait's outcome. A matching wake releases
// waiters immediately through the same key.
public static class KernelSyncOnAddressCompatExports
{
    // Safety-net poll interval. Real releases come from the wake side (generation
    // bump + WakeBlockedThreads); this only bounds how long a wait that genuinely
    // raced/missed its wake stays parked before the guest re-evaluates. Kept
    // large: a short interval turns every parked waiter into a hot re-poll that
    // steals scheduler bandwidth from the threads that actually make progress
    // (including the ones that would issue the wake), so it must be a rare last
    // resort, not a spin substitute.
    private static readonly TimeSpan WaitSelfHealTimeout = TimeSpan.FromMilliseconds(100);

    // Per-address host gate for the non-cooperative (host main thread) fallback,
    // which cannot use the guest-thread scheduler's block mechanism.
    private static readonly ConcurrentDictionary<ulong, object> _hostAddressGates = new();

    // Per-address wake generation. A wait captures the current generation and
    // its wake predicate stays unsatisfied (keeps the thread parked) until a
    // wake bumps it. This is what actually holds the thread blocked: a bare
    // "always satisfied" predicate is treated as an immediate late-arrival by
    // the dispatcher's race guard and never yields, leaving the guest to
    // busy-spin. The generation also closes the register-vs-park race for free:
    // a wake landing in that window bumps the generation, so the predicate is
    // already satisfied and the guest correctly resumes at once.
    private static readonly ConcurrentDictionary<ulong, long> _wakeGenerations = new();

    private static long CurrentGeneration(ulong address) =>
        _wakeGenerations.TryGetValue(address, out var generation) ? generation : 0;

    private static string WakeKey(ulong address) => $"sceKernelSyncOnAddress:{address:X16}";

    [SysAbiExport(
        Nid = "Hc4CaR6JBL0",
        ExportName = "sceKernelSyncOnAddressWait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int SyncOnAddressWait(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        if (address == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // rsi = compare value, rdx = guest timeout in microseconds (0 = wait
        // forever). Confirmed empirically, not copied from any other
        // emulator: traced 36k+ real calls from Cult of the Lamb
        // (PPSA06464) and rsi/rdx/rcx sat at a rock-solid 0x0 the entire
        // run while r8 (not a real argument at this arity) churned through
        // unrelated pointer/scratch values -- the classic futex-style
        // 3-arg (addr, expected, timeout) shape, with this title's
        // spinlocks always comparing against 0 and never using a timeout.
        var expected = unchecked((uint)ctx[CpuRegister.Rsi]);
        var timeoutUsec = unchecked((uint)ctx[CpuRegister.Rdx]);

        // Futex fast path: only block while memory still holds the value the
        // guest compared against. If it already moved on, the wake this call
        // would have parked for has effectively already happened -- return
        // at once instead of blocking on a stale condition.
        if (!ctx.TryReadUInt32(address, out var currentValue))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (currentValue != expected)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
        }

        var observedGeneration = CurrentGeneration(address);

        // timeout=0 means "wait forever" -- only the self-heal safety net
        // bounds that case. A nonzero guest timeout is authoritative: it is
        // the real deadline and self-heal plays no part in it (it only wins
        // the MIN below if it happens to be the tighter bound, which still
        // resolves correctly since ResumeWait re-checks the guest deadline
        // specifically, not just "some deadline fired").
        var guestDeadline = timeoutUsec == 0
            ? 0L
            : GuestThreadExecution.ComputeDeadlineTimestamp(TimeSpan.FromMicroseconds(timeoutUsec));
        var selfHealDeadline = GuestThreadExecution.ComputeDeadlineTimestamp(WaitSelfHealTimeout);
        var deadline = guestDeadline != 0 && guestDeadline < selfHealDeadline
            ? guestDeadline
            : selfHealDeadline;

        // Pure read, no side effects -- safe to use both as the scheduler's
        // wake predicate and again in ResumeWait to tell a real wake apart
        // from a self-heal/guest-timeout expiry.
        bool WokeForReal() => CurrentGeneration(address) != observedGeneration;

        int ResumeWait()
        {
            if (WokeForReal())
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }

            // Only a guest-specified timeout that has actually elapsed is a
            // real timeout. An expiry caused solely by the self-heal safety
            // net (guest asked to wait forever) is a spurious wake: return
            // OK and let the guest's own re-check loop decide whether to
            // wait again, same as any futex-style caller must already
            // tolerate. This is what keeps the self-heal poll a rare
            // recovery path instead of a second, silently-wrong timeout.
            if (guestDeadline != 0 && Stopwatch.GetTimestamp() >= guestDeadline)
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT;
            }

            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        // Cooperative path: stay parked until a wake bumps this address's
        // generation, the guest's own timeout elapses, or (failing both) the
        // self-heal deadline expires. The guest re-evaluates its own
        // condition after resuming, as any futex-style caller must.
        if (GuestThreadExecution.RequestCurrentThreadBlock(
                ctx,
                "sceKernelSyncOnAddressWait",
                WakeKey(address),
                resumeHandler: ResumeWait,
                wakeHandler: WokeForReal,
                deadline))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
        }

        // Non-cooperative caller (host main thread): cannot use the
        // guest-thread scheduler's block mechanism, so wait directly on a
        // per-address gate. A guest timeout is honored as the real wait
        // bound and reported via ETIMEDOUT; an infinite guest wait
        // (timeout=0) falls back to the self-heal poll purely as a safety
        // net, same split as the cooperative path above.
        var gate = _hostAddressGates.GetOrAdd(address, static _ => new object());
        lock (gate)
        {
            if (CurrentGeneration(address) == observedGeneration)
            {
                var hostWaitTimeout = guestDeadline != 0
                    ? TimeSpan.FromMicroseconds(timeoutUsec)
                    : WaitSelfHealTimeout;
                Monitor.Wait(gate, hostWaitTimeout);

                if (guestDeadline != 0 &&
                    CurrentGeneration(address) == observedGeneration &&
                    Stopwatch.GetTimestamp() >= guestDeadline)
                {
                    return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT);
                }
            }
        }

        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "q2y-wDIVWZA",
        ExportName = "sceKernelSyncOnAddressWake",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int SyncOnAddressWake(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        if (address == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // rsi carries the number of waiters to release (1 = wake-one, a large
        // value = wake-all); default to all if it looks unset.
        var requested = unchecked((long)ctx[CpuRegister.Rsi]);
        var wakeCount = requested is > 0 and < int.MaxValue ? (int)requested : int.MaxValue;

        // Bump the generation first so a wait that has registered but not yet
        // parked sees the change and resumes instead of missing this wake.
        _wakeGenerations.AddOrUpdate(address, 1, static (_, current) => current + 1);

        GuestThreadExecution.Scheduler?.WakeBlockedThreads(WakeKey(address), wakeCount);

        if (_hostAddressGates.TryGetValue(address, out var gate))
        {
            lock (gate)
            {
                Monitor.PulseAll(gate);
            }
        }

        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static int SetReturn(CpuContext ctx, OrbisGen2Result result)
    {
        var value = (int)result;
        ctx[CpuRegister.Rax] = unchecked((ulong)value);
        return value;
    }
}

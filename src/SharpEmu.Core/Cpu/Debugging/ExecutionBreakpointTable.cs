// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE;

namespace SharpEmu.Core.Cpu.Debugging;

/// <summary>
/// The patched trap bytes behind execution breakpoints. Guest code runs
/// natively at its own virtual addresses, so a breakpoint replaces the first
/// byte of an instruction with int3 and remembers what it displaced.
/// </summary>
/// <remarks>
/// Separate from the backend so the patching contract can be exercised on
/// ordinary memory: what makes a breakpoint work is that the byte is swapped
/// and restored exactly, and that is decidable without a CPU.
/// </remarks>
public sealed class ExecutionBreakpointTable
{
    public const byte TrapByte = 0xCC;

    // Writable page protections. A guest code page is mapped executable and
    // writable, so the trap can be planted without changing protection.
    private const uint PageReadWrite = 0x04;
    private const uint PageWriteCopy = 0x08;
    private const uint PageExecuteReadWrite = 0x40;
    private const uint PageExecuteWriteCopy = 0x80;

    private readonly ConcurrentDictionary<ulong, byte> _armed = new();
    private readonly object _gate = new();

    public IReadOnlyCollection<ulong> Armed => _armed.Keys.ToArray();

    public bool IsEmpty => _armed.IsEmpty;

    public unsafe bool TryArm(ulong address, out string error)
    {
        error = string.Empty;
        if (address == 0)
        {
            error = "address is zero";
            return false;
        }

        lock (_gate)
        {
            if (_armed.ContainsKey(address))
            {
                return true;
            }

            // The page has to be checked, not tried. Writing to an unmapped
            // guest address raises AccessViolationException, which .NET treats
            // as a corrupted-state exception and does not deliver to an
            // ordinary catch: the process simply dies. Arming a breakpoint at
            // attach time, before the module carrying it is mapped, is exactly
            // that case.
            if (!IsWritable(address, out error))
            {
                return false;
            }

            var slot = (byte*)address;
            var original = *slot;
            if (original == TrapByte)
            {
                // A trap that is not ours. Recording 0xCC as the byte to
                // restore would leave it in place forever.
                error = $"0x{address:X16} already holds a trap byte";
                return false;
            }

            *slot = TrapByte;
            _armed[address] = original;
            return true;
        }
    }

    public unsafe bool TryDisarm(ulong address)
    {
        lock (_gate)
        {
            if (!_armed.TryRemove(address, out var original))
            {
                return false;
            }

            // The module may have been unmapped since. The record is gone
            // either way, which is what disarming has to guarantee.
            if (IsWritable(address, out _))
            {
                *(byte*)address = original;
            }

            return true;
        }
    }

    /// <summary>
    /// Removes the trap so the instruction can execute, keeping the record so
    /// <see cref="TryRearm"/> can put it back. False when the address is not
    /// armed, including when another thread disarmed it concurrently.
    /// </summary>
    public unsafe bool TryLiftForStep(ulong address)
    {
        lock (_gate)
        {
            if (!_armed.TryGetValue(address, out var original) ||
                !IsWritable(address, out _))
            {
                return false;
            }

            *(byte*)address = original;
            return true;
        }
    }

    /// <summary>Restores the trap after a step, unless it was disarmed meanwhile.</summary>
    public unsafe void TryRearm(ulong address)
    {
        lock (_gate)
        {
            if (_armed.ContainsKey(address) && IsWritable(address, out _))
            {
                *(byte*)address = TrapByte;
            }
        }
    }

    private static unsafe bool IsWritable(ulong address, out string error)
    {
        error = string.Empty;
        if (HostMemory.Query((void*)address, out var info) == 0)
        {
            error = $"0x{address:X16} is not mapped";
            return false;
        }

        if (info.State != HostMemory.MEM_COMMIT)
        {
            error = $"0x{address:X16} is reserved but not committed";
            return false;
        }

        if (info.Protect is not (PageReadWrite or PageWriteCopy or
            PageExecuteReadWrite or PageExecuteWriteCopy))
        {
            error = $"0x{address:X16} is not writable (protect=0x{info.Protect:X})";
            return false;
        }

        return true;
    }
}

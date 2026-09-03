// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;

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

            byte original;
            try
            {
                var slot = (byte*)address;
                original = *slot;
                if (original == TrapByte)
                {
                    // A trap that is not ours. Recording 0xCC as the byte to
                    // restore would leave it in place forever.
                    error = $"0x{address:X16} already holds a trap byte";
                    return false;
                }

                *slot = TrapByte;
            }
            catch (Exception exception)
            {
                error = $"0x{address:X16} is not writable: {exception.Message}";
                return false;
            }

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

            try
            {
                *(byte*)address = original;
            }
            catch
            {
                // The page went away with its module. The record is gone either
                // way, which is what disarming has to guarantee.
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
            if (!_armed.TryGetValue(address, out var original))
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
            if (_armed.ContainsKey(address))
            {
                *(byte*)address = TrapByte;
            }
        }
    }
}

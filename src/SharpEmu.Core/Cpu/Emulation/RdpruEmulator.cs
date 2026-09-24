// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Core.Cpu.Emulation;

/// <summary>
/// RDPRU (0F 01 FD) reads a processor register selected by ECX into EDX:EAX: 0 is MPERF, 1 is
/// APERF. A host without it raises #UD. Both counters are stood in for by one monotonic host
/// counter, so their ratio (the effective-frequency measure they exist for) is 1.
/// </summary>
public static class RdpruEmulator
{
    public const int Length = 3;

    private const uint CarryFlag = 1u << 0;
    private const uint ArithmeticFlags = CarryFlag | (1u << 2) | (1u << 4) | (1u << 6) | (1u << 7) | (1u << 11);

    public static bool IsRdpru(ReadOnlySpan<byte> code) =>
        code.Length >= Length && code[0] == 0x0F && code[1] == 0x01 && code[2] == 0xFD;

    // A known selector sets CF and returns the counter; any other returns zero with CF clear.
    public static (uint Eax, uint Edx, uint Eflags) Execute(uint selector, ulong counter, uint eflags)
    {
        eflags &= ~ArithmeticFlags;
        if (selector > 1)
        {
            return (0, 0, eflags);
        }

        return ((uint)counter, (uint)(counter >> 32), eflags | CarryFlag);
    }
}

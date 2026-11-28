// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.Intrinsics.X86;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace SharpEmu.Core.Loader;

// Cache-line instructions the PS5's Zen 2 has and some hosts do not, which fault with #UD there.
// CLZERO (AMD only) clears the 64-byte line holding rAX; the trampoline stores the zeros instead.
// CLWB only writes a line back and leaves memory as it is, so it becomes a NOP.
internal static class CacheLineRewrite
{
    private const int CacheLineBytes = 64;

    private static readonly bool Disabled = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_CACHE_LINE_REWRITE"), "1", StringComparison.Ordinal);

    private static readonly bool RewriteClzero = !Disabled && HostLacksClzero();
    private static readonly bool RewriteClwb = !Disabled && HostLacksClwb();

    internal static bool IsRequired { get; } = RewriteClzero || RewriteClwb;

    // Bytes the expansion of one instruction needs at most in the trampoline.
    internal const int MaximumExpansionBytes = 96;

    private static bool HostLacksClzero()
    {
        if (!X86Base.IsSupported)
        {
            return false;
        }

        var (maxExtended, _, _, _) = X86Base.CpuId(unchecked((int)0x8000_0000), 0);
        if ((uint)maxExtended < 0x8000_0008)
        {
            return true;
        }

        var (_, ebx, _, _) = X86Base.CpuId(unchecked((int)0x8000_0008), 0);
        return (ebx & 1) == 0;
    }

    private static bool HostLacksClwb()
    {
        if (!X86Base.IsSupported)
        {
            return false;
        }

        var (maxLeaf, _, _, _) = X86Base.CpuId(0, 0);
        if (maxLeaf < 7)
        {
            return true;
        }

        var (_, ebx, _, _) = X86Base.CpuId(7, 0);
        return (ebx & (1 << 24)) == 0;
    }

    internal static bool CanRewrite(in Instruction instruction) =>
        (RewriteClzero && instruction.Code is Code.Clzeroq or Code.Clzerod) ||
        (RewriteClwb && instruction.Code == Code.Clwb_m8);

    internal static IList<Instruction> Expand(IList<Instruction> instructions)
    {
        var expanded = new List<Instruction>(instructions.Count);
        foreach (var instruction in instructions)
        {
            if (!CanRewrite(instruction))
            {
                expanded.Add(instruction);
                continue;
            }

            var assembler = new Assembler(64);
            EmitExpansion(assembler, instruction);
            var first = true;
            ulong generatedIp = 0xFFFC_0000_0000_0000UL + ((instruction.IP & 0xFFFF_FFFF) << 12);
            foreach (var generated in assembler.Instructions)
            {
                var copy = generated;
                // Keep branch targets at the first generated instruction.
                copy.IP = first ? instruction.IP : generatedIp++;
                first = false;
                expanded.Add(copy);
            }
        }

        return expanded;
    }

    internal static void EmitExpansion(Assembler a, in Instruction instruction)
    {
        if (instruction.Code == Code.Clwb_m8)
        {
            a.nop();
            return;
        }

        // CLZERO leaves the flags and rAX alone; the line is rAX rounded down to 64 bytes
        // (EAX zero-extended with a 32-bit address size).
        a.pushfq();
        a.push(rax);
        if (instruction.Code == Code.Clzerod)
        {
            a.mov(eax, eax);
        }

        a.and(rax, -CacheLineBytes);
        for (var offset = 0; offset < CacheLineBytes; offset += sizeof(ulong))
        {
            a.mov(__qword_ptr[rax + offset], 0);
        }

        a.pop(rax);
        a.popfq();
    }
}

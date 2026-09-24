// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.Intrinsics.X86;
using Iced.Intel;

namespace SharpEmu.Core.Cpu.Emulation;

/// <summary>
/// Instruction-set extensions of the guest CPU that a host can lack and whose instructions
/// the native backend recovers in software after the host raises #UD.
/// </summary>
[Flags]
public enum RecoverableExtensions
{
    None = 0,
    Sse4a = 1 << 0,
    MonitorX = 1 << 1,
    Bmi1 = 1 << 2,
    Bmi2 = 1 << 3,
    Lzcnt = 1 << 4,
    ClZero = 1 << 5,
    Rdpru = 1 << 6,
}

public static class HostExtensionSupport
{
    /// <summary>The recoverable extensions this host does not implement.</summary>
    public static RecoverableExtensions Missing { get; } = DetectMissing();

    /// <summary>The recoverable extensions an instruction needs.</summary>
    public static RecoverableExtensions Required(in Instruction instruction)
    {
        var required = RecoverableExtensions.None;
        foreach (var feature in instruction.Code.CpuidFeatures())
        {
            required |= feature switch
            {
                CpuidFeature.SSE4A => RecoverableExtensions.Sse4a,
                CpuidFeature.MONITORX => RecoverableExtensions.MonitorX,
                CpuidFeature.BMI1 => RecoverableExtensions.Bmi1,
                CpuidFeature.BMI2 => RecoverableExtensions.Bmi2,
                CpuidFeature.LZCNT => RecoverableExtensions.Lzcnt,
                CpuidFeature.CLZERO => RecoverableExtensions.ClZero,
                CpuidFeature.RDPRU => RecoverableExtensions.Rdpru,
                _ => RecoverableExtensions.None,
            };
        }

        return required;
    }

    /// <summary>
    /// True when the host raises #UD on the instruction, so it reaches the recovery path through
    /// a host exception like any faulting memory access.
    /// </summary>
    public static bool TrapsOnHost(in Instruction instruction, RecoverableExtensions missing) =>
        missing != RecoverableExtensions.None && (Required(instruction) & missing) != 0;

    private static RecoverableExtensions DetectMissing()
    {
        if (!X86Base.IsSupported)
        {
            return RecoverableExtensions.None;
        }

        var missing = RecoverableExtensions.None;
        if (!Bmi1.IsSupported)
        {
            missing |= RecoverableExtensions.Bmi1;
        }

        if (!Bmi2.IsSupported)
        {
            missing |= RecoverableExtensions.Bmi2;
        }

        if (!Lzcnt.IsSupported)
        {
            missing |= RecoverableExtensions.Lzcnt;
        }

        var maximumExtendedLeaf = (uint)X86Base.CpuId(unchecked((int)0x8000_0000), 0).Eax;
        var extendedFeatures = maximumExtendedLeaf >= 0x8000_0001 ? (uint)X86Base.CpuId(unchecked((int)0x8000_0001), 0).Ecx : 0;
        var extendedIdentifiers = maximumExtendedLeaf >= 0x8000_0008 ? (uint)X86Base.CpuId(unchecked((int)0x8000_0008), 0).Ebx : 0;
        if ((extendedFeatures & (1u << 6)) == 0)
        {
            missing |= RecoverableExtensions.Sse4a;
        }

        if ((extendedFeatures & (1u << 29)) == 0)
        {
            missing |= RecoverableExtensions.MonitorX;
        }

        if ((extendedIdentifiers & (1u << 0)) == 0)
        {
            missing |= RecoverableExtensions.ClZero;
        }

        if ((extendedIdentifiers & (1u << 4)) == 0)
        {
            missing |= RecoverableExtensions.Rdpru;
        }

        return missing;
    }
}

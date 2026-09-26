// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using System.Linq;

namespace SharpEmu.ShaderCompiler.Resources;

// One address handle's planned range: its base value and the signed byte span its
// accesses reach. A handle with a run-time offset is unbounded up to the cap.
public sealed record DeviceAddressRangePlan(
    uint Handle,
    ScalarValue BaseLow,
    ScalarValue BaseHigh,
    IReadOnlyList<int> MemoryIndices,
    bool Written,
    bool Bounded,
    long FirstByte,
    long EndByte,
    bool Plannable)
{
    public ulong Extent => Bounded && EndByte > FirstByte ? (ulong)(EndByte - FirstByte) : 0;
}

// Bounds every device-address handle after tracking. The pass is optional: an
// unplannable handle never fails the plan, it is reported for the hosts to decide.
public static class DeviceAddressRangePlanner
{
    public const ulong MaxRangeBytes = 16 * 1024 * 1024;
    private const ulong AddressMask = 0x0000_FFFF_FFFF_FFFFul;

    public static IReadOnlyList<DeviceAddressRangePlan> Plan(ShaderResourcePlan plan)
    {
        var ranges = new List<DeviceAddressRangePlan>();
        var handles = new List<ScalarValue>();
        for (var index = 0; index < plan.Memory.Count; index++)
        {
            var memory = plan.Memory[index];
            if (memory.Kind is not (MemoryResourceKind.Flat or MemoryResourceKind.Global or MemoryResourceKind.ScalarAddress) || memory.PlanningOnly)
            {
                continue;
            }

            if (plan.Accesses[index]?.Handle is not { Kind: ScalarValueKind.AddressHandle } handle || handle.Operands.Length != 2)
            {
                continue;
            }

            // A FLAT address built from an aperture base is an LDS or scratch
            // pointer; the backend routes it per lane, so it owns no device range.
            if (memory.Kind == MemoryResourceKind.Flat)
            {
                memory.AddressSpace = ClassifyFlatAddress(handle.Operands[1]);
                if (memory.AddressSpace != FlatAddressSpace.Global)
                {
                    continue;
                }
            }

            var slot = handles.FindIndex(existing => plan.Graph.Equivalent(existing, handle));
            if (slot < 0)
            {
                slot = handles.Count;
                handles.Add(handle);
                ranges.Add(new DeviceAddressRangePlan((uint)slot, handle.Operands[0], handle.Operands[1], [], false, true, long.MaxValue, long.MinValue, false));
            }

            // The immediate offset is signed; the span keeps the lowest and highest byte.
            var written = memory.Access is MemoryAccess.Write or MemoryAccess.Atomic;
            var bytes = Math.Max((memory.DataBits + 7) / 8, 1u) * Math.Max(memory.DataDwords, 1u);
            var firstByte = (long)unchecked((int)memory.Offset);
            ranges[slot] = ranges[slot] with
            {
                MemoryIndices = [.. ranges[slot].MemoryIndices, index],
                Written = ranges[slot].Written || written,
                Bounded = ranges[slot].Bounded && IsBoundedOffset(plan, index),
                FirstByte = Math.Min(ranges[slot].FirstByte, firstByte),
                EndByte = Math.Max(ranges[slot].EndByte, firstByte + bytes),
            };
        }

        for (var slot = 0; slot < ranges.Count; slot++)
        {
            ranges[slot] = ranges[slot] with
            {
                Plannable = plan.ValidateRuntimeValue(ranges[slot].BaseLow) && plan.ValidateRuntimeValue(ranges[slot].BaseHigh),
            };
        }

        return ranges;
    }

    // The hardware compares only bits 63:48 of a FLAT address with the apertures,
    // and shaders commonly merge an aperture into the upper half of an otherwise
    // computed high dword (SDWA WORD1). Any aperture reaching the high dword
    // therefore marks the address as local.
    private static FlatAddressSpace ClassifyFlatAddress(ScalarValue high)
    {
        var shared = false;
        var @private = false;
        var pending = new Stack<ScalarValue>();
        var visited = new HashSet<ScalarValue>();
        pending.Push(high);
        while (pending.TryPop(out var value))
        {
            if (!visited.Add(value))
            {
                continue;
            }

            if (value.Kind == ScalarValueKind.MemoryAperture)
            {
                if (Gen5InlineConstants.IsSharedAperture((uint)value.Payload))
                    shared = true;
                else
                    @private = true;
                continue;
            }

            foreach (var operand in value.Operands)
                pending.Push(operand);
        }

        return (shared, @private) switch
        {
            (true, true) => FlatAddressSpace.SharedOrPrivate,
            (true, false) => FlatAddressSpace.Shared,
            (false, true) => FlatAddressSpace.Private,
            _ => FlatAddressSpace.Global,
        };
    }

    // An access is bounded when its only run-time term is the record's immediate offset:
    // a scalar read, or a global access whose vector offset is a constant zero.
    private static bool IsBoundedOffset(ShaderResourcePlan plan, int memoryIndex)
    {
        var memory = plan.Memory[memoryIndex];
        if (memory.Kind == MemoryResourceKind.ScalarAddress)
        {
            var read = plan.Accesses[memoryIndex]?.Read;
            return read is not null && read.Operands[1].IsConstant;
        }

        var offset = plan.Accesses[memoryIndex]?.Offset;
        return offset is { IsConstant: true } && offset.ConstantU32 == 0;
    }

    // Evaluates the planned ranges for one draw. A bounded range spans its lowest byte to
    // its extent; an unbounded one starts at the lowest immediate and takes the cap.
    public static DeviceAddressRange[] Evaluate(ShaderResourcePlan plan, ResourceRuntimeInputs inputs)
    {
        var ranges = plan.DeviceAddressRanges;
        var result = new DeviceAddressRange[ranges.Count];
        using var scratch = RuntimeEvaluationScratch.Rent();
        var evaluator = new RuntimeValueEvaluator(scratch, plan, inputs);
        for (var index = 0; index < ranges.Count; index++)
        {
            var range = ranges[index];
            uint low = 0;
            uint high = 0;
            var planned = range.Plannable && evaluator.Evaluate(range.BaseLow, out low) && evaluator.Evaluate(range.BaseHigh, out high);
            var handleBase = (low | ((ulong)high << 32)) & AddressMask;
            var firstByte = range.Bounded ? range.FirstByte : Math.Min(range.FirstByte, 0);
            var baseAddress = planned ? unchecked(handleBase + (ulong)firstByte) & AddressMask : 0;
            var size = range.Bounded ? range.Extent : MaxRangeBytes;
            result[index] = new DeviceAddressRange(range.Handle, baseAddress, planned ? size : 0, planned, range.Written);
        }

        return result;
    }
}

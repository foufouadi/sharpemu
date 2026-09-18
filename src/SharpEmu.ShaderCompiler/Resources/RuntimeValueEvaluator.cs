// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;

namespace SharpEmu.ShaderCompiler.Resources;

// Evaluates graph values against one draw's inputs. Results are memoised so values
// shared by several descriptors and flattened reads are computed once.
public sealed class RuntimeValueEvaluator
{
    private const ulong AddressMask = 0x0000_FFFF_FFFF_FFFFul;

    // A bindless pointer the guest leaves unbound for a dispatch resolves its address-handle
    // dwords to zero; a small immediate offset on top of that still lands under this guest
    // null-page threshold. Treat such reads as zero instead of faulting the host, so the
    // descriptor built from them collapses to a null resource. Ported from KytyPS5's identical
    // fix ("shader: treat a null-based SRT constant read as zero").
    private const ulong GuestNullPageSize = 0x10000ul;

    private readonly ShaderResourcePlan _plan;
    private readonly ResourceRuntimeInputs _inputs;
    private readonly IReadOnlyList<byte> _cleanFlatSlots;
    private readonly RuntimeValueEvaluator? _cleanEvaluator;
    private readonly ScalarValue? _activeMask;
    private readonly Dictionary<ScalarValue, ulong> _cache;
    private readonly List<ScalarValue> _visiting;

    public RuntimeValueEvaluator(
        ShaderResourcePlan plan,
        ResourceRuntimeInputs inputs,
        IReadOnlyList<byte>? cleanFlatSlots = null,
        RuntimeValueEvaluator? cleanEvaluator = null,
        ScalarValue? activeMask = null)
        : this(plan, inputs, cleanFlatSlots, cleanEvaluator, activeMask, [], [])
    {
    }

    internal RuntimeValueEvaluator(
        RuntimeEvaluationScratch scratch,
        ShaderResourcePlan plan,
        ResourceRuntimeInputs inputs,
        IReadOnlyList<byte>? cleanFlatSlots = null,
        RuntimeValueEvaluator? cleanEvaluator = null,
        ScalarValue? activeMask = null)
        : this(plan, inputs, cleanFlatSlots, cleanEvaluator, activeMask, scratch.Values, scratch.Visiting)
    {
    }

    private RuntimeValueEvaluator(
        ShaderResourcePlan plan,
        ResourceRuntimeInputs inputs,
        IReadOnlyList<byte>? cleanFlatSlots,
        RuntimeValueEvaluator? cleanEvaluator,
        ScalarValue? activeMask,
        Dictionary<ScalarValue, ulong> cache,
        List<ScalarValue> visiting)
    {
        _cache = cache;
        _visiting = visiting;
        _plan = plan;
        _inputs = inputs;
        _cleanFlatSlots = cleanFlatSlots ?? [];
        _cleanEvaluator = cleanEvaluator;
        _activeMask = activeMask;
    }

    public bool Evaluate(ScalarValue value, out uint result)
    {
        if (!EvaluateWide(value, out var wide))
        {
            result = 0;
            return false;
        }

        result = (uint)wide;
        return true;
    }

    public bool EvaluateWide(ScalarValue value, out ulong result)
    {
        result = 0;
        if (value.IsConstant)
        {
            result = value.Payload;
            return true;
        }

        if (_activeMask is not null && value.Kind == ScalarValueKind.Select && ReferenceEquals(value.Operands[0], _activeMask))
        {
            return EvaluateWide(value.Operands[1], out result);
        }

        if (_cache.TryGetValue(value, out result))
        {
            return true;
        }

        if (_visiting.Contains(value))
        {
            return false;
        }

        _visiting.Add(value);
        var evaluated = EvaluateNode(value, out var computed);
        _visiting.RemoveAt(_visiting.Count - 1);
        if (!evaluated)
        {
            return false;
        }

        _cache[value] = computed;
        result = computed;
        return true;
    }

    private bool Operand(ScalarValue value, int index, out ulong result) => EvaluateWide(value.Operands[index], out result);

    private bool EvaluateNode(ScalarValue value, out ulong result)
    {
        result = 0;
        switch (value.Kind)
        {
            case ScalarValueKind.Undefined:
                return false;
            case ScalarValueKind.UserData:
            {
                var register = value.UserDataRegister;
                if (register < _plan.UserDataBase || register - _plan.UserDataBase >= (uint)_inputs.UserData.Count)
                {
                    return false;
                }

                result = _inputs.UserData[(int)(register - _plan.UserDataBase)];
                return true;
            }
            case ScalarValueKind.ShaderBase:
                result = _inputs.ShaderBase;
                return true;
            case ScalarValueKind.Phi:
            {
                var invariant = _plan.Graph.ResolveInvariantPhi(value);
                return invariant is not null && EvaluateWide(invariant, out result);
            }
            case ScalarValueKind.FirstLane:
            {
                using var scratch = RuntimeEvaluationScratch.Rent();
                return new RuntimeValueEvaluator(scratch, _plan, _inputs, _cleanFlatSlots, _cleanEvaluator, value.Operands[1])
                    .EvaluateWide(value.Operands[0], out result);
            }
            case ScalarValueKind.ResourceTableWord:
            {
                var slot = (int)value.Payload;
                if (slot >= _plan.TableReads.Count)
                {
                    return false;
                }

                if (slot < _cleanFlatSlots.Count && _cleanFlatSlots[slot] != 0 && _cleanEvaluator is not null)
                {
                    return _cleanEvaluator.EvaluateWide(_plan.TableReads[slot].Value, out result);
                }

                return EvaluateWide(_plan.TableReads[slot].Value, out result);
            }
            case ScalarValueKind.ScalarAddressWord:
            case ScalarValueKind.ScalarBufferWord:
                return EvaluateRawRead(value, out result);
            case ScalarValueKind.Select:
            {
                if (!Operand(value, 0, out var condition) || !Operand(value, 1, out var whenTrue) || !Operand(value, 2, out var whenFalse))
                {
                    return false;
                }

                result = condition != 0 ? whenTrue : whenFalse;
                return true;
            }
            case ScalarValueKind.Operation:
            {
                if (!RuntimeValueValidator.IsUniformOperation(value.Operation))
                {
                    return false;
                }

                Span<ulong> operands = stackalloc ulong[value.Operands.Length];
                for (var index = 0; index < operands.Length; index++)
                {
                    if (!Operand(value, index, out operands[index]))
                    {
                        return false;
                    }
                }

                return ScalarOperationSemantics.TryEvaluate(value.Operation, operands, out result);
            }
            default:
                return false;
        }
    }

    // A raw read adds the immediate and dynamic offsets to the 48-bit handle base, checks
    // a buffer read against its records, and reads one aligned dword.
    private bool EvaluateRawRead(ScalarValue value, out ulong result)
    {
        result = 0;
        if (value.MemoryIndex >= _plan.Memory.Count)
        {
            return false;
        }

        var memory = _plan.Memory[value.MemoryIndex];
        var handle = value.Operands[0];
        if (handle.Operands.Length < 2 ||
            !EvaluateWide(handle.Operands[0], out var low) ||
            !EvaluateWide(handle.Operands[1], out var high) ||
            !Operand(value, 1, out var offset))
        {
            if (Environment.GetEnvironmentVariable("SHARPEMU_RESOURCE_TRACKER_DIAG") == "1")
            {
                Console.Error.WriteLine($"[RV-EVAL-DIAG] EvaluateRawRead failed handle.Kind={handle.Kind} handle.Operands={handle.Operands.Length} " +
                    $"op0.Kind={(handle.Operands.Length > 0 ? handle.Operands[0].Kind.ToString() : "n/a")} " +
                    $"op1.Kind={(handle.Operands.Length > 1 ? handle.Operands[1].Kind.ToString() : "n/a")} " +
                    $"offsetOperand.Kind={(value.Operands.Length > 1 ? value.Operands[1].Kind.ToString() : "n/a")}");
            }

            return false;
        }

        var baseAddress = ((high << 32) | (uint)low) & AddressMask;
        var immediate = (long)(int)memory.Offset;
        ulong address;
        if (value.Kind == ScalarValueKind.ScalarBufferWord)
        {
            if (handle.Operands.Length != 4 ||
                !EvaluateWide(handle.Operands[2], out var records) ||
                !EvaluateWide(handle.Operands[3], out _))
            {
                return false;
            }

            if (immediate < 0)
            {
                return false;
            }

            var byteOffset = (ulong)immediate + (uint)offset;
            var aligned = byteOffset & ~3ul;
            var stride = ((uint)high >> 16) & 0x3FFFu;
            var size = stride == 0 ? (ulong)(uint)records : (ulong)stride * (uint)records;
            if (aligned > size || size - aligned < sizeof(uint))
            {
                return false;
            }

            address = ((baseAddress & ~3ul) + byteOffset) & ~3ul;
        }
        else
        {
            var relative = (immediate & ~3L) + (long)((uint)offset & ~3u);
            if (!AddSignedAddress(baseAddress & ~3ul, relative, out address))
            {
                return false;
            }

            if (address < GuestNullPageSize)
            {
                result = 0;
                return true;
            }
        }

        if (_inputs.ReadMemory is null || !_inputs.ReadMemory(address, out var word))
        {
            if (Environment.GetEnvironmentVariable("SHARPEMU_RESOURCE_TRACKER_DIAG") == "1")
            {
                Console.Error.WriteLine($"[RV-EVAL-DIAG] ReadMemory failed address=0x{address:X} readerNull={_inputs.ReadMemory is null} value.Kind={value.Kind}");
            }

            return false;
        }

        result = word;
        return true;
    }

    private static bool AddSignedAddress(ulong baseAddress, long offset, out ulong result)
    {
        result = 0;
        if (baseAddress > AddressMask)
        {
            return false;
        }

        if (offset < 0)
        {
            var magnitude = (ulong)(-offset);
            if (magnitude > baseAddress)
            {
                return false;
            }

            result = baseAddress - magnitude;
            return true;
        }

        var forward = (ulong)offset;
        if (forward > AddressMask - baseAddress)
        {
            return false;
        }

        result = baseAddress + forward;
        return true;
    }

    // Evaluates descriptor sources and, when asked, the flattened table in one memoised
    // walk. On failure neither output changes.
    public static bool EvaluateSources(
        ShaderResourcePlan plan,
        IReadOnlyList<uint> sources,
        ResourceRuntimeInputs inputs,
        IReadOnlyList<byte> cleanFlatSlots,
        bool evaluateTable,
        out List<DescriptorWords> results,
        out uint[] table)
        => EvaluateSources(plan, sources, inputs, cleanFlatSlots, evaluateTable, out results, out table, out _);

    internal static bool EvaluateSources(
        ShaderResourcePlan plan,
        IReadOnlyList<uint> sources,
        ResourceRuntimeInputs inputs,
        IReadOnlyList<byte> cleanFlatSlots,
        bool evaluateTable,
        out List<DescriptorWords> results,
        out uint[] table,
        out bool[] activeSources,
        int additionalTableWords = 0)
    {
        results = [];
        table = [];
        activeSources = [];
        var anyClean = false;
        foreach (var clean in cleanFlatSlots)
        {
            anyClean |= clean != 0;
        }

        if (anyClean && inputs.ReadCleanMemory is null)
        {
            return false;
        }

        using var cleanScratch = RuntimeEvaluationScratch.Rent();
        using var scratch = RuntimeEvaluationScratch.Rent();
        var cleanEvaluator = new RuntimeValueEvaluator(cleanScratch, plan, inputs.WithReader(inputs.ReadCleanMemory));
        var evaluator = new RuntimeValueEvaluator(scratch, plan, inputs, cleanFlatSlots, cleanEvaluator);
        if (evaluateTable && plan.ResourceBranches.Count != 0)
            activeSources = EvaluateActiveSources(plan, inputs, cleanEvaluator);
        var evaluated = new List<DescriptorWords>(sources.Count);
        foreach (var sourceIndex in sources)
        {
            if (sourceIndex >= plan.DescriptorSources.Count)
            {
                return false;
            }

            var source = plan.DescriptorSources[(int)sourceIndex];
            var words = new uint[source.DwordCount];
            if (activeSources.Length == 0 || activeSources[sourceIndex])
            {
                for (var index = 0; index < words.Length; index++)
                {
                    if (!evaluator.Evaluate(source.Dwords[index], out words[index]))
                    {
                        if (Environment.GetEnvironmentVariable("SHARPEMU_RESOURCE_TRACKER_DIAG") == "1")
                        {
                            Console.Error.WriteLine($"[RV-EVAL-DIAG] evaluate failed source={sourceIndex} dword={index} Kind={source.Dwords[index].Kind} Op={source.Dwords[index].Operation}");
                        }

                        return false;
                    }
                }
            }

            evaluated.Add(new DescriptorWords(words));
        }

        uint[] flattened = [];
        if (evaluateTable)
        {
            flattened = new uint[checked(plan.TableReads.Count + additionalTableWords)];
            foreach (var read in plan.TableReads)
            {
                var clean = read.FlatOffset < cleanFlatSlots.Count && cleanFlatSlots[(int)read.FlatOffset] != 0;
                var selected = clean ? cleanEvaluator : evaluator;
                if (read.FlatOffset >= plan.TableReads.Count || !selected.Evaluate(read.Value, out var word))
                {
                    if (Environment.GetEnvironmentVariable("SHARPEMU_RESOURCE_TRACKER_DIAG") == "1")
                    {
                        Console.Error.WriteLine($"[RV-EVAL-DIAG] evaluate failed tableRead FlatOffset={read.FlatOffset} Kind={read.Value.Kind} Op={read.Value.Operation}");
                        DumpValueTree(read.Value, 0, 6);
                    }

                    return false;
                }

                flattened[(int)read.FlatOffset] = word;
            }
        }

        results = evaluated;
        table = flattened;
        return true;
    }

    private static void DumpValueTree(ScalarValue value, int depth, int maxDepth)
    {
        var indent = new string(' ', depth * 2);
        var payload = value.Kind is ScalarValueKind.Constant or ScalarValueKind.UserData or ScalarValueKind.ScalarAddressWord or
            ScalarValueKind.ScalarBufferWord or ScalarValueKind.ResourceTableWord or ScalarValueKind.Phi
            ? $" Payload={value.Payload}"
            : "";
        Console.Error.WriteLine($"[RV-EVAL-DIAG] {indent}Kind={value.Kind} Op={value.Operation}{payload} Operands={value.Operands.Length}");
        if (depth >= maxDepth)
        {
            return;
        }

        foreach (var operand in value.Operands)
        {
            DumpValueTree(operand, depth + 1, maxDepth);
        }
    }

    private static bool[] EvaluateActiveSources(ShaderResourcePlan plan, ResourceRuntimeInputs inputs, RuntimeValueEvaluator cleanEvaluator)
    {
        var activeSources = new bool[plan.DescriptorSources.Count];
        Array.Fill(activeSources, true);
        foreach (var block in plan.ResourceBranches)
            foreach (var source in block.Sources) activeSources[source] = false;

        var visited = new bool[plan.ResourceBranches.Count];
        var pending = new Stack<int>();
        pending.Push(0);
        while (pending.TryPop(out var blockIndex))
        {
            if (visited[blockIndex]) continue;
            visited[blockIndex] = true;
            var block = plan.ResourceBranches[blockIndex];
            foreach (var source in block.Sources) activeSources[source] = true;
            // An unreadable predicate keeps both paths; never use the general reader to choose one.
            if (block.Condition is { } condition && inputs.ReadCleanMemory is not null && cleanEvaluator.Evaluate(condition, out var value))
                pending.Push(block.Successors[value != 0 ? 0 : 1]);
            else
                foreach (var successor in block.Successors) pending.Push(successor);
        }

        ResourceMaterializationProfile.RecordActivity(activeSources);
        return activeSources;
    }

    public static bool EvaluateDescriptorSource(ShaderResourcePlan plan, uint source, ResourceRuntimeInputs inputs, out DescriptorWords result)
    {
        result = default;
        if (!EvaluateSources(plan, [source], inputs, [], evaluateTable: false, out var results, out _))
        {
            return false;
        }

        result = results[0];
        return true;
    }

    public static bool FlattenResourceTable(ShaderResourcePlan plan, ResourceRuntimeInputs inputs, out uint[] table) =>
        EvaluateSources(plan, [], inputs, [], evaluateTable: true, out _, out table);
}

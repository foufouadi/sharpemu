// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Gpu.Pipelines;

// TEMP DIAG (SHARPEMU_DIAG_DESCRIPTOR_TABLES=1): where the V# of every formatted buffer load
// comes from, evaluated at dispatch:
//   fixed   - all four V# words evaluate now, so the CPU could bind the buffer itself;
//   table   - the V# is entry i of a table the CPU can locate (i is only known in the shader);
//   unknown - its source cannot be evaluated at dispatch.
// For each it reports whether the GPU wrote the bytes the V# was read from (byte granular)
// and the unified formats seen. Each (shader, V#, source) is logged a few times at most.
internal static class DescriptorTableDiag
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_DIAG_DESCRIPTOR_TABLES") == "1";

    private const int MaxLinesPerHandle = 4;
    private static readonly Dictionary<(ulong Hash, ScalarValue Handle), HashSet<ulong>> Seen = [];

    public static void Report(ulong hash, ShaderResourcePlan plan, ResourceRuntimeInputs inputs, IShaderPipelineHost host)
    {
        var evaluator = new RuntimeValueEvaluator(plan, inputs);
        var handles = new Dictionary<ScalarValue, List<int>>();
        for (var index = 0; index < plan.Memory.Count; index++)
        {
            var memory = plan.Memory[index];
            if (!memory.Formatted || memory.Kind != MemoryResourceKind.Buffer || memory.PlanningOnly) continue;
            if (plan.Accesses[index]?.Handle is not { Kind: ScalarValueKind.BufferHandle, Operands.Length: 4 } handle) continue;
            if (!handles.TryGetValue(handle, out var list)) handles[handle] = list = [];
            list.Add(index);
        }

        foreach (var (handle, indices) in handles)
        {
            var memory = plan.Memory[indices[0]];
            var path = memory.DeviceDescriptor ? "device" : memory.BufferDescriptor?.Provenance == BufferDescriptorProvenance.Runtime ? "candidates" : "bound";
            string line;
            ulong sourceKey;
            if (TryEvaluateAll(evaluator, handle, out var words))
            {
                var source = SourceRange(plan, evaluator, handle.Operands[0], out _);
                var gpuWritten = source is { } range && host.DiagGpuWroteBytes(range.Address, 16);
                sourceKey = words[0] ^ ((ulong)words[3] << 32);
                line = $"kind=fixed format={(words[3] >> 12) & 0x7F} base=0x{words[0] | ((ulong)(words[1] & 0xFFFF) << 32):X} " +
                    $"read_from={(source is { } at ? $"0x{at.Address:X}" : "registers")} gpu_written={gpuWritten}";
            }
            else if (TryLocateTable(plan, evaluator, handle.Operands[3], out var table))
            {
                sourceKey = table.Base;
                var entries = table.Size == 0 ? 16UL : Math.Min(table.Size / table.Stride + 1, 4096UL);
                var gpuWritten = host.DiagGpuWroteBytes(table.Base, table.Size == 0 ? entries * table.Stride : table.Size);
                var formats = new SortedDictionary<uint, int>();
                var word = new byte[4];
                for (ulong entry = 0; entry < entries; entry++)
                {
                    var at = table.Base + table.Word3Offset + entry * table.Stride;
                    if (table.Size != 0 && at + 4 > table.Base + table.Size) break;
                    if (!host.TryReadResidentGuestBytes(at, word, clean: false)) break;
                    var format = (BitConverter.ToUInt32(word) >> 12) & 0x7F;
                    formats[format] = formats.GetValueOrDefault(format) + 1;
                }

                line = $"kind=table table=0x{table.Base:X} size=0x{table.Size:X} entry_stride={table.Stride} gpu_written={gpuWritten} " +
                    $"formats={string.Join(',', formats.Select(pair => $"{pair.Key}x{pair.Value}"))}";
            }
            else
            {
                sourceKey = 0;
                line = $"kind=unknown words={string.Join(',', handle.Operands.Select(operand => operand.Kind))}";
            }

            lock (Seen)
            {
                if (!Seen.TryGetValue((hash, handle), out var sources)) Seen[(hash, handle)] = sources = [];
                if (sources.Count >= MaxLinesPerHandle || !sources.Add(sourceKey)) continue;
            }

            Console.Error.WriteLine($"[DIAG][DESC] hash=0x{hash:X16} loads={indices.Count} path={path} {line}");
        }
    }

    // SHARPEMU_DIAG_ACCESS_HASHES=<hash,...>: once per listed shader, every memory access with
    // the class of its source at dispatch: fixed (all handle words evaluate), table (indexed
    // table the CPU can locate), unknown. Joined with [SHADER][EMIT_COST] by pc offline.
    private static readonly HashSet<ulong> AccessHashes = (Environment.GetEnvironmentVariable("SHARPEMU_DIAG_ACCESS_HASHES") ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(text => Convert.ToUInt64(text.Replace("0x", string.Empty), 16))
        .ToHashSet();
    private static readonly HashSet<ulong> AccessesReported = [];

    public static void ReportAccesses(ulong hash, ShaderResourcePlan plan, ResourceRuntimeInputs inputs, IShaderPipelineHost host)
    {
        lock (AccessesReported)
        {
            if (!AccessHashes.Contains(hash) || !AccessesReported.Add(hash)) return;
        }

        var evaluator = new RuntimeValueEvaluator(plan, inputs);
        for (var index = 0; index < plan.Memory.Count; index++)
        {
            var memory = plan.Memory[index];
            if (memory.PlanningOnly) continue;
            var handle = plan.Accesses[index]?.Handle;
            string source;
            if (handle is null)
            {
                source = "no-handle";
            }
            else
            {
                var fixedHandle = true;
                for (var word = 0; word < handle.Operands.Length && fixedHandle; word++)
                {
                    fixedHandle = evaluator.Evaluate(handle.Operands[word], out _);
                }

                source = fixedHandle ? "fixed"
                    : handle.Operands.Length > 0 && TryLocateTable(plan, evaluator, handle.Operands[^1], out _) ? "table"
                    : "unknown";
            }

            var path = memory.DeviceDescriptor ? "device"
                : memory.BufferDescriptor?.Provenance == BufferDescriptorProvenance.Runtime ? "runtime-descriptor"
                : memory.Resource != MemoryAccessInfo.NoResource ? "bound"
                : "address";
            Console.Error.WriteLine(
                $"[DIAG][ACCESS] hash=0x{hash:X16} pc=0x{memory.Pc:X} opcode={memory.Opcode} kind={memory.Kind} path={path} source={source} " +
                $"handle={handle?.Kind.ToString() ?? "-"}");
        }
    }

    private static bool TryEvaluateAll(RuntimeValueEvaluator evaluator, ScalarValue handle, out uint[] words)
    {
        words = new uint[4];
        for (var word = 0; word < 4; word++)
        {
            if (!evaluator.Evaluate(handle.Operands[word], out words[word])) return false;
        }

        return true;
    }

    // The guest address a V# word was loaded from, when it came from memory.
    private static (ulong Address, ulong Size)? SourceRange(ShaderResourcePlan plan, RuntimeValueEvaluator evaluator, ScalarValue word, out ulong dynamicOffset)
    {
        dynamicOffset = 0;
        if (word.Kind is not (ScalarValueKind.ScalarBufferWord or ScalarValueKind.ScalarAddressWord) || word.Operands.Length < 2) return null;
        if (!TryBase(evaluator, word.Operands[0], out var baseAddress, out _)) return null;
        if (!evaluator.Evaluate(word.Operands[1], out var offset)) return null;
        dynamicOffset = offset;
        return (baseAddress + plan.Memory[word.MemoryIndex].Offset + offset, 4);
    }

    private readonly record struct Table(ulong Base, ulong Size, ulong Stride, ulong Word3Offset);

    // A V# read at base + immediate + index * K: the table is at base + immediate, K apart.
    private static bool TryLocateTable(ShaderResourcePlan plan, RuntimeValueEvaluator evaluator, ScalarValue word3, out Table table)
    {
        table = default;
        if (word3.Kind is not (ScalarValueKind.ScalarBufferWord or ScalarValueKind.ScalarAddressWord) || word3.Operands.Length < 2) return false;
        if (!TryBase(evaluator, word3.Operands[0], out var baseAddress, out var size)) return false;
        var offset = word3.Operands[1];
        ulong constantPart = 0;
        if (offset is { Kind: ScalarValueKind.Operation, Operation: ScalarOperation.IAdd32 } &&
            offset.Operands.FirstOrDefault(operand => operand.Kind == ScalarValueKind.Constant) is { } addend)
        {
            constantPart = addend.Payload;
            offset = offset.Operands.First(operand => operand != addend);
        }

        if (offset is not { Kind: ScalarValueKind.Operation, Operation: ScalarOperation.IMul32 or ScalarOperation.ShiftLeft32 } ||
            offset.Operands.FirstOrDefault(operand => operand.Kind == ScalarValueKind.Constant) is not { } factor)
        {
            return false;
        }

        var stride = offset.Operation == ScalarOperation.IMul32 ? factor.Payload : 1UL << (int)factor.Payload;
        if (stride == 0) return false;
        table = new Table(baseAddress, size, stride, plan.Memory[word3.MemoryIndex].Offset + constantPart);
        return true;
    }

    // The base (and, for a V#, the byte size) of the handle a scalar load reads through.
    private static bool TryBase(RuntimeValueEvaluator evaluator, ScalarValue handle, out ulong baseAddress, out ulong size)
    {
        baseAddress = 0;
        size = 0;
        if (handle.Operands.Length < 2 ||
            !evaluator.Evaluate(handle.Operands[0], out var low) ||
            !evaluator.Evaluate(handle.Operands[1], out var high))
        {
            return false;
        }

        baseAddress = low | ((ulong)(high & 0xFFFF) << 32);
        if (handle.Kind == ScalarValueKind.BufferHandle && handle.Operands.Length == 4 && evaluator.Evaluate(handle.Operands[2], out var records))
        {
            var stride = (high >> 16) & 0x3FFF;
            size = stride == 0 ? records : (ulong)records * stride;
        }

        return true;
    }
}

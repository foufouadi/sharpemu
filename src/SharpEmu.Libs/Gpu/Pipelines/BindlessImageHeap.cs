// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Pipelines;

// One update-after-bind image set shared by all translated guest pipelines. The
// shader still has separate Vulkan image types for dimensions and numeric classes,
// but the arrays themselves are runtime arrays owned by this set rather than by a
// shader permutation.
public sealed unsafe class BindlessImageHeap : IDisposable
{
    private sealed class SlotKey : IEquatable<SlotKey>
    {
        public readonly DescriptorBindingKind Kind;
        public readonly ulong View;
        public readonly uint[] Words;

        public SlotKey(DescriptorBindingKind kind, ulong view, ReadOnlySpan<uint> words)
        {
            Kind = kind;
            View = view;
            Words = words.ToArray();
        }

        public bool Equals(SlotKey? other) => other is not null && Kind == other.Kind && View == other.View &&
            Words.AsSpan().SequenceEqual(other.Words);

        public override bool Equals(object? obj) => Equals(obj as SlotKey);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Kind);
            hash.Add(View);
            hash.AddBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(Words.AsSpan()));
            return hash.ToHashCode();
        }
    }

    private readonly GpuDeviceInfo _device;
    private readonly DescriptorSet _set;
    private readonly Dictionary<SlotKey, uint> _slots = new();
    private readonly uint[] _next = new uint[BindingLayout.ImageBindingCount];
    private readonly uint[] _capacity = new uint[BindingLayout.ImageBindingCount];
    private readonly DescriptorSetLayout _layout;

    public BindlessImageHeap(
        GpuDeviceInfo device,
        SubmissionScheduler scheduler,
        uint maxSampledImages,
        uint maxStorageImages,
        uint maxUpdateAfterBindSampledImages,
        uint maxUpdateAfterBindStorageImages,
        uint maxUpdateAfterBindDescriptors,
        IReadOnlyDictionary<DescriptorBindingKind, uint> requested)
    {
        var sampledCount = 0u;
        var storageCount = 0u;
        for (var index = 0u; index < BindingLayout.ImageBindingCount; index++)
        {
            var kind = (DescriptorBindingKind)(BindingLayout.FirstImageBinding + index);
            if (DescriptorWriter.DescriptorType(kind) == DescriptorType.SampledImage) sampledCount++;
            else storageCount++;
        }

        var sampledRequested = new uint[sampledCount];
        var storageRequested = new uint[storageCount];
        for (var index = 0u; index < BindingLayout.ImageBindingCount; index++)
        {
            var kind = (DescriptorBindingKind)(BindingLayout.FirstImageBinding + index);
            var count = Math.Max(requested.GetValueOrDefault(kind), 1u);
            if (DescriptorWriter.DescriptorType(kind) == DescriptorType.SampledImage)
                sampledRequested[SampledIndex(index)] = count;
            else
                storageRequested[StorageIndex(index)] = count;
        }

        var sampledLimit = Math.Min(maxSampledImages, maxUpdateAfterBindSampledImages);
        var storageLimit = Math.Min(maxStorageImages, maxUpdateAfterBindStorageImages);
        var sampledDemand = sampledRequested.Aggregate(0UL, static (sum, value) => sum + value);
        var storageDemand = storageRequested.Aggregate(0UL, static (sum, value) => sum + value);
        var totalDemand = sampledDemand + storageDemand;
        if (totalDemand > maxUpdateAfterBindDescriptors)
        {
            throw SubmissionScheduler.Fatal(
                $"The persistent image heap demand exceeds the update-after-bind limit: " +
                $"requested={totalDemand} limit={maxUpdateAfterBindDescriptors}.");
        }

        var totalLimit = Math.Min((ulong)maxUpdateAfterBindDescriptors, (ulong)sampledLimit + storageLimit);
        var remainder = totalLimit - totalDemand;
        var sampledBudget = sampledDemand + Math.Min(
            (ulong)sampledLimit - sampledDemand,
            totalDemand == 0 ? 0 : remainder * sampledDemand / totalDemand);
        var storageBudget = totalLimit - sampledBudget;
        if (storageBudget > storageLimit)
        {
            var moved = storageBudget - storageLimit;
            storageBudget = storageLimit;
            sampledBudget = Math.Min((ulong)sampledLimit, sampledBudget + moved);
        }

        AllocateCapacities(sampledRequested, (uint)sampledBudget, _capacity, DescriptorType.SampledImage);
        AllocateCapacities(storageRequested, (uint)storageBudget, _capacity, DescriptorType.StorageImage);
        _device = device;
        Console.Error.WriteLine(
            $"[LOADER][INFO] Vulkan bindless heap sampled={sampledBudget}/{sampledLimit} " +
            $"storage={storageBudget}/{storageLimit} requested={totalDemand}/{totalLimit}");

        var bindings = new DescriptorSetLayoutBinding[BindingLayout.ImageBindingCount];
        var flags = new DescriptorBindingFlags[bindings.Length];
        for (var index = 0; index < bindings.Length; index++)
        {
            var kind = (DescriptorBindingKind)(BindingLayout.FirstImageBinding + (uint)index);
            bindings[index] = new DescriptorSetLayoutBinding
            {
                Binding = BindingLayout.FirstImageBinding + (uint)index,
                DescriptorType = DescriptorWriter.DescriptorType(kind),
                DescriptorCount = _capacity[index],
                StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit | ShaderStageFlags.ComputeBit,
            };
            flags[index] = DescriptorBindingFlags.PartiallyBoundBit | DescriptorBindingFlags.UpdateAfterBindBit;
        }

        var bindingFlags = new DescriptorSetLayoutBindingFlagsCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo,
            BindingCount = (uint)flags.Length,
        };
        fixed (DescriptorSetLayoutBinding* bindingPointer = bindings)
        fixed (DescriptorBindingFlags* flagPointer = flags)
        {
            bindingFlags.PBindingFlags = flagPointer;
            var create = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                PNext = &bindingFlags,
                Flags = DescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBit,
                BindingCount = (uint)bindings.Length,
                PBindings = bindingPointer,
            };
            Check(device.Vk.CreateDescriptorSetLayout(device.Device, &create, null, out _layout), "vkCreateDescriptorSetLayout(bindless)");
        }

        var poolSizes = new List<DescriptorPoolSize>();
        var sampled = 0u;
        var storage = 0u;
        for (var index = 0; index < _capacity.Length; index++)
        {
            if (bindings[index].DescriptorType == DescriptorType.SampledImage) sampled += _capacity[index];
            else storage += _capacity[index];
        }
        poolSizes.Add(new DescriptorPoolSize(DescriptorType.SampledImage, sampled));
        poolSizes.Add(new DescriptorPoolSize(DescriptorType.StorageImage, storage));
        fixed (DescriptorPoolSize* poolPointer = poolSizes.ToArray())
        {
            var poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                Flags = DescriptorPoolCreateFlags.UpdateAfterBindBit,
                MaxSets = 1,
                PoolSizeCount = (uint)poolSizes.Count,
                PPoolSizes = poolPointer,
            };
            Check(device.Vk.CreateDescriptorPool(device.Device, &poolInfo, null, out var pool), "vkCreateDescriptorPool(bindless)");
            var setLayout = _layout;
            var allocate = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = pool,
                DescriptorSetCount = 1,
                PSetLayouts = &setLayout,
            };
            Check(device.Vk.AllocateDescriptorSets(device.Device, &allocate, out _set), "vkAllocateDescriptorSets(bindless)");
            _pool = pool;
        }

        // Slot zero is deliberately left as the null descriptor. A draw never maps
        // an image to it unless the descriptor was rejected before residency.
    }

    private static int SampledIndex(uint bindingIndex)
    {
        var count = 0;
        for (var index = 0u; index < bindingIndex; index++)
        {
            if (DescriptorWriter.DescriptorType((DescriptorBindingKind)(BindingLayout.FirstImageBinding + index)) == DescriptorType.SampledImage) count++;
        }
        return count;
    }

    private static int StorageIndex(uint bindingIndex) => (int)bindingIndex - SampledIndex(bindingIndex);

    private static void AllocateCapacities(uint[] requested, uint limit, uint[] destination, DescriptorType type)
    {
        var total = requested.Aggregate(0UL, static (sum, value) => sum + value);
        if (total > limit)
        {
            throw SubmissionScheduler.Fatal($"The persistent image heap demand exceeds the device limit: type={type} requested={total} limit={limit}.");
        }

        var remainder = limit - (uint)total;
        var weightTotal = requested.Aggregate(0UL, static (sum, value) => sum + Math.Max(value, 1u));
        var cursor = 0;
        for (var index = 0u; index < BindingLayout.ImageBindingCount; index++)
        {
            var kind = (DescriptorBindingKind)(BindingLayout.FirstImageBinding + index);
            if (DescriptorWriter.DescriptorType(kind) != type) continue;
            var weight = Math.Max(requested[cursor], 1u);
            var extra = weightTotal == 0 ? 0u : (uint)(remainder * weight / weightTotal);
            destination[index] = requested[cursor] + extra;
            cursor++;
        }

        // Integer rounding leaves a small remainder. Give it to the first slots
        // without exceeding the device limit.
        var assigned = 0UL;
        for (var index = 0u; index < BindingLayout.ImageBindingCount; index++)
        {
            var kind = (DescriptorBindingKind)(BindingLayout.FirstImageBinding + index);
            if (DescriptorWriter.DescriptorType(kind) == type)
            {
                assigned += destination[index];
            }
        }
        for (var index = 0u; index < BindingLayout.ImageBindingCount && assigned < limit; index++)
        {
            var kind = (DescriptorBindingKind)(BindingLayout.FirstImageBinding + index);
            if (DescriptorWriter.DescriptorType(kind) != type) continue;
            destination[index]++;
            assigned++;
        }
    }

    private DescriptorPool _pool;

    public DescriptorSetLayout Layout => _layout;
    public DescriptorSet Set => _set;

    public uint GetOrCreateSlot(DescriptorBindingKind kind, ReadOnlySpan<uint> words, ImageView view, ImageLayout layout)
    {
        var index = ImageDescriptorBinding.ArrayIndex(kind);
        if (index >= _capacity.Length || view.Handle == 0)
        {
            throw SubmissionScheduler.Fatal($"The bindless image slot is invalid: kind={kind} view=0x{view.Handle:X}.");
        }

        var key = new SlotKey(kind, view.Handle, words);
        if (_slots.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var slot = ++_next[index];
        if (slot >= _capacity[index])
        {
            throw SubmissionScheduler.Fatal($"The bindless image heap is full: kind={kind} capacity={_capacity[index]}.");
        }

        var info = new DescriptorImageInfo { ImageView = view, ImageLayout = layout };
        DescriptorImageInfo* infoPointer = &info;
        {
            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _set,
                DstBinding = BindingLayout.FirstImageBinding + index,
                DstArrayElement = slot,
                DescriptorCount = 1,
                DescriptorType = DescriptorWriter.DescriptorType(kind),
                PImageInfo = infoPointer,
            };
            _device.Vk.UpdateDescriptorSets(_device.Device, 1, &write, 0, null);
        }
        _slots.Add(key, slot);
        return slot;
    }

    public void Dispose()
    {
        if (_pool.Handle != 0) _device.Vk.DestroyDescriptorPool(_device.Device, _pool, null);
        if (_layout.Handle != 0) _device.Vk.DestroyDescriptorSetLayout(_device.Device, _layout, null);
        _slots.Clear();
    }

    private static void Check(Result result, string operation)
    {
        if (result != Result.Success) throw SubmissionScheduler.Fatal($"{operation} failed: result={result}.");
    }
}

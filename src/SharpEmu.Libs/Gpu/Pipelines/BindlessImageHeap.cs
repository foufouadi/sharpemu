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
    private const uint BindingCount = 2;
    // NVIDIA reports maxUpdateAfterBindDescriptorsInAllPools as UINT_MAX. Keep
    // the persistent layout bounded even when that device-wide limit is not
    // useful, while retaining the full capacity on devices with lower limits.
    private const uint StableCapacityPerBinding = 128u * 1024u;

    private sealed class SlotKey : IEquatable<SlotKey>
    {
        public readonly DescriptorBindingKind Kind;
        public readonly ulong View;
        public readonly ImageLayout Layout;
        public readonly uint[] Words;

        public SlotKey(DescriptorBindingKind kind, ulong view, ImageLayout layout, ReadOnlySpan<uint> words)
        {
            Kind = kind;
            View = view;
            Layout = layout;
            Words = words.ToArray();
        }

        public bool Equals(SlotKey? other) => other is not null && Kind == other.Kind && View == other.View &&
            Layout == other.Layout &&
            Words.AsSpan().SequenceEqual(other.Words);

        public override bool Equals(object? obj) => Equals(obj as SlotKey);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Kind);
            hash.Add(View);
            hash.Add(Layout);
            hash.AddBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(Words.AsSpan()));
            return hash.ToHashCode();
        }
    }

    private readonly GpuDeviceInfo _device;
    private readonly SubmissionScheduler _scheduler;
    private readonly DescriptorSet _set;
    private readonly Dictionary<SlotKey, uint> _slots = new();
    private readonly List<uint>[] _free = [[], []];
    private readonly uint[] _next = new uint[BindingCount];
    private readonly uint[] _capacity = new uint[BindingCount];
    private readonly DescriptorSetLayout _layout;

    public BindlessImageHeap(
        GpuDeviceInfo device,
        SubmissionScheduler scheduler,
        uint maxPerStageSampledImages,
        uint maxPerStageStorageImages,
        uint maxPerStageUpdateAfterBindSampledImages,
        uint maxPerStageUpdateAfterBindStorageImages,
        uint maxUpdateAfterBindSampledImages,
        uint maxUpdateAfterBindStorageImages,
        uint maxUpdateAfterBindDescriptors)
    {
        _scheduler = scheduler;
        var sampledLimit = Math.Min(
            Math.Min(Math.Min(maxPerStageSampledImages, maxPerStageUpdateAfterBindSampledImages), maxUpdateAfterBindSampledImages),
            StableCapacityPerBinding);
        var storageLimit = Math.Min(
            Math.Min(Math.Min(maxPerStageStorageImages, maxPerStageUpdateAfterBindStorageImages), maxUpdateAfterBindStorageImages),
            StableCapacityPerBinding);
        var totalLimit = Math.Min((ulong)maxUpdateAfterBindDescriptors, (ulong)sampledLimit + storageLimit);
        var sampledCapacity = Math.Min((ulong)sampledLimit, totalLimit / 2);
        var storageCapacity = Math.Min((ulong)storageLimit, totalLimit - sampledCapacity);
        if (sampledCapacity == 0 || storageCapacity == 0)
        {
            throw SubmissionScheduler.Fatal(
                $"The device has no usable persistent image heap capacity: sampled={sampledLimit} storage={storageLimit} total={maxUpdateAfterBindDescriptors}.");
        }

        // If one type has a smaller limit, give the unused half to the other type.
        var unassigned = totalLimit - sampledCapacity - storageCapacity;
        if (unassigned != 0)
        {
            var sampledRoom = (ulong)sampledLimit - sampledCapacity;
            var sampledExtra = Math.Min(unassigned, sampledRoom);
            sampledCapacity += sampledExtra;
            storageCapacity += Math.Min(unassigned - sampledExtra, (ulong)storageLimit - storageCapacity);
        }

        _capacity[0] = (uint)sampledCapacity;
        _capacity[1] = (uint)storageCapacity;
        _device = device;
        Console.Error.WriteLine(
            $"[LOADER][INFO] Vulkan bindless heap sampled={sampledCapacity}/{sampledLimit} " +
            $"storage={storageCapacity}/{storageLimit} total={sampledCapacity + storageCapacity}/{totalLimit}");

        var bindings = new DescriptorSetLayoutBinding[BindingCount];
        var flags = new DescriptorBindingFlags[bindings.Length];
        for (var index = 0u; index < bindings.Length; index++)
        {
            bindings[index] = new DescriptorSetLayoutBinding
            {
                Binding = index,
                DescriptorType = index == 0 ? DescriptorType.SampledImage : DescriptorType.StorageImage,
                DescriptorCount = _capacity[index],
                StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit | ShaderStageFlags.ComputeBit,
            };
            flags[index] = DescriptorBindingFlags.PartiallyBoundBit |
                DescriptorBindingFlags.UpdateAfterBindBit |
                DescriptorBindingFlags.UpdateUnusedWhilePendingBit;
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
        poolSizes.Add(new DescriptorPoolSize(DescriptorType.SampledImage, _capacity[0]));
        poolSizes.Add(new DescriptorPoolSize(DescriptorType.StorageImage, _capacity[1]));
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

    private DescriptorPool _pool;

    public DescriptorSetLayout Layout => _layout;
    public DescriptorSet Set => _set;

    public uint GetOrCreateSlot(DescriptorBindingKind kind, ReadOnlySpan<uint> words, ImageView view, ImageLayout layout)
    {
        var index = DescriptorWriter.DescriptorType(kind) == DescriptorType.SampledImage ? 0u : 1u;
        if (index >= _capacity.Length || view.Handle == 0)
        {
            throw SubmissionScheduler.Fatal($"The bindless image slot is invalid: kind={kind} view=0x{view.Handle:X}.");
        }

        var key = new SlotKey(kind, view.Handle, layout, words);
        if (_slots.TryGetValue(key, out var existing))
        {
            return existing;
        }

        uint slot;
        if (_free[index].Count != 0)
        {
            slot = _free[index][^1];
            _free[index].RemoveAt(_free[index].Count - 1);
        }
        else
        {
            slot = ++_next[index];
            if (slot >= _capacity[index])
            {
                throw SubmissionScheduler.Fatal($"The bindless image heap is full: kind={kind} capacity={_capacity[index]}.");
            }
        }

        var info = new DescriptorImageInfo { ImageView = view, ImageLayout = layout };
        DescriptorImageInfo* infoPointer = &info;
        {
            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _set,
                DstBinding = index,
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

    public void InvalidateViews(IReadOnlyList<ImageView> views)
    {
        if (views.Count == 0)
        {
            return;
        }

        var handles = views.Select(static view => view.Handle).ToHashSet();
        var retired = new List<(uint Binding, uint Slot)>();
        foreach (var pair in _slots.Where(pair => handles.Contains(pair.Key.View)).ToArray())
        {
            var binding = DescriptorWriter.DescriptorType(pair.Key.Kind) == DescriptorType.SampledImage ? 0u : 1u;
            _slots.Remove(pair.Key);
            retired.Add((binding, pair.Value));
        }

        if (retired.Count != 0)
        {
            _scheduler.QueueCompletionAction(() =>
            {
                foreach (var (binding, slot) in retired)
                {
                    WriteNullDescriptor(binding, slot);
                    _free[binding].Add(slot);
                }
            });
        }
    }

    private void WriteNullDescriptor(uint binding, uint slot)
    {
        var info = new DescriptorImageInfo { ImageLayout = ImageLayout.Undefined };
        DescriptorImageInfo* infoPointer = &info;
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _set,
            DstBinding = binding,
            DstArrayElement = slot,
            DescriptorCount = 1,
            DescriptorType = binding == 0 ? DescriptorType.SampledImage : DescriptorType.StorageImage,
            PImageInfo = infoPointer,
        };
        _device.Vk.UpdateDescriptorSets(_device.Device, 1, &write, 0, null);
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

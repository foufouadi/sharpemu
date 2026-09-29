// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Buffers;

// The device facts the stores need: handles, memory types, limits, format support
// and the count of live device-memory allocations made through this object.
public sealed unsafe class GpuDeviceInfo : IImageFormatSupport
{
    private PhysicalDeviceMemoryProperties _memoryProperties;
    private readonly Dictionary<Format, FormatProperties> _formatProperties = new();
    private readonly Dictionary<(Format, ImageType, ImageTiling, ImageUsageFlags, ImageCreateFlags), (Result Result, ImageFormatProperties Properties)> _imageFormatProperties = new();
    private readonly object _gate = new();
    private int _liveAllocations;
    private int _peakAllocations;

    public GpuDeviceInfo(Vk vk, PhysicalDevice physicalDevice, Device device, bool memoryBudgetEnabled = false)
    {
        Vk = vk;
        PhysicalDevice = physicalDevice;
        Device = device;
        vk.GetPhysicalDeviceMemoryProperties(physicalDevice, out _memoryProperties);
        DeviceLocalHeapBytes = CalculateDeviceLocalHeapBytes();
        var queriedMemory = memoryBudgetEnabled
            ? QueryDeviceLocalMemory()
            : (Budget: 0UL, Usage: 0UL, Available: 0UL);
        if (queriedMemory.Budget != 0)
        {
            DeviceLocalBudgetBytes = queriedMemory.Budget;
            DeviceLocalUsageBytes = queriedMemory.Usage;
            DeviceLocalAvailableBytes = queriedMemory.Available;
            HasMemoryBudget = true;
        }
        else
        {
            DeviceLocalBudgetBytes = DeviceLocalHeapBytes;
            DeviceLocalUsageBytes = 0;
            DeviceLocalAvailableBytes = DeviceLocalHeapBytes;
            HasMemoryBudget = false;
        }
        vk.GetPhysicalDeviceProperties(physicalDevice, out var properties);
        MinUniformBufferOffsetAlignment = Math.Max(properties.Limits.MinUniformBufferOffsetAlignment, 1);
        MinStorageBufferOffsetAlignment = Math.Max(properties.Limits.MinStorageBufferOffsetAlignment, 1);
        NonCoherentAtomSize = Math.Max(properties.Limits.NonCoherentAtomSize, 1);
        MaxStorageBufferRange = properties.Limits.MaxStorageBufferRange;
        MaxMemoryAllocationCount = properties.Limits.MaxMemoryAllocationCount;
        MaxComputeWorkGroupCount = (properties.Limits.MaxComputeWorkGroupCount[0], properties.Limits.MaxComputeWorkGroupCount[1], properties.Limits.MaxComputeWorkGroupCount[2]);
    }

    public Vk Vk { get; }

    public PhysicalDevice PhysicalDevice { get; }

    public Device Device { get; }

    public ulong DeviceLocalHeapBytes { get; }

    public ulong DeviceLocalBudgetBytes { get; private set; }

    public ulong DeviceLocalUsageBytes { get; private set; }

    public ulong DeviceLocalAvailableBytes { get; private set; }

    public bool HasMemoryBudget { get; }

    public ulong MinUniformBufferOffsetAlignment { get; }

    public ulong MinStorageBufferOffsetAlignment { get; }

    public ulong NonCoherentAtomSize { get; }

    public uint MaxStorageBufferRange { get; }

    public uint MaxMemoryAllocationCount { get; }

    private ImageMemoryPool? _imageMemory;

    // Images share pooled device-memory blocks; see ImageMemoryPool.
    public ImageMemoryPool ImageMemory => _imageMemory ??= new ImageMemoryPool(this);

    public ulong ImageMemoryAllocatedBytes => _imageMemory?.AllocatedBytes ?? 0;

    public ulong ImageMemoryPlacedBytes => _imageMemory?.PlacedBytes ?? 0;

    public (uint X, uint Y, uint Z) MaxComputeWorkGroupCount { get; }

    public uint MemoryTypeCount => _memoryProperties.MemoryTypeCount;

    private ulong CalculateDeviceLocalHeapBytes()
    {
        var usedHeaps = new bool[_memoryProperties.MemoryHeapCount];
        fixed (PhysicalDeviceMemoryProperties* properties = &_memoryProperties)
        {
            var memoryTypes = &properties->MemoryTypes.Element0;
            for (var index = 0u; index < properties->MemoryTypeCount; index++)
            {
                if ((memoryTypes[index].PropertyFlags & MemoryPropertyFlags.DeviceLocalBit) != 0)
                {
                    usedHeaps[(int)memoryTypes[index].HeapIndex] = true;
                }
            }

            var heaps = &properties->MemoryHeaps.Element0;
            ulong total = 0;
            for (var index = 0u; index < properties->MemoryHeapCount; index++)
            {
                if (usedHeaps[(int)index])
                {
                    total += heaps[index].Size;
                }
            }

            return total;
        }
    }

    private (ulong Budget, ulong Usage, ulong Available) QueryDeviceLocalMemory()
    {
        var budget = new PhysicalDeviceMemoryBudgetPropertiesEXT
        {
            SType = StructureType.PhysicalDeviceMemoryBudgetPropertiesExt,
        };
        var properties = new PhysicalDeviceMemoryProperties2
        {
            SType = StructureType.PhysicalDeviceMemoryProperties2,
            PNext = &budget,
        };
        Vk.GetPhysicalDeviceMemoryProperties2(PhysicalDevice, &properties);

        var usedHeaps = new bool[_memoryProperties.MemoryHeapCount];
        fixed (PhysicalDeviceMemoryProperties* memoryProperties = &_memoryProperties)
        {
            var memoryTypes = &memoryProperties->MemoryTypes.Element0;
            for (var index = 0u; index < memoryProperties->MemoryTypeCount; index++)
            {
                if ((memoryTypes[index].PropertyFlags & MemoryPropertyFlags.DeviceLocalBit) != 0)
                {
                    usedHeaps[(int)memoryTypes[index].HeapIndex] = true;
                }
            }
        }

        ulong totalBudget = 0;
        ulong totalUsage = 0;
        ulong totalAvailable = 0;
        for (var index = 0u; index < _memoryProperties.MemoryHeapCount; index++)
        {
            if (usedHeaps[(int)index])
            {
                var heapBudget = budget.HeapBudget[index];
                var heapUsage = budget.HeapUsage[index];
                totalBudget = checked(totalBudget + heapBudget);
                totalUsage = checked(totalUsage + heapUsage);
                totalAvailable = checked(totalAvailable + CalculateAvailableBytes(heapBudget, heapUsage));
            }
        }

        return totalBudget != 0
            ? (totalBudget, totalUsage, totalAvailable)
            : (0, 0, 0);
    }

    internal static ulong CalculateAvailableBytes(ulong budget, ulong usage) =>
        budget > usage ? budget - usage : 0;

    public bool RefreshMemoryBudget()
    {
        if (!HasMemoryBudget)
        {
            return false;
        }

        var queriedMemory = QueryDeviceLocalMemory();
        if (queriedMemory.Budget == 0)
        {
            return false;
        }

        DeviceLocalBudgetBytes = queriedMemory.Budget;
        DeviceLocalUsageBytes = queriedMemory.Usage;
        DeviceLocalAvailableBytes = queriedMemory.Available;
        return true;
    }

    public int LiveAllocations => Volatile.Read(ref _liveAllocations);

    public int PeakAllocations => Volatile.Read(ref _peakAllocations);

    public MemoryPropertyFlags GetMemoryTypeFlags(uint index)
    {
        fixed (PhysicalDeviceMemoryProperties* properties = &_memoryProperties)
        {
            return (&properties->MemoryTypes.Element0)[index].PropertyFlags;
        }
    }

    public FormatProperties GetFormatProperties(Format format)
    {
        lock (_gate)
        {
            if (!_formatProperties.TryGetValue(format, out var properties))
            {
                Vk.GetPhysicalDeviceFormatProperties(PhysicalDevice, format, out properties);
                _formatProperties[format] = properties;
            }

            return properties;
        }
    }

    public bool TryGetImageFormatProperties(Format format, ImageType type, ImageTiling tiling, ImageUsageFlags usage, ImageCreateFlags flags, out ImageFormatProperties properties)
    {
        lock (_gate)
        {
            var key = (format, type, tiling, usage, flags);
            if (!_imageFormatProperties.TryGetValue(key, out var entry))
            {
                var result = Vk.GetPhysicalDeviceImageFormatProperties(PhysicalDevice, format, type, tiling, usage, flags, out var found);
                entry = (result, found);
                _imageFormatProperties[key] = entry;
            }

            properties = entry.Properties;
            return entry.Result == Result.Success;
        }
    }

    // Every device-memory allocation goes through here so the live count stays exact.
    public Result AllocateMemory(in MemoryAllocateInfo info, out DeviceMemory memory)
    {
        fixed (MemoryAllocateInfo* pointer = &info)
        {
            var result = Vk.AllocateMemory(Device, pointer, null, out memory);
            if (result == Result.Success)
            {
                var live = Interlocked.Increment(ref _liveAllocations);
                int peak;
                while ((peak = Volatile.Read(ref _peakAllocations)) < live &&
                       Interlocked.CompareExchange(ref _peakAllocations, live, peak) != peak)
                {
                }
            }

            return result;
        }
    }

    public void FreeMemory(DeviceMemory memory)
    {
        if (memory.Handle == 0)
        {
            return;
        }

        Vk.FreeMemory(Device, memory, null);
        Interlocked.Decrement(ref _liveAllocations);
    }
}

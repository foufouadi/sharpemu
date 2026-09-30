// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Buffers;

// The device facts the stores need: handles, memory types, limits, format support
// and the count of live device-memory allocations made through this object.
public sealed unsafe class GpuDeviceInfo : IImageFormatSupport
{
    private PhysicalDeviceMemoryProperties _memoryProperties;
    // Queried for every draw target from the render thread and from other threads; a hit takes no lock.
    // Two threads may query the same missing key once each; the driver answers identically.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Format, FormatProperties> _formatProperties = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(Format, ImageType, ImageTiling, ImageUsageFlags, ImageCreateFlags), (Result Result, ImageFormatProperties Properties)> _imageFormatProperties = new();
    private int _liveAllocations;
    private int _peakAllocations;

    // VK_EXT_image_view_min_lod is enabled, so a view can clamp to a texture descriptor's MIN_LOD.
    public bool ImageViewMinLodSupported { get; init; }

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
        Slabs = new GpuMemorySlabs(this);
    }

    // Shared chunks the small buffers are carved from; freed at device teardown.
    internal GpuMemorySlabs Slabs { get; }

    public Vk Vk { get; }

    // Queue families that may access every buffer; set before the first buffer is
    // created when a second queue family (the async readback queue) reads them.
    public uint[]? SharedQueueFamilies { get; set; }

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

    // Small device-local buffers (the 16 KiB pages demand paging creates by the thousand)
    // share 64 MiB blocks instead of one driver allocation each.
    private ImageMemoryPool? _bufferMemory;

    public ImageMemoryPool BufferMemory => _bufferMemory ??= new ImageMemoryPool(this, 64UL << 20, deviceAddress: true);

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
        if (!_formatProperties.TryGetValue(format, out var properties))
        {
            Vk.GetPhysicalDeviceFormatProperties(PhysicalDevice, format, out properties);
            _formatProperties[format] = properties;
        }

        return properties;
    }

    public bool TryGetImageFormatProperties(Format format, ImageType type, ImageTiling tiling, ImageUsageFlags usage, ImageCreateFlags flags, out ImageFormatProperties properties)
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

    // TEMP DIAG: live device memory by memory type index.
    internal static readonly long[] DiagBytesByType = new long[32];
    internal static readonly long[] DiagCountByType = new long[32];
    internal static long DiagAllocations;
    internal static long DiagFrees;
    internal static long DiagAllocatedBytes;
    private static int _diagLargeLogged;
    private static int _diagSmallLogged;
    private static int _diagExactLogged;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, (uint Type, ulong Size)> DiagLive = new();

    internal string DiagDescribeMemoryTypes()
    {
        var parts = new List<string>();
        for (uint type = 0; type < 32; type++)
        {
            var bytes = Interlocked.Read(ref DiagBytesByType[type]);
            if (bytes == 0) continue;
            parts.Add($"type{type}[{GetMemoryTypeFlags(type)}]={bytes >> 20}MB/n{Interlocked.Read(ref DiagCountByType[type])}");
        }

        return string.Join(' ', parts);
    }

    // Every device-memory allocation goes through here so the live count stays exact.
    public Result AllocateMemory(in MemoryAllocateInfo info, out DeviceMemory memory)
    {
        fixed (MemoryAllocateInfo* pointer = &info)
        {
            var result = Vk.AllocateMemory(Device, pointer, null, out memory);
            if (result == Result.Success)
            {
                // TEMP DIAG: live bytes per memory type for the periodic memory report.
                DiagLive[memory.Handle] = (info.MemoryTypeIndex, info.AllocationSize);
                Interlocked.Add(ref DiagBytesByType[info.MemoryTypeIndex & 31], (long)info.AllocationSize);
                Interlocked.Increment(ref DiagCountByType[info.MemoryTypeIndex & 31]);
                Interlocked.Increment(ref DiagAllocations);
                // Small allocations: one sample stack every 500 once past 1500 live allocations.
                if (info.AllocationSize < 1UL << 20 && Interlocked.Read(ref DiagAllocations) > 1500 &&
                    Interlocked.Read(ref DiagAllocations) % 500 == 0 && Interlocked.Increment(ref _diagSmallLogged) <= 12)
                {
                    var smallFrames = new System.Diagnostics.StackTrace(1, false).GetFrames()
                        .Select(frame => frame.GetMethod())
                        .Where(method => method is not null)
                        .Take(8)
                        .Select(method => $"{method!.DeclaringType?.Name}.{method.Name}");
                    Console.Error.WriteLine($"[DIAG][SMALL_ALLOC] size_kb={info.AllocationSize >> 10} type={info.MemoryTypeIndex} from {string.Join(" < ", smallFrames)}");
                }

                if (info.AllocationSize == 0x1FE0000 && Interlocked.Increment(ref _diagExactLogged) <= 6)
                {
                    var exactFrames = new System.Diagnostics.StackTrace(1, false).GetFrames()
                        .Select(frame => frame.GetMethod())
                        .Where(method => method is not null)
                        .Take(10)
                        .Select(method => $"{method!.DeclaringType?.Name}.{method.Name}");
                    Console.Error.WriteLine($"[DIAG][ALLOC_1FE0000] count={Interlocked.Read(ref DiagAllocations)} from {string.Join(" < ", exactFrames)}");
                }

                if (info.AllocationSize >= 64UL << 20 && Interlocked.Increment(ref _diagLargeLogged) <= 40)
                {
                    var frames = new System.Diagnostics.StackTrace(1, false).GetFrames()
                        .Select(frame => frame.GetMethod())
                        .Where(method => method is not null)
                        .Take(6)
                        .Select(method => $"{method!.DeclaringType?.Name}.{method.Name}");
                    Console.Error.WriteLine($"[DIAG][LARGE_ALLOC] size_mb={info.AllocationSize >> 20} type={info.MemoryTypeIndex} from {string.Join(" < ", frames)}");
                }
                Interlocked.Add(ref DiagAllocatedBytes, (long)info.AllocationSize);
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

        if (DiagLive.TryRemove(memory.Handle, out var freed)) // TEMP DIAG
        {
            Interlocked.Add(ref DiagBytesByType[freed.Type & 31], -(long)freed.Size);
            Interlocked.Decrement(ref DiagCountByType[freed.Type & 31]);
            Interlocked.Increment(ref DiagFrees);
        }

        Vk.FreeMemory(Device, memory, null);
        Interlocked.Decrement(ref _liveAllocations);
    }
}

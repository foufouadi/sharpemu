// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Buffers;

// A place in device memory an image is bound to: its own allocation, or a range of a block.
public readonly record struct ImageMemory(DeviceMemory Memory, ulong Offset, ulong Size, int Block);

// Images share large device-memory blocks instead of one allocation each: a driver caps the
// number of live allocations (4096 on common Windows drivers) far below the number of textures
// a bindless title keeps resident. Images are all optimal-tiled, so no linear/optimal
// granularity separates them; each range only honours its own alignment.
public sealed class ImageMemoryPool
{
    public const ulong BlockSize = 256UL << 20;
    public const ulong DedicatedThreshold = 64UL << 20;

    private sealed class Block
    {
        public required DeviceMemory Memory;
        public required uint MemoryType;
        public readonly SortedDictionary<ulong, ulong> Free = new();
        public ulong Used;
    }

    private readonly GpuDeviceInfo _device;
    private readonly List<Block?> _blocks = [];
    private readonly object _gate = new();

    public ImageMemoryPool(GpuDeviceInfo device)
    {
        _device = device;
    }

    public Result Allocate(in MemoryRequirements requirements, uint memoryType, out ImageMemory memory)
    {
        memory = default;
        if (requirements.Size >= DedicatedThreshold)
        {
            var info = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = requirements.Size, MemoryTypeIndex = memoryType };
            var dedicated = _device.AllocateMemory(info, out var handle);
            if (dedicated == Result.Success)
            {
                memory = new ImageMemory(handle, 0, requirements.Size, -1);
            }

            return dedicated;
        }

        var alignment = Math.Max(requirements.Alignment, 1UL);
        lock (_gate)
        {
            for (var index = 0; index < _blocks.Count; index++)
            {
                if (_blocks[index] is { } block && block.MemoryType == memoryType && TryPlace(block, requirements.Size, alignment, out var offset))
                {
                    memory = new ImageMemory(block.Memory, offset, requirements.Size, index);
                    return Result.Success;
                }
            }

            var blockInfo = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = BlockSize, MemoryTypeIndex = memoryType };
            var result = _device.AllocateMemory(blockInfo, out var blockMemory);
            if (result != Result.Success)
            {
                return result;
            }

            var created = new Block { Memory = blockMemory, MemoryType = memoryType };
            created.Free.Add(0, BlockSize);
            var slot = _blocks.IndexOf(null);
            if (slot < 0)
            {
                slot = _blocks.Count;
                _blocks.Add(created);
            }
            else
            {
                _blocks[slot] = created;
            }

            TryPlace(created, requirements.Size, alignment, out var placed);
            memory = new ImageMemory(blockMemory, placed, requirements.Size, slot);
            return Result.Success;
        }
    }

    public void Free(in ImageMemory memory)
    {
        if (memory.Memory.Handle == 0)
        {
            return;
        }

        if (memory.Block < 0)
        {
            _device.FreeMemory(memory.Memory);
            return;
        }

        lock (_gate)
        {
            var block = _blocks[memory.Block] ?? throw new InvalidOperationException("The image memory block was already released.");
            var start = memory.Offset;
            var end = memory.Offset + memory.Size;

            // Merge with the free ranges on either side.
            foreach (var (freeStart, freeLength) in block.Free)
            {
                if (freeStart + freeLength == start)
                {
                    start = freeStart;
                    block.Free.Remove(freeStart);
                    break;
                }
            }

            if (block.Free.TryGetValue(end, out var nextLength))
            {
                block.Free.Remove(end);
                end += nextLength;
            }

            block.Free[start] = end - start;
            block.Used -= memory.Size;
            if (block.Used == 0)
            {
                _device.FreeMemory(block.Memory);
                _blocks[memory.Block] = null;
            }
        }
    }

    private static bool TryPlace(Block block, ulong size, ulong alignment, out ulong offset)
    {
        foreach (var (start, length) in block.Free)
        {
            var aligned = (start + alignment - 1) / alignment * alignment;
            var padding = aligned - start;
            if (padding > length || length - padding < size)
            {
                continue;
            }

            block.Free.Remove(start);
            if (padding != 0)
            {
                block.Free[start] = padding;
            }

            var tail = length - padding - size;
            if (tail != 0)
            {
                block.Free[aligned + size] = tail;
            }

            block.Used += size;
            offset = aligned;
            return true;
        }

        offset = 0;
        return false;
    }
}

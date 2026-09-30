// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Buffers;

namespace SharpEmu.Libs.Gpu;

// TEMP DIAG (SHARPEMU_DIAG_MEMORY=1): every 30 s, where the process memory goes; every
// 2 minutes, an inventory of the committed address space by type and by allocation.
internal static unsafe partial class MemoryReportDiag
{
    private static Timer? _timer;
    internal static int LiveDescriptorPools;
    private static int _ticks;

    public static void Start(GpuDeviceInfo device, Func<ulong> guestDirectBytes)
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_DIAG_MEMORY") != "1" || _timer is not null) return;
        _timer = new Timer(_ =>
        {
            using var process = Process.GetCurrentProcess();
            var gc = GC.GetGCMemoryInfo();
            Console.Error.WriteLine(
                $"[DIAG][MEMORY] private_mb={process.PrivateMemorySize64 >> 20} working_set_mb={process.WorkingSet64 >> 20} " +
                $"guest_direct_mb={guestDirectBytes() >> 20} gc_heap_mb={gc.HeapSizeBytes >> 20} gc_committed_mb={gc.TotalCommittedBytes >> 20} " +
                $"buffers_vram_mb={Interlocked.Read(ref GpuBuffer.DiagBytes[0]) >> 20} buffers_sysram_mb={Interlocked.Read(ref GpuBuffer.DiagBytes[1]) >> 20} " +
                $"buffers_vram_mapped_mb={Interlocked.Read(ref GpuBuffer.DiagBytes[2]) >> 20} images_mb={device.ImageMemoryAllocatedBytes >> 20}");
            Console.Error.WriteLine($"[DIAG][DEVICE_MEMORY] allocations={Interlocked.Read(ref GpuDeviceInfo.DiagAllocations)} frees={Interlocked.Read(ref GpuDeviceInfo.DiagFrees)} allocated_total_mb={Interlocked.Read(ref GpuDeviceInfo.DiagAllocatedBytes) >> 20} descriptor_pools={Volatile.Read(ref LiveDescriptorPools)} command_buffers={Volatile.Read(ref SharpEmu.Libs.Gpu.Scheduling.TickedBufferRing.DiagAllocatedBuffers)} {device.DiagDescribeMemoryTypes()}");
            if (OperatingSystem.IsWindows() && Interlocked.Increment(ref _ticks) % 4 == 1)
            {
                ReportAddressSpace();
            }
        }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    private const uint MemCommit = 0x1000;
    private const uint MemFree = 0x10000;
    private const uint MemImage = 0x1000000;
    private const uint MemMapped = 0x40000;
    private const uint MemPrivate = 0x20000;

    [LibraryImport("kernel32.dll")]
    private static partial nuint VirtualQuery(nint address, out MemoryBasicInformation information, nuint length);

    // Committed bytes by type, then the largest committed allocations (a region's
    // AllocationBase groups the pieces of one VirtualAlloc or one mapped view).
    private static void ReportAddressSpace()
    {
        var byType = new Dictionary<uint, ulong>();
        var byAllocation = new Dictionary<(nint Base, uint Type), (ulong Committed, ulong Reserved, uint Protect)>();
        nint address = 0;
        while (VirtualQuery(address, out var info, (nuint)sizeof(MemoryBasicInformation)) != 0)
        {
            var size = (ulong)info.RegionSize;
            if (info.State == MemCommit)
            {
                byType[info.Type] = byType.GetValueOrDefault(info.Type) + size;
            }

            if (info.State != MemFree)
            {
                var key = (info.AllocationBase, info.Type);
                var entry = byAllocation.GetValueOrDefault(key);
                byAllocation[key] = (entry.Committed + (info.State == MemCommit ? size : 0), entry.Reserved + size,
                    entry.Protect == 0 ? info.AllocationProtect : entry.Protect);
            }

            var next = (ulong)info.BaseAddress + size;
            if (next <= (ulong)address || next >= 0x7FFF_FFFF_0000) break;
            address = (nint)next;
        }

        Console.Error.WriteLine(
            $"[DIAG][ADDRESS_SPACE] committed_private_mb={byType.GetValueOrDefault(MemPrivate) >> 20} " +
            $"committed_mapped_mb={byType.GetValueOrDefault(MemMapped) >> 20} committed_image_mb={byType.GetValueOrDefault(MemImage) >> 20}");
        // Committed private allocations under 64 MiB: count and total per size bucket, and the
        // committed sizes that repeat most (a pool or a per-object allocation shows up here).
        var small = byAllocation.Where(pair => pair.Key.Type == MemPrivate && pair.Value.Committed < 64UL << 20 && pair.Value.Committed != 0).ToList();
        ulong[] limits = [1UL << 20, 4UL << 20, 16UL << 20, 64UL << 20];
        var bucketText = string.Join(' ', limits.Select((limit, index) =>
        {
            var low = index == 0 ? 0UL : limits[index - 1];
            var inBucket = small.Where(pair => pair.Value.Committed >= low && pair.Value.Committed < limit).ToList();
            return $"<{limit >> 20}MB:n={inBucket.Count},mb={inBucket.Sum(pair => (long)pair.Value.Committed) >> 20}";
        }));
        Console.Error.WriteLine($"[DIAG][ADDRESS_SPACE] small_private {bucketText}");
        foreach (var group in small.GroupBy(pair => pair.Value.Committed).OrderByDescending(group => (long)group.Key * group.Count()).Take(12))
        {
            Console.Error.WriteLine(
                $"[DIAG][ADDRESS_SPACE]   repeated committed_kb={group.Key >> 10} count={group.Count()} total_mb={((long)group.Key * group.Count()) >> 20} " +
                $"reserved_kb={group.First().Value.Reserved >> 10} protect=0x{group.First().Value.Protect:X}");
        }

        // A look inside the first few 32 MiB write-combined blocks: nonzero bytes in their first
        // MiB and their first 32 bytes, to tell resource copies from empty driver reservations.
        foreach (var ((allocationBase, _), _) in small.Where(pair => pair.Value.Committed == 32UL << 20 && pair.Value.Protect == 0x404).Take(4))
        {
            var bytes = new ReadOnlySpan<byte>((void*)allocationBase, 1 << 20);
            var nonzero = 0;
            foreach (var value in bytes) nonzero += value == 0 ? 0 : 1;
            Console.Error.WriteLine(
                $"[DIAG][ADDRESS_SPACE]   wc32 base=0x{(ulong)allocationBase:X12} nonzero_in_first_mb={nonzero} head={Convert.ToHexString(bytes[..32])}");
        }

        foreach (var ((allocationBase, type), (committed, reserved, protect)) in byAllocation
                     .Where(pair => pair.Value.Committed >= 64UL << 20)
                     .OrderByDescending(pair => pair.Value.Committed)
                     .Take(25))
        {
            var kind = type == MemPrivate ? "private" : type == MemMapped ? "mapped" : type == MemImage ? "image" : $"0x{type:X}";
            Console.Error.WriteLine(
                $"[DIAG][ADDRESS_SPACE]   base=0x{(ulong)allocationBase:X12} {kind} committed_mb={committed >> 20} reserved_mb={reserved >> 20} protect=0x{protect:X}");
        }
    }
}

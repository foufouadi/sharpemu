// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.Host.Posix;

namespace SharpEmu.Core.Cpu;

public sealed class TrackedCpuMemory : ICpuMemory, ITrackedCpuMemory, IGuestMemoryAllocator, ICpuMemoryWrapper
{
    private readonly ICpuMemory _inner;

    public TrackedCpuMemory(ICpuMemory inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public CpuMemoryAccessFailure? LastFailure { get; private set; }

    public ICpuMemory Inner => _inner;

    public string DescribeReadRange(ulong address, ulong size) => _inner.DescribeReadRange(address, size);

    // Guest pointers can name the HLE libc/mspace heaps, which live in native host memory
    // outside every guest region; UntrackedHostMemory serves those on hosts whose memory
    // table cannot see them, as Win32 VirtualQuery does on Windows.
    public bool TryRead(ulong virtualAddress, Span<byte> destination)
    {
        var result = _inner.TryRead(virtualAddress, destination) ||
            UntrackedHostMemory.TryRead(virtualAddress, destination);
        if (!result)
        {
            LastFailure = new CpuMemoryAccessFailure(virtualAddress, destination.Length, isWrite: false);
        }

        return result;
    }

    public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
    {
        var result = _inner.TryWrite(virtualAddress, source) ||
            UntrackedHostMemory.TryWrite(virtualAddress, source);
        if (!result)
        {
            LastFailure = new CpuMemoryAccessFailure(virtualAddress, source.Length, isWrite: true);
        }

        return result;
    }

    public bool TryCompare(
        ulong virtualAddress,
        ReadOnlySpan<byte> expected,
        out bool equal)
    {
        if (_inner.TryCompare(virtualAddress, expected, out equal))
        {
            return true;
        }

        Span<byte> actual = expected.Length <= 256 ? stackalloc byte[expected.Length] : new byte[expected.Length];
        if (!UntrackedHostMemory.TryRead(virtualAddress, actual))
        {
            equal = false;
            return false;
        }

        equal = actual.SequenceEqual(expected);
        return true;
    }

    public bool TryCopy(ulong destinationAddress, ulong sourceAddress, ulong length) =>
        _inner.TryCopy(destinationAddress, sourceAddress, length);

    public bool CanRead(ulong address, ulong size) => _inner.CanRead(address, size);

    public bool TryScanCString(ulong address, byte needle, bool findLast, ulong maxLength, out ulong match) =>
        _inner.TryScanCString(address, needle, findLast, maxLength, out match);

    public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address)
    {
        if (_inner is IGuestMemoryAllocator allocator)
        {
            return allocator.TryAllocateGuestMemory(size, alignment, out address);
        }

        address = 0;
        return false;
    }

    public bool TryFreeGuestMemory(ulong address)
    {
        return _inner is IGuestMemoryAllocator allocator && allocator.TryFreeGuestMemory(address);
    }
}

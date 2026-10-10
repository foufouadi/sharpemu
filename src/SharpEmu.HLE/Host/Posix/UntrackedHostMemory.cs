// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.HLE.Host.Posix;

/// <summary>
/// Copies to and from native host memory that the emulator did not map itself.
/// The POSIX host-memory table knows only the emulator's own mappings, so it
/// reports the native heap behind <c>Marshal.AllocHGlobal</c> (the HLE libc and
/// mspace heaps handed to the guest) as free, where Win32 VirtualQuery reports
/// it committed. The copy goes through the kernel, which applies the real page
/// protection and returns EFAULT instead of raising SIGSEGV.
/// </summary>
public static unsafe class UntrackedHostMemory
{
    private const ulong CanonicalUpper = 0x0000_8000_0000_0000UL;

    public static bool TryRead(ulong address, Span<byte> destination)
    {
        fixed (byte* buffer = destination)
        {
            return TryCopy(address, buffer, destination.Length, write: false);
        }
    }

    public static bool TryWrite(ulong address, ReadOnlySpan<byte> source)
    {
        fixed (byte* buffer = source)
        {
            return TryCopy(address, buffer, source.Length, write: true);
        }
    }

    private static bool TryCopy(ulong address, byte* buffer, int length, bool write)
    {
        if (!OperatingSystem.IsLinux() || length <= 0 || address == 0 ||
            address >= CanonicalUpper || CanonicalUpper - address < (ulong)length ||
            !IsUntracked(address, (ulong)length))
        {
            return false;
        }

        var local = new IoVector { Address = (nint)buffer, Length = (nuint)length };
        var remote = new IoVector { Address = (nint)address, Length = (nuint)length };
        var copied = write
            ? ProcessVmWritev(Environment.ProcessId, &local, 1, &remote, 1, 0)
            : ProcessVmReadv(Environment.ProcessId, &local, 1, &remote, 1, 0);
        return copied == length;
    }

    // Emulator-mapped memory keeps its own protection and tracking rules; only
    // ranges wholly outside it take the kernel path.
    private static bool IsUntracked(ulong address, ulong length)
    {
        var end = address + length;
        var cursor = address;
        while (cursor < end)
        {
            if (HostMemory.Query((void*)cursor, out var info) == 0 ||
                info.State != HostMemory.MEM_FREE_STATE ||
                info.RegionSize == 0)
            {
                return false;
            }

            var next = info.BaseAddress + info.RegionSize;
            if (next <= cursor)
            {
                return false;
            }

            cursor = next;
        }

        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoVector
    {
        public nint Address;
        public nuint Length;
    }

    [DllImport("libc", EntryPoint = "process_vm_readv")]
    private static extern nint ProcessVmReadv(
        int processId, IoVector* local, nuint localCount, IoVector* remote, nuint remoteCount, nuint flags);

    [DllImport("libc", EntryPoint = "process_vm_writev")]
    private static extern nint ProcessVmWritev(
        int processId, IoVector* local, nuint localCount, IoVector* remote, nuint remoteCount, nuint flags);
}

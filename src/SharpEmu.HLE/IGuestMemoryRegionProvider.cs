// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE;

[Flags]
public enum GuestMemoryProtection
{
    None = 0,
    Read = 1,
    Write = 2,
    Execute = 4,
}

public readonly record struct GuestMemoryRegion(
    ulong VirtualAddress,
    ulong MemorySize,
    GuestMemoryProtection Protection);

/// <summary>Optional guest-memory mapping view exposed to HLE exports.</summary>
public interface IGuestMemoryRegionProvider
{
    IReadOnlyList<GuestMemoryRegion> SnapshotGuestMemoryRegions();
}

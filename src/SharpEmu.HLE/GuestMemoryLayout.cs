// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE;

public static class GuestMemoryLayout
{
    // The direct memory a PS5 game gets: 12.5 GiB of the console's 16 GiB, the rest being the
    // system's. Games size their pools from sceKernelGetDirectMemorySize, so reporting the
    // whole 16 GiB made them claim (and fill) memory a console never gives them.
    // SHARPEMU_DIRECT_MEMORY_MB overrides it.
    public static readonly ulong DirectBytes =
        (ulong.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_DIRECT_MEMORY_MB"), out var megabytes) && megabytes > 0
            ? megabytes
            : 12800UL) * 1024 * 1024;
    public const ulong FlexibleBytes = 448UL * 1024 * 1024;
    // Flexible memory sits after the optional direct-memory slack so late direct
    // allocations that draw past the reported pool never overlap it.
    public static readonly ulong FlexibleOffset = DirectBytes + SlackBytes;
    // Allocator headroom beyond the report (SHARPEMU_DIRECT_MEMORY_SLACK_MB): the guest
    // still sees DirectBytes, but late heap growth can draw past it.
    public static readonly ulong SlackBytes =
        ulong.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_DIRECT_MEMORY_SLACK_MB"), out var slackMegabytes) &&
        slackMegabytes > 0 && slackMegabytes <= 8192
            ? slackMegabytes * 1024 * 1024
            : 0UL;
    public static readonly ulong BackingBytes = DirectBytes + SlackBytes + FlexibleBytes;
    public const ulong GuestPage = 0x4000;
}

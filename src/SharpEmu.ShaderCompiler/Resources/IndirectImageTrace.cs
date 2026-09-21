// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

// SHARPEMU_TRACE_INDIRECT_IMAGES=1 prints each distinct runtime descriptor table once:
// the heap, the selecting mask, the keys it yields and the descriptors they resolve to.
// A table that changes content prints again, so a per-frame selection stays visible
// without logging every dispatch.
internal static class IndirectImageTrace
{
    private const int MaxDistinctTables = 256;
    private static readonly bool Enabled =
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_INDIRECT_IMAGES") == "1";
    private static readonly HashSet<ulong> Seen = new();

    internal static void WriteWaveTable(ulong heap, uint mask, IEnumerable<uint> keys, IReadOnlyList<uint[]> descriptors)
    {
        if (!Enabled)
            return;

        var hash = new HashCode();
        hash.Add(heap);
        hash.Add(mask);
        foreach (var key in keys)
            hash.Add(key);
        foreach (var descriptor in descriptors)
            foreach (var word in descriptor)
                hash.Add(word);
        var identity = (ulong)(uint)hash.ToHashCode() | ((ulong)mask << 32);
        lock (Seen)
        {
            if (Seen.Count >= MaxDistinctTables || !Seen.Add(identity))
                return;
        }

        var entries = keys.Zip(descriptors, (key, words) =>
            $"key={key} type={GuestImageFormat.ImageTypeOf(words)} words={string.Join(':', words.Select(word => word.ToString("X8")))}");
        Console.Error.WriteLine(
            $"[GPU][TRACE][INDIRECT_IMAGE] heap=0x{heap:X} mask=0x{mask:X8} keys={descriptors.Count} {string.Join(" | ", entries)}");
    }
}

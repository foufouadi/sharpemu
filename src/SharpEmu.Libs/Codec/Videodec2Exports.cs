// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Codec;

/// <summary>
/// libSceVideodec2 (hardware compute-based decoder, distinct from the
/// software-path libSceVideodec above). Only the capability-query surface
/// needed to let callers finish decoder setup is implemented; actual
/// hardware-accelerated decode is out of scope.
/// </summary>
public static class Videodec2Exports
{
    private const int Ok = 0;

    [SysAbiExport(
        Nid = "RnDibcGCPKw",
        ExportName = "sceVideodec2QueryComputeMemoryInfo",
        Target = Generation.Gen5,
        LibraryName = "libSceVideodec2")]
    public static int Videodec2QueryComputeMemoryInfo(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        if (paramAddress == 0)
        {
            return SetReturn(ctx, VideodecErrorInvalidArg);
        }

        // Ghost of Yotei's disassembly shows the success path (return 0)
        // continues initializing the decoder object using ITS OWN existing
        // fields, not values written back into this query's param struct —
        // so a plain success with no memory writes matches observed usage.
        // On failure the caller tears the whole decoder object down and
        // falls back, so a spurious error here only disables hardware video
        // decode, it doesn't leave state half-built.
        return SetReturn(ctx, Ok);
    }

    private const int VideodecErrorInvalidArg = unchecked((int)0x80620801);

    // Yotei's movie player (thread MovieDecoder) calls this once during boot
    // (caller 0x800E20206: rdi = out queue on the stack, rsi = compute memory
    // info, rdx = compute config info; `test eax` bails the whole intro-movie
    // path on any nonzero return — after which the game's render loop waits
    // on movie frames that never come and stops submitting graphics, the
    // post-flip-18 wall). The queue is an opaque token the game hands back to
    // later Videodec2 calls; no field of it is read by the caller
    // (disassembled through +0x200 past the call site).
    private const ulong ComputeQueueToken = 0x56D2_C0DE_0001UL;

    [SysAbiExport(
        Nid = "eD+X2SmxUt4",
        ExportName = "sceVideodec2AllocateComputeQueue",
        Target = Generation.Gen5,
        LibraryName = "libSceVideodec2")]
    public static int Videodec2AllocateComputeQueue(CpuContext ctx)
    {
        var queueAddress = ctx[CpuRegister.Rdi];
        if (queueAddress == 0 || !ctx.TryWriteUInt64(queueAddress, ComputeQueueToken))
        {
            return SetReturn(ctx, VideodecErrorInvalidArg);
        }

        return SetReturn(ctx, Ok);
    }

    // Caller 0x800E202B3 (same intro-movie function as AllocateComputeQueue):
    // after this returns 0, the game reads a size at out+0x08, arena-allocates
    // that many bytes and stores the pointer at out+0x10, then repeats with a
    // size at out+0x28 into out+0x30 — and a size of ZERO skips its
    // allocation cleanly (`test rdx,rdx; je next-block`) while garbage would
    // demand a giant allocation and bail the whole movie path. Zeroing both
    // size fields is therefore the safe no-decode stub.
    [SysAbiExport(
        Nid = "qqMCwlULR+E",
        ExportName = "sceVideodec2QueryDecoderMemoryInfo",
        Target = Generation.Gen5,
        LibraryName = "libSceVideodec2")]
    public static int Videodec2QueryDecoderMemoryInfo(CpuContext ctx)
    {
        var memoryInfoAddress = ctx[CpuRegister.Rsi];
        if (memoryInfoAddress == 0 ||
            !ctx.TryWriteUInt64(memoryInfoAddress + 0x08, 0) ||
            !ctx.TryWriteUInt64(memoryInfoAddress + 0x28, 0) ||
            // Frame-slot size. 0x800E1C740 divides its arena's remaining space
            // by this value (align-up, `div r15d` — zero crashed MovieDecoder
            // with 0xC0000094) and bails below a quotient of 4, then carves
            // that many slots out of the game's own movie arena. A small
            // nonzero size keeps the quotient comfortably above 4 while
            // consuming almost nothing; the slots are never filled because
            // decode itself stays stubbed.
            !ctx.TryWriteUInt64(memoryInfoAddress + 0x38, 0x1000))
        {
            return SetReturn(ctx, VideodecErrorInvalidArg);
        }

        return SetReturn(ctx, Ok);
    }

    private const ulong DecoderToken = 0x56D2_C0DE_0002UL;

    // Caller 0x800E20425: CreateDecoder(config=rdi, memoryInfo=rsi,
    // out decoder=rdx). The handle is opaque to the game — the very next use
    // (0x800E20457) loads it back only to pass as rdi to the next Videodec2
    // import.
    [SysAbiExport(
        Nid = "CNNRoRYd8XI",
        ExportName = "sceVideodec2CreateDecoder",
        Target = Generation.Gen5,
        LibraryName = "libSceVideodec2")]
    public static int Videodec2CreateDecoder(CpuContext ctx)
    {
        var decoderAddress = ctx[CpuRegister.Rdx];
        if (decoderAddress == 0 || !ctx.TryWriteUInt64(decoderAddress, DecoderToken))
        {
            return SetReturn(ctx, VideodecErrorInvalidArg);
        }

        DumpDebugStruct(ctx, "CreateDecoder config", ctx[CpuRegister.Rdi], 0x80);
        DumpDebugStruct(ctx, "CreateDecoder memInfo", ctx[CpuRegister.Rsi], 0x60);
        return SetReturn(ctx, Ok);
    }

    private static readonly bool DebugDumpEnabled =
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_VIDEODEC2_DEBUG_DUMP"),
            "1",
            StringComparison.Ordinal);

    private static void DumpDebugStruct(CpuContext ctx, string label, ulong address, int length)
    {
        if (!DebugDumpEnabled || address == 0)
        {
            return;
        }

        Span<byte> buffer = stackalloc byte[length];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            Console.Error.WriteLine($"[VIDEODEC2][DEBUG] {label} @0x{address:x}: <unreadable>");
            return;
        }

        Console.Error.WriteLine($"[VIDEODEC2][DEBUG] {label} @0x{address:x}: {Convert.ToHexString(buffer)}");
    }

    // End-of-stream drain, call site 0x800E20124: Flush(decoder=rdi,
    // out1=rsi, out2=rdx). Return 0 with the picture-ready byte at [rdx]
    // cleared says "no buffered pictures remain", which sends the player to
    // its termination path (0x800e20016 — the same label its error checks
    // use doubles as the movie-finished exit); a 1 would publish one more
    // frame and loop back into Flush.
    [SysAbiExport(
        Nid = "l1hXwscLuCY",
        ExportName = "sceVideodec2Flush",
        Target = Generation.Gen5,
        LibraryName = "libSceVideodec2")]
    public static int Videodec2Flush(CpuContext ctx)
    {
        var outputInfoAddress = ctx[CpuRegister.Rdx];
        ReadOnlySpan<byte> noPicture = [0];
        if (outputInfoAddress == 0 ||
            !ctx.Memory.TryWrite(outputInfoAddress, noPicture))
        {
            return SetReturn(ctx, VideodecErrorInvalidArg);
        }

        return SetReturn(ctx, Ok);
    }

    // Called at 0x800E2045E right after CreateDecoder with the decoder token
    // in rdi; the stubbed decoder has no state to reset. Its generic-error
    // failure previously routed the player straight into DeleteDecoder.
    [SysAbiExport(
        Nid = "wJXikG6QFN8",
        ExportName = "sceVideodec2Reset",
        Target = Generation.Gen5,
        LibraryName = "libSceVideodec2")]
    public static int Videodec2Reset(CpuContext ctx)
    {
        return SetReturn(ctx, Ok);
    }

    [SysAbiExport(
        Nid = "jwImxXRGSKA",
        ExportName = "sceVideodec2DeleteDecoder",
        Target = Generation.Gen5,
        LibraryName = "libSceVideodec2")]
    public static int Videodec2DeleteDecoder(CpuContext ctx)
    {
        return SetReturn(ctx, Ok);
    }

    // Decode loop call site 0x800E1FEB0: Decode(decoder=rdi, input=rsi,
    // out1=rdx, out2=rcx). On return 0 the game reads ONE BYTE at [rcx] — the
    // picture-ready flag: anything but 1 just loops to feed the next access
    // unit ("no output yet"), 1 makes it read dimensions from rcx+8/rcx+0x10
    // and publish a frame. The flag byte lives in uninitialized stack, so it
    // MUST be written 0 explicitly (a stale 1 would publish a garbage frame).
    // Exactly one byte — the notice-screen canary smash came from widening
    // exactly this kind of write.
    [SysAbiExport(
        Nid = "852F5+q6+iM",
        ExportName = "sceVideodec2Decode",
        Target = Generation.Gen5,
        LibraryName = "libSceVideodec2")]
    public static int Videodec2Decode(CpuContext ctx)
    {
        if (DebugDumpEnabled)
        {
            var outputSlotObj = ctx[CpuRegister.Rsi];
            var inputAuStruct = ctx[CpuRegister.Rdx];
            DumpDebugStruct(ctx, "Decode outputSlotObj", outputSlotObj, 0x18);
            DumpDebugStruct(ctx, "Decode inputAuStruct", inputAuStruct, 0x18);
            if (inputAuStruct != 0 &&
                ctx.TryReadUInt64(inputAuStruct + 0x08, out var auDataPtr) &&
                ctx.TryReadUInt64(inputAuStruct + 0x10, out var auDataSize))
            {
                Console.Error.WriteLine(
                    $"[VIDEODEC2][DEBUG] Decode AU data ptr=0x{auDataPtr:x} size={auDataSize}");
                DumpDebugStruct(ctx, "Decode AU bytes", auDataPtr, (int)Math.Min(auDataSize, 64UL));
            }

            if (outputSlotObj != 0 &&
                ctx.TryReadUInt64(outputSlotObj, out var slotPtr) &&
                ctx.TryReadUInt64(outputSlotObj + 0x08, out var slotSize))
            {
                Console.Error.WriteLine(
                    $"[VIDEODEC2][DEBUG] Decode output slot ptr=0x{slotPtr:x} size={slotSize}");
            }
        }

        var outputInfoAddress = ctx[CpuRegister.Rcx];
        ReadOnlySpan<byte> noPicture = [0];
        if (outputInfoAddress == 0 ||
            !ctx.Memory.TryWrite(outputInfoAddress, noPicture))
        {
            return SetReturn(ctx, VideodecErrorInvalidArg);
        }

        return SetReturn(ctx, Ok);
    }

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }
}

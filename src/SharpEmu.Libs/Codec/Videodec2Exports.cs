// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Codec;

/// <summary>
/// libSceVideodec2 (hardware compute-based decoder, distinct from the
/// software-path libSceVideodec above). The capability-query surface keeps
/// the decoder lifecycle resolvable regardless of whether real decode is
/// available; sceVideodec2Decode itself now feeds a real FFmpeg H.264
/// session (Videodec2Decoder) when one could be opened, falling back to the
/// original "no picture" stub if FFmpeg's native libraries are missing or a
/// given decoder failed to open -- see docs/ffmpeg-videodec2-groundwork.md.        
/// </summary>
public static class Videodec2Exports
{
    private const int Ok = 0;

    // Real decoder instance per opaque handle, or null when TryCreate()
    // failed (FFmpeg unavailable, or this particular open failed) -- a null
    // entry still occupies a valid handle so every other export's lifecycle
    // bookkeeping (Decode/Flush/Reset/DeleteDecoder all keying off the same
    // handle) doesn't need a separate "is this a real decoder" branch; it
    // just falls back to the pre-existing stub behavior per call.
    private static readonly ConcurrentDictionary<ulong, Videodec2Decoder?> Decoders = new();
    private static long _nextDecoderHandle = unchecked((long)DecoderToken);

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

    // Sanity bounds for the AU/output-slot size fields read out of guest
    // memory before allocating buffers of that size: a real H.264 access
    // unit is normally well under 1 MB even for a keyframe, and even an 8K
    // NV12 frame (7680x4320x1.5) is ~50 MB, so both ceilings have generous
    // headroom while still catching garbage/not-yet-primed struct reads
    // (observed in practice) before they reach `new byte[...]` and throw.
    private const ulong MaxPlausibleAuBytes = 32UL * 1024 * 1024;
    private const ulong MaxPlausibleSlotBytes = 64UL * 1024 * 1024;

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
    // import. Real callers get a fresh handle per call (monotonic counter
    // seeded at the old fixed token, so it stays in the same address range
    // callers have always seen); TryCreate() failing just means Decode
    // falls back to the original stub for this specific handle.
    [SysAbiExport(
        Nid = "CNNRoRYd8XI",
        ExportName = "sceVideodec2CreateDecoder",
        Target = Generation.Gen5,
        LibraryName = "libSceVideodec2")]
    public static int Videodec2CreateDecoder(CpuContext ctx)
    {
        var decoderAddress = ctx[CpuRegister.Rdx];
        if (decoderAddress == 0)
        {
            return SetReturn(ctx, VideodecErrorInvalidArg);
        }

        var handle = unchecked((ulong)Interlocked.Increment(ref _nextDecoderHandle));
        Decoders[handle] = Videodec2Decoder.TryCreate();

        if (!ctx.TryWriteUInt64(decoderAddress, handle))
        {
            Decoders.TryRemove(handle, out var created);
            created?.Dispose();
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
        var handle = ctx[CpuRegister.Rdi];
        var outputInfoAddress = ctx[CpuRegister.Rdx];
        if (outputInfoAddress == 0 || !ctx.Memory.TryWrite(outputInfoAddress, NoPicture))
        {
            return SetReturn(ctx, VideodecErrorInvalidArg);
        }

        if (Decoders.TryGetValue(handle, out var decoder) && decoder is not null)
        {
            // Report a frame the worker already finished first; only once
            // that's caught up do we queue a fresh drain request, so a game
            // that calls Flush in a loop (per this export's own doc
            // comment) drains strictly in order. Actual pixels go through
            // Videodec2Decoder's own scheduler thread -- see that class's
            // doc comment for why Flush/Decode never call
            // VulkanVideoPresenter.Submit directly anymore.
            if (decoder.TryConsumeProtocolReadySignal(out var width, out var height))
            {
                if (ctx.TryWriteUInt64(outputInfoAddress + 0x08, width) &&
                    ctx.TryWriteUInt64(outputInfoAddress + 0x10, height))
                {
                    _ = ctx.Memory.TryWrite(outputInfoAddress, PictureReady);
                }
            }
            else
            {
                decoder.RequestDrain();
            }
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
        var handle = ctx[CpuRegister.Rdi];
        if (Decoders.TryRemove(handle, out var decoder))
        {
            decoder?.Dispose();
        }

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
    //
    // rsi/rdx roles were originally guessed backwards (rsi=output,
    // rdx=input) and swapped after live evidence proved it wrong: rsi's
    // struct is populated call-to-call by the game's own NAL demuxer
    // (0x800E25520, confirmed by static disassembly to scan for Annex-B
    // start codes and write {tag=0x30, ptr, len} entries into a table at
    // [decoder_state+0x28], stride 0x30 -- exactly the stride Decode's rsi
    // argument walks one entry per call) with a genuinely varying
    // pointer/size (e.g. 0x1010c9d2c0/249250, 0x1010cda062/13157); a live
    // dump of the bytes at that pointer showed real Annex-B data (`00 00 00
    // 01 06 05 FF FF FF 1F ...`, start code + SEI/slice NAL headers). rdx's
    // fields, by contrast, are a single fixed address (0x2020cb6500) and
    // size (4096) on every call, always zero -- an unwritten scratch/output
    // buffer, not an input. So: rsi's fields (+0x08=AU data pointer,
    // +0x10=AU byte size) are the Annex-B access unit already demuxed by
    // the game; rdx's fields (+0x08=destination pointer, +0x10=destination
    // byte capacity) are the buffer this export decodes NV12 pixels into
    // when a real decoder is attached.
    [SysAbiExport(
        Nid = "852F5+q6+iM",
        ExportName = "sceVideodec2Decode",
        Target = Generation.Gen5,
        LibraryName = "libSceVideodec2")]
    public static int Videodec2Decode(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var inputAuStruct = ctx[CpuRegister.Rsi];
        var outputSlotObj = ctx[CpuRegister.Rdx];
        var outputInfoAddress = ctx[CpuRegister.Rcx];

        if (outputInfoAddress == 0 || !ctx.Memory.TryWrite(outputInfoAddress, NoPicture))
        {
            return SetReturn(ctx, VideodecErrorInvalidArg);
        }

        if (!Decoders.TryGetValue(handle, out var decoder) || decoder is null)
        {
            // No real decoder for this handle (FFmpeg unavailable, or this
            // CreateDecoder call failed to open one) -- pre-existing stub
            // behavior: "fed the AU, no picture", never fatal.
            return SetReturn(ctx, Ok);
        }

        if (inputAuStruct == 0 ||
            // +0x00 is a constant tag (observed 0x30 on every call, written
            // by the game's own NAL demuxer alongside these fields -- see
            // this export's doc comment); never consumed here.
            !ctx.TryReadUInt64(inputAuStruct + 0x08, out var auDataPtr) ||
            !ctx.TryReadUInt64(inputAuStruct + 0x10, out var auDataSize) ||
            auDataPtr == 0 || auDataSize == 0 || auDataSize > MaxPlausibleAuBytes ||
            outputSlotObj == 0 ||
            !ctx.TryReadUInt64(outputSlotObj + 0x08, out var slotPtr) ||
            !ctx.TryReadUInt64(outputSlotObj + 0x10, out var slotSize) ||
            slotPtr == 0 || slotSize == 0 || slotSize > MaxPlausibleSlotBytes)
        {
            // No AU this call (e.g. a flush-shaped invocation), no
            // destination slot, or a size field outside plausible bounds --
            // not an error, just nothing sane to feed/fill this call, same
            // as the AU==0 case above.
            return SetReturn(ctx, Ok);
        }

        var auBuffer = new byte[auDataSize];
        if (!ctx.Memory.TryRead(auDataPtr, auBuffer))
        {
            return SetReturn(ctx, Ok);
        }

        // Decode runs on Videodec2Decoder's own worker/scheduler pipeline,
        // never on this (guest) thread -- see that class's doc comment for
        // the full design and why an earlier, decode-worker-only attempt
        // broke playback (nothing paced frames to real time). This just
        // queues the AU (sub-millisecond) and reports whatever frame the
        // pipeline already finished from an EARLIER call (not necessarily
        // this AU's own result -- H.264 already has multi-frame reordering
        // delay the game's own loop already tolerates, per this export's
        // own doc comment on rsi/rdx roles) via TryConsumeProtocolReadySignal.
        // Actual pixels reach the screen through the decoder's own scheduler
        // thread on its own paced timer, not through this call at all.
        decoder.EnqueueAccessUnit(auBuffer);

        if (!decoder.TryConsumeProtocolReadySignal(out var width, out var height))
        {
            return SetReturn(ctx, Ok);
        }

        if (!ctx.TryWriteUInt64(outputInfoAddress + 0x08, width) ||
            !ctx.TryWriteUInt64(outputInfoAddress + 0x10, height) ||
            !ctx.Memory.TryWrite(outputInfoAddress, PictureReady))
        {
            return SetReturn(ctx, Ok);
        }

        return SetReturn(ctx, Ok);
    }

    private static readonly byte[] NoPicture = [0];
    private static readonly byte[] PictureReady = [1];

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }
}

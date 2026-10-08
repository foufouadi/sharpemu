// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

// Ghost of Yotei calls Push/PopMarker from a second guest thread on a DCB the main thread
// is filling, so by default the markers must not append to the buffer.
[Collection(AgcCommandBufferChainCollection.Name)]
public sealed class AgcMarkerTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const int MemorySize = 0x4000;
    private const ulong CommandBufferAddress = BaseAddress + 0x100;
    private const ulong CursorAddress = BaseAddress + 0x1000;
    private const ulong MarkerStringAddress = BaseAddress + 0x200;
    private const uint PoisonDword = 0xDEAD_BEEFu;

    [Fact]
    public void PushMarker_ByDefault_LeavesBufferAndCursorUntouched()
    {
        var (memory, ctx) = Setup();
        var before = Snapshot(memory);
        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = MarkerStringAddress;

        RunWithOverride(false, () => Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.DcbPushMarker(ctx)));

        Assert.Equal(CursorAddress, ctx[CpuRegister.Rax]);
        Assert.Equal(before, Snapshot(memory));
    }

    [Fact]
    public void PopMarker_ByDefault_LeavesBufferAndCursorUntouched()
    {
        var (memory, ctx) = Setup();
        var before = Snapshot(memory);
        ctx[CpuRegister.Rdi] = CommandBufferAddress;

        RunWithOverride(false, () => Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.AcbPopMarker(ctx)));

        Assert.Equal(CursorAddress, ctx[CpuRegister.Rax]);
        Assert.Equal(before, Snapshot(memory));
    }

    [Fact]
    public void Markers_ByDefault_RejectNullCommandBuffer()
    {
        var (_, ctx) = Setup();
        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = MarkerStringAddress;

        RunWithOverride(false, () =>
        {
            AgcExports.DcbPushMarker(ctx);
            Assert.Equal(0UL, ctx[CpuRegister.Rax]);
            AgcExports.DcbPopMarker(ctx);
            Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        });
    }

    [Fact]
    public void PushMarker_ByDefault_StillFailsOnUnreadableString()
    {
        var (_, ctx) = Setup();
        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = 0x10; // unmapped

        RunWithOverride(false, () =>
        {
            AgcExports.DcbPushMarker(ctx);
            Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        });
    }

    [Fact]
    public void Markers_WhenEnabled_WriteNopPackets()
    {
        var (memory, ctx) = Setup();
        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = MarkerStringAddress;

        RunWithOverride(true, () =>
        {
            AgcExports.DcbPushMarker(ctx);
            Assert.Equal(CursorAddress, ctx[CpuRegister.Rax]);
            // "pass" + terminator = 2 payload dwords, plus the header.
            Assert.Equal(0xC001_102Cu, ReadUInt32(memory, CursorAddress));
            Assert.Equal(CursorAddress + 12, ReadUInt64(memory, CommandBufferAddress + 0x10));

            AgcExports.DcbPopMarker(ctx);
            Assert.Equal(CursorAddress + 12, ctx[CpuRegister.Rax]);
            Assert.Equal(0xC000_1030u, ReadUInt32(memory, CursorAddress + 12));
            Assert.Equal(CursorAddress + 20, ReadUInt64(memory, CommandBufferAddress + 0x10));
        });
    }

    private static (FakeCpuMemory Memory, CpuContext Ctx) Setup()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteUInt64(memory, CommandBufferAddress + 0x10, CursorAddress);
        WriteUInt64(memory, CommandBufferAddress + 0x18, CursorAddress + 0x400);
        memory.WriteCString(MarkerStringAddress, "pass");
        for (ulong offset = 0; offset < 0x40; offset += 4)
        {
            WriteUInt32(memory, CursorAddress + offset, PoisonDword);
        }

        return (memory, ctx);
    }

    private static void RunWithOverride(bool enabled, Action body)
    {
        var previous = AgcExports.MarkerPacketsOverride;
        AgcExports.MarkerPacketsOverride = enabled;
        try
        {
            body();
        }
        finally
        {
            AgcExports.MarkerPacketsOverride = previous;
        }
    }

    private static byte[] Snapshot(FakeCpuMemory memory)
    {
        var buffer = new byte[MemorySize];
        Assert.True(memory.TryRead(BaseAddress, buffer));
        return buffer;
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[4];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[8];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }
}

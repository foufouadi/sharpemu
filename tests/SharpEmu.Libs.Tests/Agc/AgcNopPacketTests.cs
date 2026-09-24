// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcNopPacketTests
{
    private const ulong MemoryAddress = 0x2_1000_0000;
    private const ulong CommandBufferAddress = MemoryAddress + 0x80;
    private const ulong PacketAddress = MemoryAddress + 0x200;

    [Theory]
    [InlineData(1u, PacketHeader.HeaderOnlyNop)]
    [InlineData(2u, 0xC000_1000u)]
    [InlineData(4u, 0xC002_1000u)]
    public void CbNop_WritesAPacketOfTheRequestedLength(uint dwords, uint expectedHeader)
    {
        var (memory, context) = CreateCommandBuffer();
        context[CpuRegister.Rsi] = dwords;

        AgcExports.CbNop(context);

        Assert.Equal(PacketAddress, context[CpuRegister.Rax]);
        var header = ReadDword(memory, PacketAddress);
        Assert.Equal(expectedHeader, header);
        Assert.Equal(dwords, PacketHeader.Length(header));
        Assert.Equal(PacketAddress + (dwords * sizeof(uint)), ReadQword(memory, CommandBufferAddress + 0x10));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0x4001u)]
    public void CbNop_RejectsLengthsWithoutAnEncoding(uint dwords)
    {
        var (_, context) = CreateCommandBuffer();
        context[CpuRegister.Rsi] = dwords;

        AgcExports.CbNop(context);

        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    private static (FakeCpuMemory Memory, CpuContext Context) CreateCommandBuffer()
    {
        var memory = new FakeCpuMemory(MemoryAddress, 0x20000);
        var context = new CpuContext(memory, Generation.Gen5);
        WriteQword(memory, CommandBufferAddress + 0x10, PacketAddress);
        WriteQword(memory, CommandBufferAddress + 0x18, MemoryAddress + 0x20000);
        context[CpuRegister.Rdi] = CommandBufferAddress;
        return (memory, context);
    }

    private static uint ReadDword(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[4];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static ulong ReadQword(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[8];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private static void WriteQword(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }
}

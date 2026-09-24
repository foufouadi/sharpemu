// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

// DS_MIN_F32 / DS_MAX_F32 compare DATA0 with memory and store the winner; DATA1 is not read.
public sealed class Gen5DataShareFloatMinMaxTests
{
    public const uint Stored = 0x4000_0000;    // 2.0
    public const uint Smaller = 0x3F80_0000;   // 1.0
    public const uint Larger = 0x4040_0000;    // 3.0
    public const uint Decoy = 0xC2C8_0000;     // -100.0, held in the register DATA1 names

    public static TheoryData<bool, bool, uint, uint> Cases => new()
    {
        { false, false, Smaller, Smaller },
        { false, false, Larger, Stored },
        { false, true, Smaller, Stored },
        { false, true, Larger, Larger },
        { true, false, Smaller, Smaller },
        { true, false, Larger, Stored },
        { true, true, Smaller, Stored },
        { true, true, Larger, Larger },
    };

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void DecodeReadsOneDataOperand(bool max, bool global)
    {
        var instruction = DecodeMinMax(max, global);
        Assert.Equal(max ? "DsMaxF32" : "DsMinF32", instruction.Opcode);
        Assert.Equal([Gen5Operand.Vector(0), Gen5Operand.Vector(1)], instruction.Sources);
        Assert.Empty(instruction.Destinations);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void SpirvBackendCompilesTheSingleOperandForm(bool max, bool global)
    {
        var (plan, resources, layout) = Prepare(CreateReadbackProgram(max, global));
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var error), error);
    }

    // v0 = LDS address, v1 = DATA0, v2 = DATA1 decoy; memory starts at Stored and v6 reads it back.
    public static Gen5ShaderProgram CreateReadbackProgram(bool max, bool global) => Program(
        MoveVectorFromScalar(0, 0, 8), MoveVectorFromScalar(4, 1, 9), MoveVector(8, 2, Decoy), MoveVector(12, 3, Stored),
        DataShare(16, "DsWriteB32", global, [Gen5Operand.Vector(0), Gen5Operand.Vector(3)], []),
        DecodeMinMax(max, global) with { Pc = 24 },
        DataShare(32, "DsReadB32", global, [Gen5Operand.Vector(0)], [6]),
        BufferAccess(40, "BufferStoreDword", 4, vectorData: 6), EndProgram(48));

    // DS_MIN_F32 (0x12) / DS_MAX_F32 (0x13) with ADDR=v0, DATA0=v1, DATA1=v2.
    private static Gen5ShaderInstruction DecodeMinMax(bool max, bool global)
    {
        uint[] words = [0xD800_0000u | ((max ? 0x13u : 0x12u) << 18) | (global ? 1u << 17 : 0u), 0x0002_0100u, 0xBF81_0000u];
        var bytes = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)), words[index]);
        var context = new CpuContext(new ShaderMemory(bytes), Generation.Gen5);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(context, 0x1000, out var program, out var error), error);
        return program.Instructions[0];
    }

    private sealed class ShaderMemory(byte[] bytes) : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address < 0x1000 || destination.Length > bytes.Length ||
                address - 0x1000 > (ulong)(bytes.Length - destination.Length)) return false;
            bytes.AsSpan((int)(address - 0x1000), destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}

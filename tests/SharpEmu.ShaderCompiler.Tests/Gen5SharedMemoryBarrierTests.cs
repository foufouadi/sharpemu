// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5SharedMemoryBarrierTests
{
    [Fact]
    public void Wave64BarrierAcrossDivergentBlocksUsesUniformDispatcher()
    {
        var instructions = new List<Gen5ShaderInstruction>
        {
            new(0, Gen5ShaderEncoding.Vop3, "VReadlaneB32", [0u, 0u],
                [Gen5Operand.Vector(0), new Gen5Operand(Gen5OperandKind.LiteralConstant, 0), Gen5Operand.Scalar(0)],
                [Gen5Operand.Scalar(4)], new Gen5Vop3Control(0, 0, 0, false, 0, null)),
            new(8, Gen5ShaderEncoding.Sopp, "SCbranchExecz", [1u], [], [], null),
            new(12, Gen5ShaderEncoding.Sopp, "SNop", [0u], [], [], null),
            new(16, Gen5ShaderEncoding.Sopp, "SEndpgm", [0u], [], [], null),
        };
        var (plan, resources, layout) = ResourceTestProgram.Prepare(
            new Gen5ShaderProgram(0, instructions), userDataCount: 0);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = 64,
            LocalSizeX = 64,
        };

        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var opcodes = ReadOpcodes(shader.Spirv);
        Assert.Contains((ushort)SpirvOp.ControlBarrier, opcodes);
        Assert.DoesNotContain((ushort)SpirvOp.Switch, opcodes);
    }

    [Theory]
    [InlineData(64u, 64u, false, 2)]
    [InlineData(64u, 64u, true, 2)]
    [InlineData(32u, 64u, false, 0)]
    [InlineData(64u, 128u, false, 0)]
    public void SharedMemoryPhases_SynchronizeOnlySingleWaveGroups(
        uint waveSize, uint threadCount, bool explicitBarrier, int expectedBarriers)
    {
        var instructions = new List<Gen5ShaderInstruction>
        {
            new(0, Gen5ShaderEncoding.Ds, "DsWriteB32", [0u, 0u],
                [Gen5Operand.Vector(0), Gen5Operand.Vector(1)], [], new Gen5DataShareControl(0, 0, false)),
        };
        if (explicitBarrier)
            instructions.Add(new(8, Gen5ShaderEncoding.Sopp, "SBarrier", [0u], [], [], null));
        instructions.Add(new(12, Gen5ShaderEncoding.Ds, "DsReadB32", [0u, 0u],
            [Gen5Operand.Vector(0)], [Gen5Operand.Vector(2)], new Gen5DataShareControl(0, 0, false)));
        instructions.Add(new(20, Gen5ShaderEncoding.Sopp, "SEndpgm", [0u], [], [], null));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(new Gen5ShaderProgram(0, instructions), userDataCount: 0);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = waveSize,
            LocalSizeX = threadCount,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var barriers = 0;
        var sharedPointerTypes = new HashSet<uint>();
        var sharedPointers = new HashSet<uint>();
        var sharedReads = 0;
        for (var offset = 20; offset < shader.Spirv.Length;)
        {
            var instruction = BinaryPrimitives.ReadUInt32LittleEndian(shader.Spirv.AsSpan(offset));
            uint Operand(int index) => BinaryPrimitives.ReadUInt32LittleEndian(shader.Spirv.AsSpan(offset + index * 4));
            if ((instruction & 0xFFFF) == (uint)SpirvOp.TypePointer && Operand(2) == 4)
                sharedPointerTypes.Add(Operand(1));
            if ((instruction & 0xFFFF) == (uint)SpirvOp.AccessChain && sharedPointerTypes.Contains(Operand(1)))
                sharedPointers.Add(Operand(2));
            if ((instruction & 0xFFFF) == (uint)SpirvOp.Load && sharedPointers.Contains(Operand(3)))
                sharedReads++;
            if ((instruction & 0xFFFF) == (uint)SpirvOp.ControlBarrier) barriers++;
            offset += checked((int)(instruction >> 16) * 4);
        }
        Assert.Equal(expectedBarriers, barriers);
        Assert.Equal(1, sharedReads);
    }

    private static IReadOnlyList<ushort> ReadOpcodes(byte[] spirv)
    {
        var opcodes = new List<ushort>();
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var instruction = BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(offset));
            opcodes.Add((ushort)instruction);
            offset += checked((int)(instruction >> 16) * sizeof(uint));
        }

        return opcodes;
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using System.Buffers.Binary;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

// The gfx10 f16 unary VALU family, encoded as VOP1: one dword laid out as
// 0111111 | vdst[8] | opcode[8] | src0[9], where a VGPR reads as 256 + n.
public sealed class Gen5Float16Vop1Tests
{
    private const ulong ShaderAddress = 0x1000;
    private const uint SEndpgm = 0xBF810000;

    private static uint Vop1Word(uint opcode, uint vdst, uint src0) =>
        0x7E00_0000u | (vdst << 17) | (opcode << 9) | src0;

    private static uint Vgpr(uint index) => 256u + index;

    [Theory]
    [InlineData(0x50u, "VCvtF16U16")]
    [InlineData(0x51u, "VCvtF16I16")]
    [InlineData(0x52u, "VCvtU16F16")]
    [InlineData(0x53u, "VCvtI16F16")]
    [InlineData(0x54u, "VRcpF16")]
    [InlineData(0x55u, "VSqrtF16")]
    [InlineData(0x56u, "VRsqF16")]
    [InlineData(0x57u, "VLogF16")]
    [InlineData(0x58u, "VExpF16")]
    [InlineData(0x5Bu, "VFloorF16")]
    [InlineData(0x5Cu, "VCeilF16")]
    [InlineData(0x5Du, "VTruncF16")]
    [InlineData(0x5Eu, "VRndneF16")]
    [InlineData(0x60u, "VSinF16")]
    [InlineData(0x61u, "VCosF16")]
    public void Float16Vop1OpcodesDecodeAndCompile(uint opcode, string expected)
    {
        var program = Decode([Vop1Word(opcode, vdst: 3, src0: Vgpr(1))]);

        Assert.Equal(expected, program.Instructions[0].Opcode);
        Assert.True(TryCompile(program, out var error), error);
    }

    [Fact]
    public void RsqF16CompilesInsteadOfFailingTheWholeShader()
    {
        // Silent Hill: The Short Message composites through two pixel shaders
        // that use v_rsq_f16. Before this opcode was decoded, both reported
        // "unknown-vop1 op=0x56" and never reached SPIR-V at all, so every
        // draw using them produced nothing.
        var program = Decode(
        [
            Vop1Word(0x56u, vdst: 3, src0: Vgpr(1)),
            Vop1Word(0x01u, vdst: 4, src0: Vgpr(3)),
        ]);

        Assert.Equal("VRsqF16", program.Instructions[0].Opcode);
        Assert.Equal("VMovB32", program.Instructions[1].Opcode);
        Assert.True(TryCompile(program, out var error), error);
    }

    [Fact]
    public void UnassignedOpcodeInTheBlockStillReportsItself()
    {
        // 0x59, 0x5A and 0x5F are not part of the family this decodes. They
        // must keep naming themselves rather than silently decoding as a
        // neighbour.
        var memory = new TestCpuMemory(ShaderAddress, 0x100);
        Span<byte> shader = stackalloc byte[2 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(
            shader,
            Vop1Word(0x59u, vdst: 3, src0: Vgpr(1)));
        BinaryPrimitives.WriteUInt32LittleEndian(shader[sizeof(uint)..], SEndpgm);
        Assert.True(memory.TryWrite(ShaderAddress, shader));

        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.False(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                ShaderAddress,
                out _,
                out var decodeError));
        Assert.Contains("unknown-vop1 op=0x59", decodeError, StringComparison.Ordinal);
    }

    private static bool TryCompile(Gen5ShaderProgram program, out string error)
    {
        var scalarRegisters = new uint[256];
        return Gen5SpirvTranslator.TryCompileComputeShader(
            new Gen5ShaderState(program, new uint[10], null),
            new Gen5ShaderEvaluation(scalarRegisters, scalarRegisters, [], []),
            1,
            1,
            1,
            out _,
            out error);
    }

    private static Gen5ShaderProgram Decode(uint[] words)
    {
        var memory = new TestCpuMemory(ShaderAddress, 0x100);
        Span<byte> shader = stackalloc byte[(words.Length + 1) * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                shader[(index * sizeof(uint))..],
                words[index]);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(
            shader[(words.Length * sizeof(uint))..],
            SEndpgm);
        Assert.True(memory.TryWrite(ShaderAddress, shader));

        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                ShaderAddress,
                out var program,
                out var decodeError),
            decodeError);
        return program;
    }

    private sealed class TestCpuMemory(ulong baseAddress, int size) : ICpuMemory
    {
        private readonly byte[] _bytes = new byte[size];

        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address < baseAddress ||
                address + (ulong)destination.Length > baseAddress + (ulong)size)
            {
                return false;
            }

            _bytes.AsSpan((int)(address - baseAddress), destination.Length)
                .CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source)
        {
            if (address < baseAddress ||
                address + (ulong)source.Length > baseAddress + (ulong)size)
            {
                return false;
            }

            source.CopyTo(_bytes.AsSpan((int)(address - baseAddress), source.Length));
            return true;
        }
    }
}

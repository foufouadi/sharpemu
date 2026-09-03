// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using System.Buffers.Binary;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

// The gfx10 16-bit integer VALU family. VOP3 is two dwords:
// word0 = 0xD0000000 | opcode << 16 | vdst, word1 = src0 | src1 << 9,
// and a VGPR reads as 256 + n in an operand field.
public sealed class Gen5Integer16VectorTests
{
    private const ulong ShaderAddress = 0x1000;
    private const uint SEndpgm = 0xBF810000;

    private static uint Vop3Word0(uint opcode, uint vdst) =>
        0xD000_0000u | (opcode << 16) | vdst;

    private static uint Vop3Word1(uint src0, uint src1) =>
        src0 | (src1 << 9);

    private static uint Vgpr(uint index) => 256u + index;

    [Theory]
    [InlineData(0x303u, "VAddNcU16")]
    [InlineData(0x304u, "VSubNcU16")]
    [InlineData(0x307u, "VLshrrevB16")]
    [InlineData(0x308u, "VAshrrevI16")]
    [InlineData(0x309u, "VMaxU16")]
    [InlineData(0x30Au, "VMaxI16")]
    [InlineData(0x30Bu, "VMinU16")]
    [InlineData(0x30Cu, "VMinI16")]
    [InlineData(0x30Du, "VAddNcI16")]
    [InlineData(0x30Eu, "VSubNcI16")]
    [InlineData(0x314u, "VLshlrevB16")]
    public void Integer16OpcodesDecodeAndCompile(uint opcode, string expected)
    {
        var program = Decode(
        [
            Vop3Word0(opcode, vdst: 3),
            Vop3Word1(Vgpr(1), Vgpr(2)),
        ]);

        Assert.Equal(expected, program.Instructions[0].Opcode);

        var scalarRegisters = new uint[256];
        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                new Gen5ShaderState(program, new uint[10], null),
                new Gen5ShaderEvaluation(scalarRegisters, scalarRegisters, [], []),
                1,
                1,
                1,
                out _,
                out var error),
            error);
    }

    [Fact]
    public void Integer16OpcodeIsNoLongerReportedAsRaw()
    {
        // A tester's log reported "unsupported vector opcode Vop3Raw303",
        // which is v_add_nc_u16 reaching the translator undecoded.
        var program = Decode(
        [
            Vop3Word0(0x303u, vdst: 3),
            Vop3Word1(Vgpr(1), Vgpr(2)),
        ]);

        Assert.DoesNotContain(
            program.Instructions,
            instruction => instruction.Opcode.StartsWith("Vop3Raw", StringComparison.Ordinal));
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

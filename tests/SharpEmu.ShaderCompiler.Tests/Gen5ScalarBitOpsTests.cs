// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using System.Buffers.Binary;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

// Covers the SOP1 bit/absolute-value forms added for gfx10. SOP1 words are
// 0xBE800000 | sdst << 16 | op << 8 | ssrc0, the layout the existing
// s_ff1_i32_b64 fixtures in this project already rely on.
public sealed class Gen5ScalarBitOpsTests
{
    private const ulong ShaderAddress = 0x1000;
    private const uint SEndpgm = 0xBF810000;

    [Fact]
    public void ScalarBitOpcodesDecodeAndCompile()
    {
        uint[] words =
        [
            0xBE841502, // s_flbit_i32_b32 s4, s2
            0xBE881B01, // s_bitset0_b32 s8, s1
            0xBE8A1C01, // s_bitset0_b64 s[10:11], s1
            0xBE8C1E01, // s_bitset1_b64 s[12:13], s1
            0xBE853403, // s_abs_i32 s5, s3
            0xBE8E3B02, // s_bitreplicate_b64_b32 s[14:15], s2
        ];

        var program = Decode(words);
        Assert.Equal(
            [
                "SFlbitI32B32",
                "SBitset0B32",
                "SBitset0B64",
                "SBitset1B64",
                "SAbsI32",
                "SBitreplicateB64B32",
                "SEndpgm",
            ],
            program.Instructions.Select(static instruction => instruction.Opcode));

        var scalarRegisters = new uint[256];
        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                new Gen5ShaderState(program, new uint[10], null),
                new Gen5ShaderEvaluation(scalarRegisters, scalarRegisters, [], []),
                1,
                1,
                1,
                out _,
                out var compileError),
            compileError);
    }

    [Theory]
    // S_FLBIT_I32_B32 counts leading zeros and reports -1 for a zero source.
    [InlineData(0x8000_0000u, 0u)]
    [InlineData(0x0000_0001u, 31u)]
    [InlineData(0x0000_0000u, 0xFFFF_FFFFu)]
    public void FindLastBitCountsLeadingZeros(uint source, uint expected) =>
        Assert.Equal(expected, EvaluateScalar("SFlbitI32B32", source));

    [Theory]
    // S_ABS_I32 saturates at INT_MIN, whose magnitude is not representable.
    [InlineData(0x0000_0005u, 5u)]
    [InlineData(0xFFFF_FFFBu, 5u)]
    [InlineData(0x8000_0000u, 0x8000_0000u)]
    public void AbsoluteValueSaturatesAtIntMin(uint source, uint expected) =>
        Assert.Equal(expected, EvaluateScalar("SAbsI32", source));

    [Fact]
    public void BitReplicateSpreadsEachSourceBitIntoAPair()
    {
        // 0b1001 -> 0b11000011: bit i of the source becomes bits 2i and 2i+1.
        var (low, high) = EvaluateScalarPair("SBitreplicateB64B32", 0x8000_0009u);
        Assert.Equal(0x0000_00C3u, low);
        Assert.Equal(0xC000_0000u, high);
    }

    [Fact]
    public void BitsetClearsAndSetsTheSelectedBitOfAPair()
    {
        // The bit index is a plain 32-bit operand, and the destination pair is
        // read-modify-written, so bit 33 only touches the high dword.
        var (clearedLow, clearedHigh) = EvaluateScalarPair(
            "SBitset0B64", 33, initialLow: 0xFFFF_FFFFu, initialHigh: 0xFFFF_FFFFu);
        Assert.Equal(0xFFFF_FFFFu, clearedLow);
        Assert.Equal(0xFFFF_FFFDu, clearedHigh);

        var (setLow, setHigh) = EvaluateScalarPair("SBitset1B64", 33);
        Assert.Equal(0u, setLow);
        Assert.Equal(0x0000_0002u, setHigh);
    }

    private static uint EvaluateScalar(string opcode, uint source)
    {
        var registers = Evaluate(
            opcode,
            [Gen5Operand.Scalar(2)],
            Gen5Operand.Scalar(4),
            (2, source));
        return registers[4];
    }

    private static (uint Low, uint High) EvaluateScalarPair(
        string opcode,
        uint source,
        uint initialLow = 0,
        uint initialHigh = 0)
    {
        var registers = Evaluate(
            opcode,
            [Gen5Operand.Scalar(2)],
            Gen5Operand.Scalar(4),
            (2, source),
            (4, initialLow),
            (5, initialHigh));
        return (registers[4], registers[5]);
    }

    private static uint[] Evaluate(
        string opcode,
        Gen5Operand[] sources,
        Gen5Operand destination,
        params (uint Register, uint Value)[] seeds)
    {
        var scalarRegisters = new uint[256];
        foreach (var (register, value) in seeds)
        {
            scalarRegisters[register] = value;
        }

        var program = new Gen5ShaderProgram(
            0,
            [
                new Gen5ShaderInstruction(
                    0,
                    Gen5ShaderEncoding.Sop1,
                    opcode,
                    [0],
                    sources,
                    [destination],
                    null),
                new Gen5ShaderInstruction(
                    4,
                    Gen5ShaderEncoding.Sopp,
                    "SEndpgm",
                    [SEndpgm],
                    [],
                    [],
                    null),
            ]);
        var memory = new TestCpuMemory(ShaderAddress, 0x100);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                new Gen5ShaderState(program, scalarRegisters, null),
                out var evaluation,
                out var error),
            error);
        return [.. evaluation.ScalarRegisters];
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

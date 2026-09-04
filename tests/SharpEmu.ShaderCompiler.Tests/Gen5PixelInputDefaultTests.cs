// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

/// <summary>
/// SPI_PS_INPUT_CNTL.USE_DEFAULT marks a pixel input the vertex stage does
/// not export. The hardware then feeds the shader the constant named by
/// DEFAULT_VAL rather than interpolating a parameter that is not there.
/// </summary>
public sealed class Gen5PixelInputDefaultTests
{
    private const uint InputStorageClass = 1;
    private const uint UseDefault = 0x20;

    [Fact]
    public void ExportedAttributeIsInterpolatedFromAnInputVariable()
    {
        var instructions = ReadInstructions(Compile(cntl: 0));

        // The fragment coordinate plus the attribute itself.
        Assert.Equal(2, CountInputVariables(instructions));
    }

    [Theory]
    [InlineData(0u, 0f, 0f)] // (0,0,0,0)
    [InlineData(1u, 0f, 1f)] // (0,0,0,1)
    [InlineData(2u, 1f, 0f)] // (1,1,1,0)
    [InlineData(3u, 1f, 1f)] // (1,1,1,1)
    public void UnexportedAttributeReadsItsDefaultConstant(
        uint defaultValue,
        float rgb,
        float alpha)
    {
        var cntl = UseDefault | (defaultValue << 8);

        // Channel 0 is one of x/y/z, channel 3 is w; the two halves of
        // DEFAULT_VAL drive them independently.
        AssertReadsConstant(Compile(cntl, channel: 0), rgb);
        AssertReadsConstant(Compile(cntl, channel: 3), alpha);
    }

    [Fact]
    public void UnexportedAttributeDeclaresNoInputVariable()
    {
        var instructions = ReadInstructions(Compile(UseDefault | (3u << 8)));

        // Only the fragment coordinate is left: binding a variable here would
        // read whichever location the attribute happened to land on.
        Assert.Equal(1, CountInputVariables(instructions));
    }

    private static void AssertReadsConstant(byte[] spirv, float expected)
    {
        var instructions = ReadInstructions(spirv);
        var constants = instructions
            .Where(instruction => instruction.Opcode == SpirvOp.Constant)
            .Select(instruction => BitConverter.UInt32BitsToSingle(instruction.Operands[^1]))
            .ToArray();

        Assert.Contains(expected, constants);
    }

    private static int CountInputVariables(IReadOnlyList<ParsedInstruction> instructions) =>
        instructions.Count(instruction =>
            instruction.Opcode == SpirvOp.Variable &&
            instruction.Operands.Length >= 3 &&
            instruction.Operands[2] == InputStorageClass);

    private static byte[] Compile(uint cntl, uint channel = 0)
    {
        var interpolate = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Vintrp,
            "VInterpP2F32",
            [0u],
            [Gen5Operand.Vector(1)],
            [Gen5Operand.Vector(0)],
            new Gen5InterpolationControl(0, channel));
        var export = new Gen5ShaderInstruction(
            sizeof(uint),
            Gen5ShaderEncoding.Exp,
            "Exp",
            [],
            [
                Gen5Operand.Vector(0),
                Gen5Operand.Vector(0),
                Gen5Operand.Vector(0),
                Gen5Operand.Vector(0),
            ],
            [],
            new Gen5ExportControl(0, 0xF, false, true, true));
        var end = new Gen5ShaderInstruction(
            3 * sizeof(uint),
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [0xBF810000],
            [],
            [],
            null);

        var state = new Gen5ShaderState(
            new Gen5ShaderProgram(0x1_0000_D000, [interpolate, export, end]),
            [],
            null);
        var evaluation = new Gen5ShaderEvaluation(new uint[256], new uint[256], [], []);

        Assert.True(
            Gen5SpirvTranslator.TryCompilePixelShader(
                state,
                evaluation,
                Gen5PixelOutputKind.Float,
                out var shader,
                out var error,
                pixelInputCntl: [cntl]),
            error);
        return shader.Spirv;
    }

    private static IReadOnlyList<ParsedInstruction> ReadInstructions(byte[] spirv)
    {
        var instructions = new List<ParsedInstruction>();
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var header = BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(offset));
            var wordCount = checked((int)(header >> 16));
            Assert.InRange(wordCount, 1, (spirv.Length - offset) / sizeof(uint));
            var operands = new uint[wordCount - 1];
            for (var index = 0; index < operands.Length; index++)
            {
                operands[index] = BinaryPrimitives.ReadUInt32LittleEndian(
                    spirv.AsSpan(offset + (index + 1) * sizeof(uint)));
            }

            instructions.Add(new ParsedInstruction((SpirvOp)(ushort)header, operands));
            offset += wordCount * sizeof(uint);
        }

        return instructions;
    }

    private readonly record struct ParsedInstruction(SpirvOp Opcode, uint[] Operands);
}

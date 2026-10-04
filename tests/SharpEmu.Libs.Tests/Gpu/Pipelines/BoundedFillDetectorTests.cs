// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Tests.Agc;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

public sealed class BoundedFillDetectorTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;

    private static readonly uint[] ZeroRangeFromConstantBuffer =
    [
        0xBEFC03FF, 0x24311A8B,
        0xBF960000,
        0xBFA00001,
        0xD7460000, 0x04010C08,
        0xF4201A82, 0xFA000004,
        0xBF8CC07F,
        0x7DA8006A,
        0xBF880007,
        0x7E020280,
        0xF4201A82, 0xFA000000,
        0xBF8CC07F,
        0x4A00006A,
        0xE0102000, 0x80000100,
    ];

    private static readonly uint[] ValueRangeFromPointer =
    [
        0xBEFC03FF, 0xDCFA31EA,
        0xBF960000,
        0xBFA00001,
        0xD7460000, 0x04010C02,
        0xF4040100, 0xFA000000,
        0xBF8CC07F,
        0xF4001A82, 0xFA000004,
        0xBF8CC07F,
        0x7DA8006A,
        0xBF88000C,
        0xF4001A82, 0xFA000000,
        0xF4001AC2, 0xFA000008,
        0xBF8CC07F,
        0x4A00006A,
        0x7E02026B,
        0xF4080002, 0xFA000010,
        0xBF8CC07F,
        0xE0702000, 0x80000100,
    ];

    private static readonly uint[] PatternFromUserData =
    [
        0xBFA00003,
        0xD7460002, 0x04010C0A,
        0x7DA80408,
        0xBF88003F,
        0x7E000C09,
        0xBF070980,
        0x858A807E,
        0x7E005700,
        0x100000FF, 0x4F800000,
        0x7E060F00,
        0xD5766A00, 0x02020609,
        0x7D8A0280,
        0x4C020080,
        0x02000101,
        0xD56A0001, 0x00020700,
        0x4C000303,
        0x4A020303,
        0x02000101,
        0xD56A0000, 0x00020500,
        0xD5690001, 0x00020009,
        0x4C060302,
        0x7D860609,
        0x7D8C02F9, 0x06068C02,
        0x87EA6A0C,
        0x50000080,
        0xD5286A00, 0x003200C1,
        0xD5010000, 0x002A00C1,
        0xD5690000, 0x00020009,
        0x4C000102,
        0x7D0A0080,
        0xBE88246A,
        0xBF880015,
        0x7D0A0081,
        0xBE8A246A,
        0xBF88000C,
        0x7D0A0082,
        0xBEEA246A,
        0xBF880003,
        0x7E000207,
        0xE0102000, 0x80000002,
        0x8AFE7E6A,
        0xBF880003,
        0x7E000206,
        0xE0102000, 0x80000002,
        0xBEFE046A,
        0x8AFE7E0A,
        0xBF880003,
        0x7E000205,
        0xE0102000, 0x80000002,
        0xBEFE040A,
        0x8AFE7E08,
        0xBF880003,
        0x7E000204,
        0xE0102000, 0x80000002,
    ];

    [Fact]
    public void ARepeatingPatternFillFromUserDataIsRecognized()
    {
        var fill = BoundedFillDetector.Detect(Decode(PatternFromUserData));

        Assert.NotNull(fill);
        static FillWord User(uint register) => new(FillWordSource.UserData, register, 0, 0);
        Assert.Equal(10u, fill.GroupScalarRegister);
        Assert.Equal(User(8), fill.Count);
        Assert.Null(fill.Start);
        Assert.Equal(User(4), fill.Value);
        Assert.Equal(User(9), fill.PatternLength);
        Assert.Equal([User(4), User(5), User(6), User(7)], fill.Pattern!);
        Assert.Equal([User(0), User(1), User(2), User(3)], fill.Destination);
        Assert.True(fill.Formatted);
    }

    [Fact]
    public void APatternWithUnverifiedArithmeticIsRejected()
    {
        var program = Decode(PatternFromUserData);
        program = program with
        {
            Instructions = program.Instructions.Select(instruction => instruction.Opcode == "VRcpIflagF32"
                ? instruction with { Sources = [instruction.Sources[0] with { Kind = Gen5OperandKind.EncodedConstant, Value = 128 }] }
                : instruction).ToArray(),
        };
        Assert.Null(BoundedFillDetector.Detect(program));
    }

    [Theory]
    [InlineData(0)] // reciprocal scale
    [InlineData(1)] // overwrite the output index
    [InlineData(2)] // restore another EXEC mask
    [InlineData(3)] // float negation
    public void APatternWithChangedDataFlowIsRejected(int mutation)
    {
        var program = Decode(PatternFromUserData);
        var instructions = program.Instructions.ToArray();
        var at = Array.FindIndex(instructions, instruction => instruction.Opcode ==
            (mutation == 0 ? "VMulF32" : mutation == 1 ? "VSubI32" : mutation == 2 ? "SMovB64" : "VLshlAddU32"));
        var original = instructions[at];
        instructions[at] = mutation switch
        {
            0 => original with { Sources = [original.Sources[0] with { Value = 0x3F800000 }, original.Sources[1]] },
            1 => original with { Destinations = [Gen5Operand.Vector(2)] },
            2 => original with { Sources = [Gen5Operand.Scalar(8)] },
            _ => original with { Control = ((Gen5Vop3Control)original.Control!) with { NegateMask = 1 } },
        };
        Assert.Null(BoundedFillDetector.Detect(program with { Instructions = instructions }));
    }

    [Fact]
    public void ThePatternIsRecognizedAfterVectorRegisterAllocationChanges()
    {
        var program = Decode(PatternFromUserData);
        // Preserve the architectural input v0 and move only the global index v2.
        Gen5Operand Rename(Gen5Operand operand) => operand.Kind == Gen5OperandKind.VectorRegister && operand.Value == 2
            ? operand with { Value = 17 } : operand;
        var renamed = program.Instructions.Select(instruction => instruction with
        {
            Sources = instruction.Sources.Select(Rename).ToArray(),
            Destinations = instruction.Destinations.Select(Rename).ToArray(),
            Control = instruction.Control is Gen5BufferMemoryControl control
                ? control with { VectorAddress = 17 } : instruction.Control,
        }).ToArray();
        Assert.NotNull(BoundedFillDetector.Detect(program with { Instructions = renamed }));
    }

    [Fact]
    public void APatternFillWithAnotherBranchTargetIsRejected()
    {
        var words = PatternFromUserData.ToArray();
        words[Array.IndexOf(words, 0xBF880015u)] = 0xBF880014u;
        Assert.Null(BoundedFillDetector.Detect(Decode(words)));
    }

    private static Gen5ShaderProgram Decode(uint[] words)
    {
        var memory = new FakeCpuMemory(ShaderAddress, 0x2000);
        Gen5ShaderAtomicDecodeTests.WriteProgram(memory, ShaderAddress, words);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(new CpuContext(memory, Generation.Gen5), ShaderAddress, out var program, out var error), error);
        return program;
    }

    [Fact]
    public void AZeroFillBoundedByAConstantBufferIsRecognized()
    {
        var fill = BoundedFillDetector.Detect(Decode(ZeroRangeFromConstantBuffer));

        Assert.NotNull(fill);
        Assert.Equal(8u, fill.GroupScalarRegister);
        Assert.Equal(new FillWord(FillWordSource.BufferResource, 4, 0, 4), fill.Count);
        Assert.Equal(new FillWord(FillWordSource.BufferResource, 4, 0, 0), fill.Start);
        Assert.Equal(0u, fill.ConstantValue);
        Assert.Null(fill.Value);
        Assert.Equal(
            [0u, 1, 2, 3],
            fill.Destination.Select(word => word.Source == FillWordSource.UserData ? word.Register : uint.MaxValue));
        Assert.True(fill.Formatted);
    }

    [Fact]
    public void AValueFillDescribedThroughAPointerIsRecognized()
    {
        var fill = BoundedFillDetector.Detect(Decode(ValueRangeFromPointer));

        Assert.NotNull(fill);
        Assert.Equal(2u, fill.GroupScalarRegister);
        Assert.Equal(new FillWord(FillWordSource.IndirectPointer, 0, 0, 4), fill.Count);
        Assert.Equal(new FillWord(FillWordSource.IndirectPointer, 0, 0, 0), fill.Start);
        Assert.Null(fill.ConstantValue);
        Assert.Equal(new FillWord(FillWordSource.IndirectPointer, 0, 0, 8), fill.Value);
        Assert.Equal(
            [16, 20, 24, 28],
            fill.Destination.Select(word => word.Source == FillWordSource.IndirectPointer && word.Register == 0 ? word.Offset : -1));
        Assert.False(fill.Formatted);
    }

    [Fact]
    public void AnyOtherInstructionRejectsTheFill()
    {
        var words = ZeroRangeFromConstantBuffer.ToArray();
        words[Array.IndexOf(words, 0x7E020280u)] = 0x7E026E80u;
        Assert.Null(BoundedFillDetector.Detect(Decode(words)));
    }

    [Fact]
    public void ABranchThatSkipsPastTheEndRejectsTheFill()
    {
        var words = ZeroRangeFromConstantBuffer.ToArray();
        words[Array.IndexOf(words, 0xBF880007u)] = 0xBF880006u;
        Assert.Null(BoundedFillDetector.Detect(Decode(words)));
    }
}

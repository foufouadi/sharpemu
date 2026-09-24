// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

// V_MAD_F32 and V_MAC_F32 round the product before the add; only the V_FMA forms are fused.
public sealed class Gen5MultiplyAddRoundingTests
{
    public const uint Sentinel = 0xCAFE_BABE;

    // (1 + 2^-12)^2 = 1 + 2^-11 + 2^-24 rounds to 1 + 2^-11, so adding -(1 + 2^-11)
    // gives 0 when the product is rounded and 2^-24 when it is fused.
    public const uint Factor = 0x3F80_0800;
    public const uint Addend = 0xBF80_1000;
    public const uint RoundedResult = 0x0000_0000;

    public static TheoryData<string, bool> Opcodes => new()
    {
        { "VMadF32", false },
        { "VMadMkF32", false },
        { "VMadAkF32", false },
        { "VMacF32", false },
        { "VFmaF32", true },
        { "VFmacF32", true },
    };

    // v6 = op(s8, s9, s10), stored to the buffer at s[4:7]. The MAC forms accumulate into v6,
    // which starts as the addend.
    public static Gen5ShaderProgram CreateReadbackProgram(string opcode)
    {
        var accumulate = opcode is "VMacF32" or "VFmacF32";
        var operation = accumulate
            ? Vop2(20, opcode, 6, Gen5Operand.Vector(2), Gen5Operand.Vector(3))
            : Vop3(20, opcode, 6, Gen5Operand.Vector(2), Gen5Operand.Vector(3), Gen5Operand.Vector(4));
        return Program(
            MoveVectorFromScalar(0, 2, 8), MoveVectorFromScalar(4, 3, 9), MoveVectorFromScalar(8, 4, 10),
            accumulate ? MoveVectorFromScalar(12, 6, 10) : MoveVector(12, 6, Sentinel),
            Nop(16),
            operation,
            BufferAccess(28, "BufferStoreDword", 4, vectorData: 6), EndProgram(36));
    }

    [Theory]
    [MemberData(nameof(Opcodes))]
    public void OnlyTheFmaFormsAreFused(string opcode, bool fused)
    {
        var (plan, resources, layout) = Prepare(CreateReadbackProgram(opcode));
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);

        Assert.Equal(fused, SpirvWords.UsesExtendedInstruction(shader.Spirv, SpirvWords.GlslFma));
        if (!fused)
        {
            // Both halves must stay separate operations the driver may not contract.
            var precise = SpirvWords.Decorated(shader.Spirv, SpirvWords.DecorationNoContraction);
            Assert.NotEmpty(SpirvWords.ResultsOf(shader.Spirv, SpirvWords.OpFMul).Intersect(precise));
            Assert.NotEmpty(SpirvWords.ResultsOf(shader.Spirv, SpirvWords.OpFAdd).Intersect(precise));
        }
    }
}

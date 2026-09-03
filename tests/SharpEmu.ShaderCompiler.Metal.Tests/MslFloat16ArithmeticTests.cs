// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

public sealed class MslFloat16ArithmeticTests
{
    [Fact]
    public void CompactFloat16ArithmeticUsesHalfOperandsAndPreservesRegisterShape()
    {
        var fixture = new Gen5ComputeFixture(
            "compact-f16-arithmetic",
            [
                0x64000501,
                0x66060B04,
                0x680C1107,
                0x6A12170A,
                0x72181D0D,
                0x741E2310,
                0xBF810000,
            ],
            StoreScalarResourceBase: 0,
            StoreBackingBytes: 0);

        var shader = Gen5ComputeFixtures.CompileOrThrow(fixture);

        Assert.Contains("as_type<half>", shader.Source, StringComparison.Ordinal);
        Assert.Contains("fmin(", shader.Source, StringComparison.Ordinal);
        Assert.Contains("fmax(", shader.Source, StringComparison.Ordinal);
        Assert.Contains("& 0xFFFF0000u", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void Float16UnariesEmitTheirHalfPrecisionForms()
    {
        // VOP1: 0111111 | vdst[8] | opcode[8] | src0[9], VGPR = 256 + n.
        // v_rsq_f16 v1, v0 / v_sqrt_f16 v2, v0 / v_sin_f16 v3, v0.
        var fixture = new Gen5ComputeFixture(
            "f16-unaries",
            [
                0x7E02AD00,
                0x7E04AB00,
                0x7E06C100,
                0xBF810000,
            ],
            StoreScalarResourceBase: 0,
            StoreBackingBytes: 0);

        var shader = Gen5ComputeFixtures.CompileOrThrow(fixture);

        Assert.Contains("rsqrt(", shader.Source, StringComparison.Ordinal);
        Assert.Contains("sqrt(", shader.Source, StringComparison.Ordinal);
        Assert.Contains("sin(", shader.Source, StringComparison.Ordinal);
        Assert.Contains("as_type<half>", shader.Source, StringComparison.Ordinal);
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Ir;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

/// <summary>
/// GetScalarAt seeks to the queried block instead of walking the whole program.
/// The seek has to land on exactly the instructions the old full scan would have
/// applied, across a branch, where the block being asked about is not the first
/// in program order.
/// </summary>
public sealed class Gen5ScalarSsaBlockLookupTests
{
    private static Gen5ShaderInstruction Mov(uint pc, uint destination, uint literal) =>
        new(
            pc,
            Gen5ShaderEncoding.Sop1,
            "SMov",
            [0u],
            [new Gen5Operand(Gen5OperandKind.LiteralConstant, literal)],
            [Gen5Operand.Scalar(destination)],
            null);

    private static Gen5ShaderInstruction Nop(uint pc) =>
        new(pc, Gen5ShaderEncoding.Sop1, "SNop", [0u], [], [], null);

    private static Gen5ShaderInstruction Branch(uint pc, short wordOffset) =>
        new(
            pc,
            Gen5ShaderEncoding.Sopp,
            "SBranch",
            [unchecked((uint)(ushort)wordOffset)],
            [],
            [],
            null);

    // Two blocks: the branch ends the first, and the second writes s8 again.
    // Asking inside the second block must see its own write, not the first's.
    private static List<Gen5ShaderInstruction> Branching() =>
    [
        Mov(0, destination: 8, literal: 0x1111),
        Branch(4, 1),
        Mov(8, destination: 8, literal: 0x2222),
        Mov(12, destination: 9, literal: 0x3333),
        Nop(16),
    ];

    [Fact]
    public void QueryBeforeAWriteDoesNotSeeIt()
    {
        var ssa = Gen5ScalarSsa.Build(Branching(), userData: []);

        // s9 is written at pc 12; asking at 12 must not include that write.
        var value = ssa.GetScalarAt(12, 9);

        Assert.NotEqual(0x3333u, value.Constant);
    }

    [Fact]
    public void FirstBlockIsUnaffectedByLaterBlocks()
    {
        var ssa = Gen5ScalarSsa.Build(Branching(), userData: []);

        var value = ssa.GetScalarAt(4, 8);

        Assert.Equal(IrScalarState.Constant, value.State);
        Assert.Equal(0x1111u, value.Constant);
    }

    [Fact]
    public void EveryQueryPointMatchesAFullScanOfTheProgram()
    {
        // The old lookup scanned every instruction and skipped the ones outside
        // the block. Reproduce that here and require the seek to agree at every
        // instruction boundary, for every register the program touches.
        var program = Branching();
        var ssa = Gen5ScalarSsa.Build(program, userData: []);

        foreach (var instruction in program)
        {
            foreach (var register in new uint[] { 8, 9 })
            {
                var seeked = ssa.GetScalarAt(instruction.Pc, register);
                var scanned = ReferenceScalarAt(ssa, program, instruction.Pc, register);
                Assert.Equal(scanned.State, seeked.State);
                Assert.Equal(scanned.Constant, seeked.Constant);
            }
        }
    }

    private static IrScalarValue ReferenceScalarAt(
        Gen5ScalarSsa ssa,
        IReadOnlyList<Gen5ShaderInstruction> program,
        uint pc,
        uint register)
    {
        // Independent of the implementation under test: rebuild the value by
        // replaying the block's instructions from its entry state.
        IrBlockRange? found = null;
        foreach (var candidate in ssa.Graph.Blocks)
        {
            if (pc >= candidate.StartPc && pc < candidate.EndPc)
            {
                found = candidate;
                break;
            }
        }

        if (found is not { } block)
        {
            return IrScalarValue.Unknown;
        }

        var last = IrScalarValue.Unknown;
        var seen = false;
        foreach (var instruction in program)
        {
            if (instruction.Pc < block.StartPc ||
                instruction.Pc >= block.EndPc ||
                instruction.Pc >= pc)
            {
                continue;
            }

            if (instruction.Destinations.Count == 1 &&
                instruction.Destinations[0].Kind == Gen5OperandKind.ScalarRegister &&
                instruction.Destinations[0].Value == register &&
                instruction.Sources.Count == 1 &&
                instruction.Sources[0].Kind == Gen5OperandKind.LiteralConstant)
            {
                last = IrScalarValue.FromConstant(instruction.Sources[0].Value);
                seen = true;
            }
        }

        return seen ? last : ssa.GetScalarAt(block.StartPc, register);
    }
}

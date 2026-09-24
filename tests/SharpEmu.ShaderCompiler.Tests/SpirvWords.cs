// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

namespace SharpEmu.ShaderCompiler.Tests;

// Walks a SPIR-V module instruction by instruction so tests can assert on what was emitted.
public static class SpirvWords
{
    public const uint OpExtInst = 12;
    public const uint OpDecorate = 71;
    public const uint OpFAdd = 129;
    public const uint OpFMul = 133;
    public const uint OpControlBarrier = 224;
    public const uint DecorationNoContraction = 42;
    public const uint GlslFma = 50;

    public static IEnumerable<uint[]> Instructions(byte[] spirv)
    {
        var words = new uint[spirv.Length / sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            words[index] = BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(index * sizeof(uint)));
        }

        // The five-word header precedes the instruction stream.
        for (var cursor = 5; cursor < words.Length;)
        {
            var count = (int)(words[cursor] >> 16);
            if (count == 0 || cursor + count > words.Length)
            {
                throw new InvalidDataException($"malformed SPIR-V instruction at word {cursor}");
            }

            yield return words.AsSpan(cursor, count).ToArray();
            cursor += count;
        }
    }

    public static uint Opcode(uint[] instruction) => instruction[0] & 0xFFFFu;

    public const uint OpConstant = 43;

    // The value of a 32-bit OpConstant by its result id.
    public static uint ConstantValue(byte[] spirv, uint id) =>
        Instructions(spirv).Single(instruction => Opcode(instruction) == OpConstant && instruction[2] == id)[3];

    // Result ids of every instruction with this opcode.
    public static HashSet<uint> ResultsOf(byte[] spirv, uint opcode) =>
        [.. Instructions(spirv).Where(instruction => Opcode(instruction) == opcode).Select(instruction => instruction[2])];

    public static HashSet<uint> Decorated(byte[] spirv, uint decoration) =>
        [.. Instructions(spirv)
            .Where(instruction => Opcode(instruction) == OpDecorate && instruction.Length >= 3 && instruction[2] == decoration)
            .Select(instruction => instruction[1])];

    // OpExtInst result type, result, set, instruction, operands...
    public static bool UsesExtendedInstruction(byte[] spirv, uint instruction) =>
        Instructions(spirv).Any(words => Opcode(words) == OpExtInst && words.Length >= 5 && words[4] == instruction);
}

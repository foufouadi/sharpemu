// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

public sealed class PackedShiftAndHalfStoreDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    // V_PK_*REV_B16 shift source 1 by source 0 (RDNA2 ISA, VOP3P opcodes 4-6). OP_SEL and
    // OP_SEL_HI pick a half per source: bit 0 for source 0, bit 1 for source 1.
    [Theory]
    [InlineData("VPkAshrrevI16", 0b00u, 0b11u, 0x000F_0001u, 0x8000_8000u, 0xFFFF_C000u)]
    [InlineData("VPkAshrrevI16", 0b10u, 0b01u, 0x0004_0001u, 0x8000_7FF0u, 0x07FF_C000u)]
    [InlineData("VPkLshrrevB16", 0b10u, 0b01u, 0x0004_0001u, 0x8000_7FF0u, 0x07FF_4000u)]
    [InlineData("VPkLshlrevB16", 0b10u, 0b01u, 0x0004_0001u, 0x8000_7FF0u, 0xFF00_0000u)]
    [InlineData("VPkLshlrevB16", 0b00u, 0b11u, 0x0011_0011u, 0x0001_0001u, 0x0002_0002u)]
    public void PackedShiftsSelectEachSourcesHalfAndSignExtend(string opcode, uint opSel, uint opSelHi, uint counts, uint values, uint expected)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var program = Program(
            MoveVectorFromScalar(0, 0, 8),
            MoveVectorFromScalar(4, 1, 9),
            Vop3(8, opcode, 12, Gen5Operand.Vector(0), Gen5Operand.Vector(1)) with { Control = new Gen5Vop3pControl(opSel, opSelHi, 0, 0, false) },
            BufferAccess(16, "BufferStoreDword", 4, vectorData: 12),
            EndProgram(24));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(64);
        var registers = new uint[256];
        registers[6] = 64;
        registers[8] = counts;
        registers[9] = values;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [result] }, 1));
        Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(runner.ReadBack(result, 0, 4)));
        harness.AssertNoValidationMessages();
    }

    // DS_WRITE_B16 (DS opcode 31) stores DATA[15:0] and leaves the other half of the dword alone,
    // even when another lane writes that half at the same time.
    [Fact]
    public void HalfStoresFromTwoLanesFillOneDword()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var program = Program(
            Vop2(0, "VLshlrevB32", 2, Operand(1), Gen5Operand.Vector(0)),
            Vop2(4, "VAddU32", 3, new Gen5Operand(Gen5OperandKind.LiteralConstant, 0x1_AB12), Gen5Operand.Vector(0)),
            DataShare(12, "DsWriteB16", false, [Gen5Operand.Vector(2), Gen5Operand.Vector(3)], []),
            MoveVector(20, 4, 0),
            DataShare(28, "DsReadB32", false, [Gen5Operand.Vector(4)], [5]),
            Vop2(36, "VLshlrevB32", 6, Operand(2), Gen5Operand.Vector(0)),
            BufferAccess(40, "BufferStoreDword", 4, vectorData: 5, offsetEnabled: true, vectorAddress: 6),
            EndProgram(48));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 2, ThreadCountX = 2, WaveSize = 32 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(64);
        var registers = new uint[256];
        registers[6] = 64;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [result] }, 1));
        var actual = runner.ReadBack(result, 0, 8);
        Assert.Equal(0xAB13_AB12u, BinaryPrimitives.ReadUInt32LittleEndian(actual));
        Assert.Equal(0xAB13_AB12u, BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(4)));
        harness.AssertNoValidationMessages();
    }
}

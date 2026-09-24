// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

public sealed class MultiplyAddRoundingDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    // Vulkan lets an implementation evaluate GLSL.std.450 Fma unfused (llvmpipe does), so only
    // the rounded forms have a device-independent result; the fused ones are checked in SPIR-V.
    [Theory]
    [InlineData("VMadF32")]
    [InlineData("VMadMkF32")]
    [InlineData("VMadAkF32")]
    [InlineData("VMacF32")]
    public void MadRoundsTheProduct(string opcode)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var (plan, resources, layout) = Prepare(Gen5MultiplyAddRoundingTests.CreateReadbackProgram(opcode));
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(64);
        var bindings = new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [result] };
        var registers = new uint[256];
        registers[6] = 64;
        registers[8] = Gen5MultiplyAddRoundingTests.Factor;
        registers[9] = Gen5MultiplyAddRoundingTests.Factor;
        registers[10] = Gen5MultiplyAddRoundingTests.Addend;

        harness.Run(() => runner.Dispatch(registers, bindings, 1));

        var actual = BinaryPrimitives.ReadUInt32LittleEndian(runner.ReadBack(result, 0, sizeof(uint)));
        Assert.Equal(Gen5MultiplyAddRoundingTests.RoundedResult, actual);
        harness.AssertNoValidationMessages();
    }
}

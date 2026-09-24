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

public sealed class DataShareFloatMinMaxDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Theory]
    [MemberData(nameof(Gen5DataShareFloatMinMaxTests.Cases), MemberType = typeof(Gen5DataShareFloatMinMaxTests))]
    public void MinMaxComparesTheStoredValueWithData0(bool global, bool max, uint data0, uint expected)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var (plan, resources, layout) = Prepare(Gen5DataShareFloatMinMaxTests.CreateReadbackProgram(max, global));
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(64);
        var bindings = new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [result] };
        if (global) bindings[DescriptorBindingKind.GlobalDataShare] = [runner.CreateBuffer(1024)];
        var registers = new uint[256];
        registers[6] = 64;
        registers[8] = 16;
        registers[9] = data0;

        harness.Run(() => runner.Dispatch(registers, bindings, 1));

        Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(runner.ReadBack(result, 0, sizeof(uint))));
        harness.AssertNoValidationMessages();
    }
}

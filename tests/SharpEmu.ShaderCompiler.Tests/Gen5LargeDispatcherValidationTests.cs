// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.ShaderCompiler.Vulkan;
using SharpEmu.ShaderCompiler.Tests.Resources;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5LargeDispatcherValidationTests
{
    [Fact]
    public void LargeDispatcherProducesValidStructuredSpirv()
    {
        const int branchPairs = 400;
        var instructions = new List<Gen5ShaderInstruction>(branchPairs * 2 + 1);
        for (var index = 0; index < branchPairs; index++)
        {
            var pc = (uint)index * 8;
            instructions.Add(ResourceTestProgram.Branch(pc, "SCbranchScc0", 1));
            instructions.Add(ResourceTestProgram.MoveScalar(pc + 4, 0, (uint)index));
        }

        instructions.Add(ResourceTestProgram.EndProgram((uint)branchPairs * 8));
        var request = ResourceTestProgram.Request(
            new Gen5ShaderProgram(0, instructions),
            userDataCount: 0);

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error),
            error);
        Assert.Contains(
            ReadSpirvOpcodes(shader.Spirv),
            opcode => opcode == SpirvOp.SelectionMerge);

        ValidateWithSpirvToolsWhenAvailable(shader.Spirv);
    }

    private static IReadOnlyList<SpirvOp> ReadSpirvOpcodes(byte[] code)
    {
        var words = new uint[code.Length / sizeof(uint)];
        Buffer.BlockCopy(code, 0, words, 0, code.Length);
        var result = new List<SpirvOp>();
        for (var index = 5; index < words.Length;)
        {
            var instructionWord = words[index];
            var wordCount = (int)(instructionWord >> 16);
            Assert.True(wordCount > 0 && index + wordCount <= words.Length);
            result.Add((SpirvOp)(instructionWord & 0xFFFF));
            index += wordCount;
        }

        return result;
    }

    private static void ValidateWithSpirvToolsWhenAvailable(byte[] code)
    {
        var sdk = Environment.GetEnvironmentVariable("VULKAN_SDK");
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(sdk))
        {
            candidates.Add(Path.Combine(sdk, OperatingSystem.IsWindows() ? "Bin/spirv-val.exe" : "bin/spirv-val"));
        }

        candidates.Add(@"C:\VulkanSDK\1.4.357.0\Bin\spirv-val.exe");
        var executable = candidates.FirstOrDefault(File.Exists);
        if (executable is null)
        {
            return;
        }

        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, code);
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("--target-env");
            start.ArgumentList.Add("vulkan1.2");
            start.ArgumentList.Add(path);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"spirv-val failed: {output}{error}");
        }
        finally
        {
            File.Delete(path);
        }
    }
}

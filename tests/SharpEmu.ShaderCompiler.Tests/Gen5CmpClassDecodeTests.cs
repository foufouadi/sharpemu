// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

/// <summary>
/// V_CMP_CLASS_F32 and its exec-writing twin V_CMPX_CLASS_F32. Both emitters
/// already handled the pair; only the decoder knew one of them.
/// </summary>
public sealed class Gen5CmpClassDecodeTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;
    private const uint SEndpgm = 0xBF810000;

    [Theory]
    [InlineData(0x7D100301u, "VCmpClassF32")]
    [InlineData(0x7D300301u, "VCmpxClassF32")]
    public void ClassComparisonsDecode(uint word, string expected)
    {
        // The decoder reads past the first terminator, so give it more of
        // them rather than zero padding it would try to decode.
        var program = Decode([word, SEndpgm, SEndpgm, SEndpgm]);

        Assert.Equal(expected, program.Instructions[0].Opcode);
    }

    private static Gen5ShaderProgram Decode(IReadOnlyList<uint> words)
    {
        var bytes = new byte[words.Count * sizeof(uint)];
        for (var index = 0; index < words.Count; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(index * sizeof(uint)),
                words[index]);
        }

        var memory = new TestCpuMemory(ShaderAddress, bytes.Length);
        Assert.True(memory.TryWrite(ShaderAddress, bytes));
        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                context,
                ShaderAddress,
                out var program,
                out var error),
            error);
        return program;
    }

    private sealed class TestCpuMemory(ulong baseAddress, int size) : ICpuMemory
    {
        private readonly byte[] _storage = new byte[size];

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (!TryGetOffset(virtualAddress, destination.Length, out var offset))
            {
                return false;
            }

            _storage.AsSpan(offset, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (!TryGetOffset(virtualAddress, source.Length, out var offset))
            {
                return false;
            }

            source.CopyTo(_storage.AsSpan(offset, source.Length));
            return true;
        }

        private bool TryGetOffset(ulong virtualAddress, int length, out int offset)
        {
            offset = 0;
            if (virtualAddress < baseAddress)
            {
                return false;
            }

            var relative = virtualAddress - baseAddress;
            if (relative + (ulong)length > (ulong)_storage.Length)
            {
                return false;
            }

            offset = (int)relative;
            return true;
        }
    }
}

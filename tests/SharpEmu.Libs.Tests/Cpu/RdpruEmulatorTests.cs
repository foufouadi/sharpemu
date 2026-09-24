// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Emulation;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class RdpruEmulatorTests
{
    private const uint CarryFlag = 1u << 0;
    private const uint ArithmeticFlags = CarryFlag | (1u << 2) | (1u << 4) | (1u << 6) | (1u << 7) | (1u << 11);
    private const uint InterruptFlag = 1u << 9;

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    public void KnownSelectorsReturnTheCounterAndSetCarry(uint selector)
    {
        var (eax, edx, eflags) = RdpruEmulator.Execute(selector, 0x0123_4567_89AB_CDEF, ArithmeticFlags | InterruptFlag);

        Assert.Equal(0x89AB_CDEFu, eax);
        Assert.Equal(0x0123_4567u, edx);
        Assert.Equal(CarryFlag | InterruptFlag, eflags);
    }

    [Fact]
    public void OtherSelectorsReturnZeroWithCarryClear()
    {
        var (eax, edx, eflags) = RdpruEmulator.Execute(2, ulong.MaxValue, ArithmeticFlags | InterruptFlag);

        Assert.Equal((0u, 0u, InterruptFlag), (eax, edx, eflags));
    }

    [Fact]
    public void RecognizesOnlyTheRdpruEncoding()
    {
        Assert.True(RdpruEmulator.IsRdpru([0x0F, 0x01, 0xFD]));
        Assert.False(RdpruEmulator.IsRdpru([0x0F, 0x01, 0xFA]));
        Assert.False(RdpruEmulator.IsRdpru([0x0F, 0x01]));
    }
}

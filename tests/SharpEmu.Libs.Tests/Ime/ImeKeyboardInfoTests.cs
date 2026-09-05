// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Ime;
using Xunit;

namespace SharpEmu.Libs.Tests.Ime;

/// <summary>
/// sceImeKeyboardGetInfo reports no keyboard by zeroing the caller's structure.
/// The extent it clears has to be the structure's real size: callers pass a
/// stack address, so clearing further erases their frame.
/// </summary>
public sealed class ImeKeyboardInfoTests
{
    // userId, device, type, repeatDelay, repeatRate and status, then twelve
    // reserved bytes. KytyPS5 pins the same layout with
    // static_assert(sizeof(KeyboardInfo) == 0x24) in src/libs/ime.h.
    private const int KeyboardInfoBytes = 0x24;

    [Fact]
    public void ClearsTheStructureAndNothingBeyondIt()
    {
        const ulong memoryBase = 0x1_0005_0000;
        const ulong infoAddress = memoryBase + 0x100;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);

        // Fill well past the structure so an over-long clear is visible.
        var canary = new byte[KeyboardInfoBytes + 0x80];
        canary.AsSpan().Fill(0xCD);
        Assert.True(memory.TryWrite(infoAddress, canary));

        context[CpuRegister.Rdi] = 1; // Resource id.
        context[CpuRegister.Rsi] = infoAddress;
        Assert.Equal(0, ImeExports.ImeKeyboardGetInfo(context));

        var readback = new byte[canary.Length];
        Assert.True(memory.TryRead(infoAddress, readback));

        for (var index = 0; index < KeyboardInfoBytes; index++)
        {
            Assert.Equal(0, readback[index]);
        }

        // Everything past the structure belongs to the caller. The stub used to
        // clear 0x88 bytes, which is the size of a different structure
        // entirely, and took 100 bytes of the caller's frame with it.
        for (var index = KeyboardInfoBytes; index < readback.Length; index++)
        {
            Assert.Equal(0xCD, readback[index]);
        }
    }

    [Fact]
    public void AcceptsANullStructureWithoutFaulting()
    {
        const ulong memoryBase = 0x1_0006_0000;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);

        context[CpuRegister.Rdi] = 1;
        context[CpuRegister.Rsi] = 0;

        Assert.Equal(0, ImeExports.ImeKeyboardGetInfo(context));
    }
}

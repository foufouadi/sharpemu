// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu.Debugging;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

// Guest code runs at its own virtual addresses, so an execution breakpoint is a
// patched trap byte. These cover the patching contract itself; whether the trap
// is caught is exercised by running a title under the debug server.
public sealed class ExecutionBreakpointTests
{
    private const byte TrapByte = 0xCC;

    [Fact]
    public void ArmingReplacesTheInstructionByteAndDisarmingRestoresIt()
    {
        var buffer = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.WriteByte(buffer, 0, 0x55); // push rbp
            var address = unchecked((ulong)buffer.ToInt64());
            var controller = new ExecutionBreakpointTable();

            Assert.True(controller.TryArm(address, out var error), error);
            Assert.Equal(TrapByte, Marshal.ReadByte(buffer, 0));
            Assert.Contains(address, controller.Armed);

            Assert.True(controller.TryDisarm(address));
            Assert.Equal(0x55, Marshal.ReadByte(buffer, 0));
            Assert.DoesNotContain(address, controller.Armed);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void ArmingTwiceKeepsTheOriginalByteToRestore()
    {
        // The second arm must not record the trap byte as the original, which
        // would leave the trap in place forever once disarmed.
        var buffer = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.WriteByte(buffer, 0, 0x90); // nop
            var address = unchecked((ulong)buffer.ToInt64());
            var controller = new ExecutionBreakpointTable();

            Assert.True(controller.TryArm(address, out _));
            Assert.True(controller.TryArm(address, out _));
            Assert.True(controller.TryDisarm(address));

            Assert.Equal(0x90, Marshal.ReadByte(buffer, 0));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void DisarmingAnUnarmedAddressReportsFalse()
    {
        var controller = new ExecutionBreakpointTable();
        Assert.False(controller.TryDisarm(0x8000_0000UL));
    }

    [Fact]
    public void ArmingAddressZeroIsRefusedWithAReason()
    {
        var controller = new ExecutionBreakpointTable();
        Assert.False(controller.TryArm(0, out var error));
        Assert.NotEmpty(error);
    }
}

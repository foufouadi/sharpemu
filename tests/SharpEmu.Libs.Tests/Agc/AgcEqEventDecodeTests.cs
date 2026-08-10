// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

// sce::Agc::getEqEventType/getEqContextId decode a delivered kevent: for a graphics event the
// type is the ident and the context is the data, and the two swap for any other filter (Kyty
// GraphicsDriverGetEqEventType/GraphicsDriverGetEqContextId, src/libs/agc.cpp:3995-4021).
// Ghost of Tsushima calls both on every GPU interrupt; returning a hard 0 hides the event type
// from the guest's dispatcher.
public sealed class AgcEqEventDecodeTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const ulong EventAddress = BaseAddress + 0x100;

    // Values captured live from Tsushima's kevents (artifacts/tsushima-ui-source-current log).
    private const ulong Ident = 0x52;
    private const ulong Data = 0x1F_1F1F;

    [Theory]
    [InlineData(KernelEventQueueCompatExports.KernelEventFilterGraphics, (uint)Ident, (uint)Data)]
    [InlineData(KernelEventQueueCompatExports.KernelEventFilterUser, (uint)Data, (uint)Ident)]
    public void DecodesEventTypeAndContextIdPerFilter(short filter, uint type, uint contextId)
    {
        var ctx = CreateContextWithEvent(filter);

        ctx[CpuRegister.Rdi] = EventAddress;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.DriverGetEqEventType(ctx));
        Assert.Equal(type, (uint)ctx[CpuRegister.Rax]);

        ctx[CpuRegister.Rdi] = EventAddress;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.DriverGetEqContextId(ctx));
        Assert.Equal(contextId, (uint)ctx[CpuRegister.Rax]);
    }

    // Kyty returns 0 for a null kevent rather than faulting; so must we.
    [Fact]
    public void NullEventDecodesToZero()
    {
        var ctx = CreateContextWithEvent(
            KernelEventQueueCompatExports.KernelEventFilterGraphics);

        ctx[CpuRegister.Rdi] = 0;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.DriverGetEqEventType(ctx));
        Assert.Equal(0ul, ctx[CpuRegister.Rax]);

        ctx[CpuRegister.Rdi] = 0;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.DriverGetEqContextId(ctx));
        Assert.Equal(0ul, ctx[CpuRegister.Rax]);
    }

    // kevent layout: ident u64@0x00, filter i16@0x08, flags u16@0x0A, fflags u32@0x0C,
    // data u64@0x10, udata u64@0x18.
    private static CpuContext CreateContextWithEvent(short filter)
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        Span<byte> kevent = stackalloc byte[0x20];
        BinaryPrimitives.WriteUInt64LittleEndian(kevent, Ident);
        BinaryPrimitives.WriteInt16LittleEndian(kevent[0x08..], filter);
        BinaryPrimitives.WriteUInt64LittleEndian(kevent[0x10..], Data);
        Assert.True(memory.TryWrite(EventAddress, kevent));
        return new CpuContext(memory, Generation.Gen5);
    }
}

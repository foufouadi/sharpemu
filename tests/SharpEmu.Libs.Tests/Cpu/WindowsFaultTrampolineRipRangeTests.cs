// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu.Native.Windows;
using SharpEmu.HLE.Host.Windows;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

// Regression coverage for the fault-trampoline RIP-range check: commit 5629beb's host-
// abstraction port silently dropped it, reintroducing the UnmanagedCallersOnly fatal
// (entering the managed handler with RIP in JIT/system code trips the CLR's reverse-P/Invoke
// check while the thread is in cooperative GC mode). This exercises the emitted machine code
// directly by calling the returned thunk as an ordinary function, rather than through a real
// vectored exception handler - the thunk's own logic (RIP compared against the JIT/system
// threshold) is what's under test, not Windows' SEH dispatch.
public sealed unsafe class WindowsFaultTrampolineRipRangeTests
{
    // Matches the CONTEXT (x64) Rip field offset the trampoline reads from.
    private const int ContextRipOffset = 0xF8;

    // Comfortably above the JIT/system threshold (0x7FF0'00000000) the trampoline gates on.
    private const ulong JitCodeRegionRip = 0x00007FF9_12345678UL;

    // A low, guest-address-shaped RIP, comfortably below the threshold.
    private const ulong GuestCodeRegionRip = 0x0000000A_00000000UL;

    private static volatile bool _managedCallbackInvoked;

    [UnmanagedCallersOnly]
    private static int ManagedCallback(nint exceptionPointers)
    {
        _managedCallbackInvoked = true;
        return 0; // EXCEPTION_CONTINUE_SEARCH
    }

    [Fact]
    public void FaultAboveJitThreshold_ReturnsContinueSearchWithoutEnteringManagedHandler()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = InvokeThunk(WindowsFaultCodes.AccessViolation, JitCodeRegionRip, out var invoked);

        Assert.Equal(0, result);
        Assert.False(invoked);
    }

    [Fact]
    public void FaultBelowJitThreshold_EntersManagedHandler()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        InvokeThunk(WindowsFaultCodes.AccessViolation, GuestCodeRegionRip, out var invoked);

        Assert.True(invoked);
    }

    private static int InvokeThunk(uint exceptionCode, ulong rip, out bool managedCallbackInvoked)
    {
        var memory = new WindowsHostMemory();
        var handling = new WindowsFaultHandling(memory);
        var callback = (nint)(delegate* unmanaged<nint, int>)&ManagedCallback;
        var thunk = handling.CreateHandlerThunk(callback, hostRspSwitchTlsSlot: 0, tlsGetValueAddress: 0);
        Assert.NotEqual(0, thunk);
        try
        {
            _managedCallbackInvoked = false;

            // EXCEPTION_RECORD: ExceptionCode is the first field (offset 0).
            var exceptionRecord = stackalloc byte[16];
            *(uint*)exceptionRecord = exceptionCode;

            // CONTEXT (x64): the trampoline only ever reads the Rip field.
            var context = stackalloc byte[0x100];
            *(ulong*)(context + ContextRipOffset) = rip;

            // EXCEPTION_POINTERS: { ExceptionRecord*, ContextRecord* }.
            var exceptionPointers = stackalloc nint[2];
            exceptionPointers[0] = (nint)exceptionRecord;
            exceptionPointers[1] = (nint)context;

            var thunkFn = (delegate* unmanaged<nint, int>)thunk;
            var result = thunkFn((nint)exceptionPointers);
            managedCallbackInvoked = _managedCallbackInvoked;
            return result;
        }
        finally
        {
            handling.FreeThunk(thunk);
        }
    }
}

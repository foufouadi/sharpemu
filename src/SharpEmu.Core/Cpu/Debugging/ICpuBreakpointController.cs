// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Core.Cpu.Debugging;

/// <summary>
/// Arms and disarms execution breakpoints at guest addresses. Handed to the
/// attached <see cref="ICpuDebugHook"/> when the backend starts, so a debugger
/// can request breaks without holding a reference to the backend itself.
/// </summary>
/// <remarks>
/// An armed breakpoint replaces the first byte of the guest instruction with a
/// trap and restores it when the breakpoint is hit or disarmed, so the address
/// must be the start of an instruction. Arming an address twice is idempotent.
/// </remarks>
public interface ICpuBreakpointController
{
    /// <summary>Arms a breakpoint; false when the address cannot be patched.</summary>
    bool TryArmExecutionBreakpoint(ulong address, out string error);

    /// <summary>Disarms a breakpoint, restoring the original instruction byte.</summary>
    bool TryDisarmExecutionBreakpoint(ulong address);

    /// <summary>Addresses currently armed.</summary>
    IReadOnlyCollection<ulong> ArmedExecutionBreakpoints { get; }
}

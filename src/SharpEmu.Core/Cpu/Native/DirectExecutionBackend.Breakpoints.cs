// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Debugging;

namespace SharpEmu.Core.Cpu.Native;

// Execution breakpoints. The debugger protocol, its store and its UI already
// existed; what was missing was anything that actually stops the CPU, so a
// breakpoint on an arbitrary address was accepted and silently never hit.
//
// Guest code executes natively at its own virtual addresses, so a breakpoint is
// the classic patched trap byte: replace the first byte of the instruction with
// int3, catch it in the vectored handler already installed for guest faults,
// rewind the instruction pointer, hand the frame to the debugger, then step
// over the restored instruction and put the trap back.
public sealed partial class DirectExecutionBackend : ICpuBreakpointController
{
	private const int Win64ContextEFlagsOffset = 0x44;
	private const uint TrapFlag = 0x100u;

	private readonly ExecutionBreakpointTable _breakpoints = new();

	// Address this thread must re-arm once it has stepped past the restored
	// instruction. Per-thread because two guest threads can be stepping over
	// different breakpoints at the same time.
	[ThreadStatic]
	private static ulong _breakpointRearmAddress;

	public IReadOnlyCollection<ulong> ArmedExecutionBreakpoints => _breakpoints.Armed;

	public bool TryArmExecutionBreakpoint(ulong address, out string error) =>
		_breakpoints.TryArm(address, out error);

	public bool TryDisarmExecutionBreakpoint(ulong address) =>
		_breakpoints.TryDisarm(address);

	/// <summary>
	/// Handles int3 from the vectored handler. True when the trap was one of
	/// ours and execution should continue from the restored instruction.
	/// </summary>
	private unsafe bool TryHandleExecutionBreakpoint(void* contextRecord, ulong rip)
	{
		if (_breakpoints.IsEmpty || rip == 0)
		{
			return false;
		}

		// int3 is one byte and the trap leaves the instruction pointer past it.
		var address = rip - 1;
		if (!_breakpoints.TryLiftForStep(address))
		{
			return false;
		}

		WriteCtxU64(contextRecord, 248, address);
		NotifyDebuggerBreakpoint(contextRecord, address);

		// Step over the instruction that is now restored, then put the trap
		// back. Until that single step retires, the breakpoint is lifted, so
		// another thread can run past this address unbroken; that window is the
		// price of patching bytes rather than using the four hardware slots.
		_breakpointRearmAddress = address;
		WriteCtxU32(
			contextRecord,
			Win64ContextEFlagsOffset,
			ReadCtxU32(contextRecord, Win64ContextEFlagsOffset) | TrapFlag);
		return true;
	}

	/// <summary>
	/// Handles the single step that follows a breakpoint hit, re-arming the
	/// trap. True when the step was ours.
	/// </summary>
	private unsafe bool TryCompleteExecutionBreakpointStep(void* contextRecord)
	{
		var address = _breakpointRearmAddress;
		if (address == 0)
		{
			return false;
		}

		_breakpointRearmAddress = 0;
		_breakpoints.TryRearm(address);
		WriteCtxU32(
			contextRecord,
			Win64ContextEFlagsOffset,
			ReadCtxU32(contextRecord, Win64ContextEFlagsOffset) & ~TrapFlag);
		return true;
	}

	private unsafe void NotifyDebuggerBreakpoint(void* contextRecord, ulong address)
	{
		var hook = _debugHook;
		var frame = _activeDebugFrame;
		if (hook is null || frame is null)
		{
			return;
		}

		try
		{
			// The debugger may block here to present the break. It runs on the
			// guest thread that trapped, which is what makes its register and
			// memory reads describe this thread.
			hook.OnBreakpoint(frame, address);
		}
		catch (Exception exception)
		{
			Console.Error.WriteLine(
				$"[LOADER][WARN] debugger breakpoint handler failed at 0x{address:X16}: {exception.Message}");
		}
	}
}

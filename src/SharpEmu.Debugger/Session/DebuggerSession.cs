// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Debugging;
using SharpEmu.Debugger.Breakpoints;
using SharpEmu.HLE;
using SharpEmu.Logging;

namespace SharpEmu.Debugger.Session;

/// <summary>
/// The default <see cref="IDebuggerSession"/>. It plugs into the CPU dispatcher
/// as an <see cref="ICpuDebugHook"/>: when a frame boundary warrants a stop it
/// parks the emulation thread inside <see cref="ICpuDebugHook.OnFrameEnter"/>
/// while a debug client inspects and edits state, then releases it on
/// continue/step.
/// </summary>
/// <remarks>
/// Pausing works by blocking the emulation thread on <see cref="_resumeGate"/>
/// from within the hook call. Because that thread is the one that owns the guest
/// context, register and memory accessors are safe to serve from other threads
/// only while it is parked — which is exactly the <see cref="DebuggerRunState.Paused"/>
/// window the accessors gate on.
/// </remarks>
public sealed class DebuggerSession : IDebuggerSession, ICpuDebugHook
{
    private static readonly SharpEmuLogger Log = SharpEmuLog.For("SharpEmu.Debugger");

    private readonly object _sync = new();
    private readonly ManualResetEventSlim _resumeGate = new(initialState: false);
    private readonly DebuggerSessionOptions _options;

    private ICpuDebugFrame? _currentFrame;
    private DebuggerRunState _state = DebuggerRunState.Detached;
    private DebugStopEvent? _lastStop;
    private bool _seenFirstFrame;
    private bool _pausePending;
    private bool _stepPending;

    public DebuggerSession(DebuggerSessionOptions? options = null)
    {
        _options = options ?? new DebuggerSessionOptions();
        Breakpoints = new BreakpointStore();
    }

    public BreakpointStore Breakpoints { get; }

    /// <summary>
    /// Arms breakpoints in the backend once it attaches. Null until then, which
    /// is why requests are replayed on attach rather than assumed to be live.
    /// </summary>
    private ICpuBreakpointController? _breakpointController;

    /// <summary>
    /// Execute breakpoints requested for addresses that were not mapped yet.
    /// A client can ask for a break in a module the loader has not reached, so
    /// arming is retried at every frame boundary rather than failing once.
    /// </summary>
    private readonly HashSet<ulong> _pendingExecutionBreakpoints = [];

    public ICpuDebugHook Hook => this;

    public event EventHandler<DebugStopEvent>? Stopped;

    public event EventHandler? Resumed;

    public event EventHandler? Terminated;

    public DebuggerRunState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public DebugStopEvent? LastStop
    {
        get
        {
            lock (_sync)
            {
                return _lastStop;
            }
        }
    }

    void ICpuDebugHook.OnFrameEnter(ICpuDebugFrame frame)
    {
        RetryPendingExecutionBreakpoints();
        DebugStopEvent? stop;
        lock (_sync)
        {
            _currentFrame = frame;
            var firstFrame = !_seenFirstFrame;
            _seenFirstFrame = true;

            var reason = ResolveStopReason(frame, firstFrame, out var breakpoint);
            if (reason is null)
            {
                _state = DebuggerRunState.Running;
                return;
            }

            _state = DebuggerRunState.Paused;
            _lastStop = new DebugStopEvent(
                reason.Value,
                DebugRegisterFile.Capture(frame),
                frame.Kind,
                frame.Label,
                breakpoint);
            stop = _lastStop;
            _resumeGate.Reset();
        }

        Log.Debug($"Debugger stop: {stop!.Reason} at 0x{stop.Address:X16} ({stop.FrameLabel})");
        Stopped?.Invoke(this, stop);

        // Park the emulation thread until a client resumes the target. The frame
        // stays live and inspectable for the whole wait.
        _resumeGate.Wait();

        lock (_sync)
        {
            if (_state != DebuggerRunState.Terminated)
            {
                _state = DebuggerRunState.Running;
            }
        }

        Resumed?.Invoke(this, EventArgs.Empty);
    }

    void ICpuDebugHook.OnFrameExit(ICpuDebugFrame frame, OrbisGen2Result result)
    {
        DebugStopEvent? stop = null;
        lock (_sync)
        {
            if (_options.BreakOnFault &&
                result != OrbisGen2Result.ORBIS_GEN2_OK &&
                _state != DebuggerRunState.Terminated)
            {
                // Parking here keeps the post-fault frame inspectable.
                _currentFrame = frame;
                _state = DebuggerRunState.Paused;
                _lastStop = BuildFaultStop(frame, result);
                stop = _lastStop;
                _resumeGate.Reset();
            }
        }

        if (stop is not null)
        {
            Log.Debug($"Debugger fault stop: {stop.Result} at 0x{stop.Address:X16} ({stop.FrameLabel})");
            Stopped?.Invoke(this, stop);
            _resumeGate.Wait();
            Resumed?.Invoke(this, EventArgs.Empty);
        }

        lock (_sync)
        {
            if (ReferenceEquals(_currentFrame, frame))
            {
                _currentFrame = null;
            }

            if (_state != DebuggerRunState.Terminated)
            {
                _state = DebuggerRunState.Running;
            }
        }
    }

    /// <summary>
    /// Re-arms breakpoints whose address was not mapped when they were
    /// requested. Each module initializer is a frame, so this converges as the
    /// loader brings modules in.
    /// </summary>
    private void RetryPendingExecutionBreakpoints()
    {
        ulong[] pending;
        lock (_sync)
        {
            if (_pendingExecutionBreakpoints.Count == 0)
            {
                return;
            }

            pending = [.. _pendingExecutionBreakpoints];
        }

        foreach (var address in pending)
        {
            ArmExecutionBreakpoint(address);
        }
    }

    void ICpuDebugHook.OnAttach(ICpuBreakpointController breakpoints)
    {
        lock (_sync)
        {
            _breakpointController = breakpoints;
        }

        // A client can add breakpoints before the target starts, so replay what
        // is already in the store rather than only arming later additions.
        foreach (var breakpoint in Breakpoints.Snapshot())
        {
            if (breakpoint.Enabled && breakpoint.Kind == BreakpointKind.Execute)
            {
                ArmExecutionBreakpoint(breakpoint.Address);
            }
        }
    }

    /// <summary>
    /// Asks the backend to arm an execution breakpoint. Returns the failure
    /// reason when the address cannot be patched, so a client is told rather
    /// than left with a breakpoint that silently never fires - which is what
    /// the whole surface did before the backend could stop the CPU.
    /// </summary>
    public bool ArmExecutionBreakpoint(ulong address, out string error)
    {
        error = string.Empty;
        ICpuBreakpointController? controller;
        lock (_sync)
        {
            controller = _breakpointController;
        }

        if (controller is null)
        {
            error = "the target has not started yet";
            return false;
        }

        if (!controller.TryArmExecutionBreakpoint(address, out error))
        {
            // Most often the module carrying this address is not mapped yet.
            // Keep it and retry as frames arrive.
            lock (_sync)
            {
                _pendingExecutionBreakpoints.Add(address);
            }

            Log.Warn($"Could not arm breakpoint at 0x{address:X16} yet: {error}");
            return false;
        }

        lock (_sync)
        {
            _pendingExecutionBreakpoints.Remove(address);
        }

        return true;
    }

    private void ArmExecutionBreakpoint(ulong address) =>
        _ = ArmExecutionBreakpoint(address, out _);

    /// <summary>Disarms a breakpoint; false when it was not armed.</summary>
    public bool DisarmExecutionBreakpoint(ulong address)
    {
        ICpuBreakpointController? controller;
        lock (_sync)
        {
            controller = _breakpointController;
        }

        return controller?.TryDisarmExecutionBreakpoint(address) == true;
    }

    void ICpuDebugHook.OnBreakpoint(ICpuDebugFrame frame, ulong address)
    {
        DebugStopEvent stop;
        lock (_sync)
        {
            _currentFrame = frame;
            _state = DebuggerRunState.Paused;
            _lastStop = new DebugStopEvent(
                DebugStopReason.Breakpoint,
                DebugRegisterFile.Capture(frame),
                frame.Kind,
                frame.Label,
                Breakpoints.FindExecuteHit(address));
            stop = _lastStop;
            _resumeGate.Reset();
        }

        Log.Debug($"Debugger stop: breakpoint at 0x{address:X16} ({stop.FrameLabel})");
        Stopped?.Invoke(this, stop);

        // Parks the guest thread that trapped, inside its exception handler, so
        // register and memory reads describe that thread and the instruction is
        // still pending.
        _resumeGate.Wait();

        lock (_sync)
        {
            if (_state != DebuggerRunState.Terminated)
            {
                _state = DebuggerRunState.Running;
            }
        }

        Resumed?.Invoke(this, EventArgs.Empty);
    }

    void ICpuDebugHook.OnStall(ICpuDebugFrame frame, CpuStallInfo info)
    {
        if (!_options.BreakOnStall)
        {
            return;
        }

        DebugStopEvent? stop = null;
        lock (_sync)
        {
            if (_state == DebuggerRunState.Terminated)
            {
                return;
            }

            _currentFrame = frame;
            _state = DebuggerRunState.Paused;
            _lastStop = new DebugStopEvent(
                DebugStopReason.Stall,
                DebugRegisterFile.Capture(frame),
                frame.Kind,
                frame.Label,
                breakpoint: null,
                result: null,
                detail: info.Detail,
                opcodeBytes: ReadOpcodePreview(frame, info.InstructionPointer, 16),
                stallInfo: info);
            stop = _lastStop;
            _resumeGate.Reset();
        }

        Log.Debug($"Debugger stall stop: {info.Kind} nid={info.Nid} at 0x{info.InstructionPointer:X16}");
        Stopped?.Invoke(this, stop);
        _resumeGate.Wait();

        lock (_sync)
        {
            if (ReferenceEquals(_currentFrame, frame))
            {
                _currentFrame = null;
            }

            if (_state != DebuggerRunState.Terminated)
            {
                _state = DebuggerRunState.Running;
            }
        }

        Resumed?.Invoke(this, EventArgs.Empty);
    }

    private static DebugStopEvent BuildFaultStop(ICpuDebugFrame frame, OrbisGen2Result result)
    {
        var opcodeBytes = ReadOpcodePreview(frame, frame.Rip, 16);
        var detail = $"result={result}";
        if (opcodeBytes is not null)
        {
            detail += $", bytes={opcodeBytes}";
        }

        return new DebugStopEvent(
            DebugStopReason.Fault,
            DebugRegisterFile.Capture(frame),
            frame.Kind,
            frame.Label,
            breakpoint: null,
            result: result,
            detail: detail,
            opcodeBytes: opcodeBytes);
    }

    private static string? ReadOpcodePreview(ICpuDebugFrame frame, ulong address, int maxBytes)
    {
        Span<byte> buffer = stackalloc byte[maxBytes];
        var count = 0;
        for (; count < maxBytes; count++)
        {
            if (!frame.Memory.TryRead(address + (ulong)count, buffer.Slice(count, 1)))
            {
                break;
            }
        }

        return count == 0 ? null : Convert.ToHexString(buffer[..count]);
    }

    /// <summary>
    /// Signals that the whole guest run has finished. Releases any parked
    /// emulation thread and moves the session to
    /// <see cref="DebuggerRunState.Terminated"/>.
    /// </summary>
    public void NotifyTerminated()
    {
        lock (_sync)
        {
            _state = DebuggerRunState.Terminated;
            _currentFrame = null;
        }

        _resumeGate.Set();
        Terminated?.Invoke(this, EventArgs.Empty);
    }

    private DebugStopReason? ResolveStopReason(ICpuDebugFrame frame, bool firstFrame, out Breakpoint? breakpoint)
    {
        breakpoint = null;
        if (_pausePending)
        {
            _pausePending = false;
            return DebugStopReason.Pause;
        }

        if (_stepPending)
        {
            _stepPending = false;
            return DebugStopReason.Step;
        }

        var hit = Breakpoints.FindExecuteHit(frame.EntryPoint);
        if (hit is not null)
        {
            breakpoint = hit;
            return DebugStopReason.Breakpoint;
        }

        if (_options.StopAtEntry && firstFrame)
        {
            return DebugStopReason.EntryPoint;
        }

        return null;
    }

    public bool TryGetRegisters(out DebugRegisterFile registers)
    {
        lock (_sync)
        {
            if (!IsPausedWithFrame(out var frame))
            {
                registers = default;
                return false;
            }

            registers = DebugRegisterFile.Capture(frame);
            return true;
        }
    }

    public bool TrySetRegister(DebugRegisterId id, ulong value)
    {
        lock (_sync)
        {
            if (!IsPausedWithFrame(out var frame))
            {
                return false;
            }

            if (id.IsGeneralPurpose())
            {
                frame.SetRegister(id.ToCpuRegister(), value);
                return true;
            }

            switch (id)
            {
                case DebugRegisterId.Rip:
                    frame.Rip = value;
                    return true;
                case DebugRegisterId.Rflags:
                    frame.Rflags = value;
                    return true;
                default:
                    // FS/GS bases are owned by the TLS setup and are read-only here.
                    return false;
            }
        }
    }

    public bool TryReadMemory(ulong address, Span<byte> destination)
    {
        lock (_sync)
        {
            return IsPausedWithFrame(out var frame) && frame.Memory.TryRead(address, destination);
        }
    }

    public bool TryWriteMemory(ulong address, ReadOnlySpan<byte> source)
    {
        lock (_sync)
        {
            return IsPausedWithFrame(out var frame) && frame.Memory.TryWrite(address, source);
        }
    }

    public bool TryReadXmm(int registerIndex, out ulong low, out ulong high)
    {
        lock (_sync)
        {
            if (!IsPausedWithFrame(out var frame) || (uint)registerIndex >= 16)
            {
                low = 0;
                high = 0;
                return false;
            }

            frame.GetXmm(registerIndex, out low, out high);
            return true;
        }
    }

    public bool Continue()
    {
        lock (_sync)
        {
            if (_state != DebuggerRunState.Paused)
            {
                return false;
            }

            _resumeGate.Set();
            return true;
        }
    }

    public bool StepFrame()
    {
        lock (_sync)
        {
            if (_state != DebuggerRunState.Paused)
            {
                return false;
            }

            _stepPending = true;
            _resumeGate.Set();
            return true;
        }
    }

    public void RequestPause()
    {
        lock (_sync)
        {
            if (_state == DebuggerRunState.Running)
            {
                _pausePending = true;
            }
        }
    }

    private bool IsPausedWithFrame(out ICpuDebugFrame frame)
    {
        // Callers must hold _sync.
        if (_state == DebuggerRunState.Paused && _currentFrame is not null)
        {
            frame = _currentFrame;
            return true;
        }

        frame = null!;
        return false;
    }
}

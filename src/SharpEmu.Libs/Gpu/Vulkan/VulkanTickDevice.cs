// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace SharpEmu.Libs.Gpu.Vulkan;

// One transient command pool and one timeline semaphore on the presenter's queue.
internal sealed unsafe class VulkanTickDevice : IGpuTickDevice
{
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly Queue _queue;
    private readonly CommandPool _pool;
    private readonly VkSemaphore _timeline;

    public VulkanTickDevice(Vk vk, Device device, Queue queue, uint queueFamilyIndex, object queueGate, PhysicalDevice profilePhysicalDevice = default)
    {
        _vk = vk;
        _device = device;
        _queue = queue;
        QueueGate = queueGate;
        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = queueFamilyIndex,
            Flags = CommandPoolCreateFlags.TransientBit | CommandPoolCreateFlags.ResetCommandBufferBit,
        };
        RequireSuccess(_vk.CreateCommandPool(_device, &poolInfo, null, out _pool), "vkCreateCommandPool(scheduler)");
        var typeInfo = new SemaphoreTypeCreateInfo
        {
            SType = StructureType.SemaphoreTypeCreateInfo,
            SemaphoreType = SemaphoreType.Timeline,
            InitialValue = 0,
        };
        var createInfo = new SemaphoreCreateInfo
        {
            SType = StructureType.SemaphoreCreateInfo,
            PNext = &typeInfo,
        };
        RequireSuccess(_vk.CreateSemaphore(_device, &createInfo, null, out _timeline), "vkCreateSemaphore(scheduler timeline)");
        if (profilePhysicalDevice.Handle != 0)
            CommandProfile = new VulkanCommandProfile(vk, profilePhysicalDevice, device, queueFamilyIndex);
    }

    public VulkanCommandProfile? CommandProfile { get; }

    public object QueueGate { get; }

    public ulong TimelineHandle => _timeline.Handle;

    public ulong ReadTimeline()
    {
        ulong value;
        RequireSuccess(_vk.GetSemaphoreCounterValue(_device, _timeline, &value), "vkGetSemaphoreCounterValue");
        return value;
    }

    public bool TryWaitTimeline(ulong tick, out string failure)
    {
        var semaphore = _timeline;
        var waitInfo = new SemaphoreWaitInfo
        {
            SType = StructureType.SemaphoreWaitInfo,
            SemaphoreCount = 1,
            PSemaphores = &semaphore,
            PValues = &tick,
        };
        Result result;
        var diagStart = DiagWaitCallers ? System.Diagnostics.Stopwatch.GetTimestamp() : 0; // TEMP DIAG
        using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.GpuCompletionWait))
        {
            result = _vk.WaitSemaphores(_device, &waitInfo, ulong.MaxValue);
        }
        if (DiagWaitCallers) DiagRecordWait(System.Diagnostics.Stopwatch.GetTimestamp() - diagStart); // TEMP DIAG
        failure = result.ToString();
        return result == Result.Success;
    }

    // TEMP DIAG (SHARPEMU_DIAG_WAIT_CALLERS=1): wait time per caller chain, printed every 10 s.
    private static readonly bool DiagWaitCallers = Environment.GetEnvironmentVariable("SHARPEMU_DIAG_WAIT_CALLERS") == "1";
    private static readonly Dictionary<string, (long Ticks, long Count)> DiagWaits = new();
    private static long _diagWaitReport = System.Diagnostics.Stopwatch.GetTimestamp();

    private static void DiagRecordWait(long ticks)
    {
        var frames = new System.Diagnostics.StackTrace(2, false).GetFrames();
        var key = string.Join(" < ", frames.Take(6).Select(frame => frame.GetMethod() is { } method ? $"{method.DeclaringType?.Name}.{method.Name}" : "?"));
        lock (DiagWaits)
        {
            DiagWaits[key] = DiagWaits.TryGetValue(key, out var old) ? (old.Ticks + ticks, old.Count + 1) : (ticks, 1);
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now - _diagWaitReport < 10 * System.Diagnostics.Stopwatch.Frequency) return;
            _diagWaitReport = now;
            foreach (var (caller, value) in DiagWaits.OrderByDescending(pair => pair.Value.Ticks).Take(8))
            {
                Console.Error.WriteLine($"[DIAG][WAIT] ms={value.Ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F0} n={value.Count} {caller}");
            }

            DiagWaits.Clear();
        }
    }

    public nint[] AllocateBuffers(int count)
    {
        var allocateInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _pool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = (uint)count,
        };
        var buffers = new CommandBuffer[count];
        fixed (CommandBuffer* pointer = buffers)
        {
            RequireSuccess(_vk.AllocateCommandBuffers(_device, &allocateInfo, pointer), "vkAllocateCommandBuffers(scheduler)");
        }

        var handles = new nint[count];
        for (var i = 0; i < count; i++)
        {
            handles[i] = buffers[i].Handle;
        }

        return handles;
    }

    public void BeginBuffer(nint buffer)
    {
        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        RequireSuccess(_vk.BeginCommandBuffer(new CommandBuffer(buffer), &beginInfo), "vkBeginCommandBuffer(scheduler)");
        CommandProfile?.BeginBuffer(new CommandBuffer(buffer));
    }

    public void EndBuffer(nint buffer)
    {
        CommandProfile?.WriteMarker(new CommandBuffer(buffer), VulkanCommandProfile.IntervalKind.Tail);
        RequireSuccess(_vk.EndCommandBuffer(new CommandBuffer(buffer)), "vkEndCommandBuffer(scheduler)");
    }

    public bool TrySubmit(nint buffer, SubmitBundle bundle, out string failure)
    {
        var commandBuffer = new CommandBuffer(buffer);
        fixed (ulong* waitSemaphores = bundle.WaitSemaphores)
        fixed (ulong* waitTicks = bundle.WaitTicks)
        fixed (uint* waitStages = bundle.WaitStages)
        fixed (ulong* signalSemaphores = bundle.SignalSemaphores)
        fixed (ulong* signalTicks = bundle.SignalTicks)
        {
            var waitInfos = stackalloc SemaphoreSubmitInfo[bundle.WaitCount];
            for (var index = 0; index < bundle.WaitCount; index++)
            {
                waitInfos[index] = new SemaphoreSubmitInfo
                {
                    SType = StructureType.SemaphoreSubmitInfo,
                    Semaphore = new VkSemaphore(waitSemaphores[index]),
                    Value = waitTicks[index],
                    StageMask = (PipelineStageFlags2)waitStages[index],
                };
            }

            var signalInfos = stackalloc SemaphoreSubmitInfo[bundle.SignalCount];
            for (var index = 0; index < bundle.SignalCount; index++)
            {
                signalInfos[index] = new SemaphoreSubmitInfo
                {
                    SType = StructureType.SemaphoreSubmitInfo,
                    Semaphore = new VkSemaphore(signalSemaphores[index]),
                    Value = signalTicks[index],
                    StageMask = PipelineStageFlags2.AllCommandsBit,
                };
            }

            var commandInfo = new CommandBufferSubmitInfo
            {
                SType = StructureType.CommandBufferSubmitInfo,
                CommandBuffer = commandBuffer,
                DeviceMask = 1,
            };
            var submitInfo = new SubmitInfo2
            {
                SType = StructureType.SubmitInfo2,
                WaitSemaphoreInfoCount = (uint)bundle.WaitCount,
                PWaitSemaphoreInfos = waitInfos,
                CommandBufferInfoCount = 1,
                PCommandBufferInfos = &commandInfo,
                SignalSemaphoreInfoCount = (uint)bundle.SignalCount,
                PSignalSemaphoreInfos = signalInfos,
            };
            var result = _vk.QueueSubmit2(_queue, 1, &submitInfo, default);
            if (result == Result.Success)
                CommandProfile?.MarkSubmitted(buffer);
            failure = result == Result.ErrorDeviceLost ? $"{result} {DescribeDeviceFault()}" : result.ToString();
            return result == Result.Success;
        }
    }

    // VK_EXT_device_fault: the driver's record of the faulting addresses, when it keeps one.
    private string DescribeDeviceFault()
    {
        var name = System.Text.Encoding.ASCII.GetBytes("vkGetDeviceFaultInfoEXT\0");
        nint function;
        fixed (byte* namePointer = name)
        {
            function = (nint)_vk.GetDeviceProcAddr(_device, namePointer);
        }

        if (function == 0)
        {
            return "fault=unavailable";
        }

        var getFaultInfo = (delegate* unmanaged<Device, DeviceFaultCountsEXT*, DeviceFaultInfoEXT*, Result>)function;
        var counts = new DeviceFaultCountsEXT { SType = StructureType.DeviceFaultCountsExt };
        if (getFaultInfo(_device, &counts, null) is not (Result.Success or Result.Incomplete))
        {
            return "fault=unreadable";
        }

        var addresses = new DeviceFaultAddressInfoEXT[Math.Max(1u, counts.AddressInfoCount)];
        var vendors = new DeviceFaultVendorInfoEXT[Math.Max(1u, counts.VendorInfoCount)];
        counts.VendorBinarySize = 0;
        fixed (DeviceFaultAddressInfoEXT* addressPointer = addresses)
        fixed (DeviceFaultVendorInfoEXT* vendorPointer = vendors)
        {
            var info = new DeviceFaultInfoEXT
            {
                SType = StructureType.DeviceFaultInfoExt,
                PAddressInfos = addressPointer,
                PVendorInfos = vendorPointer,
            };
            getFaultInfo(_device, &counts, &info);
            var text = new System.Text.StringBuilder($"fault=\"{System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)info.Description)}\"");
            for (var index = 0; index < counts.AddressInfoCount; index++)
            {
                text.Append($" address[{index}]={addresses[index].AddressType}:0x{addresses[index].ReportedAddress:X}/0x{addresses[index].AddressPrecision:X}");
                text.Append(SharpEmu.Libs.Gpu.Buffers.GpuBuffer.DiagDescribeAddress(addresses[index].ReportedAddress)); // TEMP DIAG
                text.Append(SharpEmu.Libs.VideoOut.AddressBindingDiag.Describe(addresses[index].ReportedAddress & ~(addresses[index].AddressPrecision - 1))); // TEMP DIAG
            }

            for (var index = 0; index < counts.VendorInfoCount; index++)
            {
                fixed (byte* description = vendors[index].Description)
                {
                    text.Append($" vendor[{index}]=0x{vendors[index].VendorFaultCode:X}:0x{vendors[index].VendorFaultData:X} {System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)description)}");
                }
            }

            return text.ToString();
        }
    }

    public void Dispose()
    {
        CommandProfile?.Dispose();
        _vk.DestroySemaphore(_device, _timeline, null);
        _vk.DestroyCommandPool(_device, _pool, null);
    }

    private static void RequireSuccess(Result result, string operation)
    {
        if (result != Result.Success)
        {
            throw SubmissionScheduler.Fatal($"{operation} failed with {result}");
        }
    }
}

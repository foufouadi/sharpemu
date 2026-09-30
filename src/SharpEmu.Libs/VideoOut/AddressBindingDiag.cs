// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.VideoOut;

// TEMP DIAG (SHARPEMU_DIAG_ADDRESS_BINDING=1): VK_EXT_device_address_binding_report. Keeps every
// GPU virtual-address range the driver binds or unbinds (buffers, images, internal allocations),
// so a device-fault address can be named even when it is not a buffer device address.
internal static unsafe class AddressBindingDiag
{
    public const string ExtensionName = "VK_EXT_device_address_binding_report";
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_DIAG_ADDRESS_BINDING") == "1";

    private readonly record struct Binding(ulong Address, ulong Size, bool Bound, ObjectType Type, ulong Handle, string? Name, long Time, bool Internal);

    private static readonly object Gate = new();
    private static readonly List<Binding> Events = new();

    public static uint Callback(
        DebugUtilsMessageSeverityFlagsEXT severity,
        DebugUtilsMessageTypeFlagsEXT type,
        DebugUtilsMessengerCallbackDataEXT* data,
        void* userData)
    {
        if ((type & DebugUtilsMessageTypeFlagsEXT.DeviceAddressBindingBitExt) == 0)
        {
            return Vk.False;
        }

        for (var next = (BaseInStructure*)data->PNext; next != null; next = next->PNext)
        {
            if (next->SType != StructureType.DeviceAddressBindingCallbackDataExt)
            {
                continue;
            }

            var binding = (DeviceAddressBindingCallbackDataEXT*)next;
            var objectType = ObjectType.Unknown;
            ulong handle = 0;
            string? name = null;
            if (data->ObjectCount > 0)
            {
                objectType = data->PObjects[0].ObjectType;
                handle = data->PObjects[0].ObjectHandle;
                name = data->PObjects[0].PObjectName == null ? null : SilkMarshal.PtrToString((nint)data->PObjects[0].PObjectName);
            }

            lock (Gate)
            {
                Events.Add(new Binding(
                    binding->BaseAddress,
                    binding->Size,
                    binding->BindingType == DeviceAddressBindingTypeEXT.BindExt,
                    objectType,
                    handle,
                    name,
                    Environment.TickCount64,
                    (binding->Flags & DeviceAddressBindingFlagsEXT.InternalObjectBitExt) != 0));
            }
        }

        return Vk.False;
    }

    // Every bind/unbind whose range covers the address, oldest first (at most 40).
    public static string Describe(ulong address)
    {
        if (!Enabled)
        {
            return string.Empty;
        }

        var now = Environment.TickCount64;
        var text = new System.Text.StringBuilder();
        lock (Gate)
        {
            var hits = Events.Where(e => address >= e.Address && address < e.Address + e.Size).ToList();
            text.Append($" binding_events={Events.Count} covering={hits.Count}");
            foreach (var e in hits.Skip(Math.Max(0, hits.Count - 40)))
            {
                text.Append(
                    $"\n    [{(e.Bound ? "bind" : "UNBIND")} {now - e.Time} ms ago] va=0x{e.Address:X}+0x{e.Size:X} " +
                    $"{e.Type} 0x{e.Handle:X} internal={e.Internal} name={e.Name}");
            }

            // The ranges ending closest below the address (an overrun lands just past one).
            var below = Events.Where(e => e.Address + e.Size <= address)
                .OrderByDescending(e => e.Address + e.Size).ThenByDescending(e => e.Time).Take(8).ToList();
            foreach (var e in below)
            {
                text.Append(
                    $"\n    near-below end=0x{e.Address + e.Size:X} (gap 0x{address - (e.Address + e.Size):X}) [{(e.Bound ? "bind" : "UNBIND")} {now - e.Time} ms ago] " +
                    $"va=0x{e.Address:X}+0x{e.Size:X} {e.Type} 0x{e.Handle:X} internal={e.Internal} name={e.Name}");
            }

            var above = Events.Where(e => e.Address > address).OrderBy(e => e.Address).ThenByDescending(e => e.Time).Take(3).ToList();
            foreach (var e in above)
            {
                text.Append(
                    $"\n    near-above start=0x{e.Address:X} (gap 0x{e.Address - address:X}) [{(e.Bound ? "bind" : "UNBIND")} {now - e.Time} ms ago] " +
                    $"va=0x{e.Address:X}+0x{e.Size:X} {e.Type} 0x{e.Handle:X} internal={e.Internal} name={e.Name}");
            }
        }

        return text.ToString();
    }
}

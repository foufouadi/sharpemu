// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Images;

// Surface metadata (HTile, DCC, CMask, FMask) keyed by its guest address.
public sealed unsafe partial class GuestImageCache
{
    public bool IsMetadata(ulong address)
    {
        using var held = _lock.Hold();
        return _surfaceMetadata.TryGetValue(address, out var found) && found.Kind != SurfaceMetadataKind.PendingDcc;
    }

    public bool IsMetadataCleared(ulong address, uint slice, out uint fillValue)
    {
        fillValue = 0;
        using var held = _lock.Hold();
        if (!_surfaceMetadata.TryGetValue(address, out var found) || found.Kind == SurfaceMetadataKind.PendingDcc || slice >= 32)
        {
            return false;
        }

        fillValue = found.FillValue;
        return (found.ClearMask & (1u << (int)slice)) != 0;
    }

    public bool IsMetadataCleared(ulong address, uint slice) => IsMetadataCleared(address, slice, out _);

    // A broad clear applies to CMask, FMask and HTile; DCC needs a validated fill value.
    public bool ClearMetadata(ulong address)
    {
        using var held = _lock.Hold();
        if (!_surfaceMetadata.TryGetValue(address, out var found) || found.Kind is SurfaceMetadataKind.PendingDcc or SurfaceMetadataKind.Dcc)
        {
            return false;
        }

        found.ClearMask = uint.MaxValue;
        return true;
    }

    // True when registered DCC absorbed the fill and the guest dispatch can be skipped.
    public bool TryAbsorbDccFill(ulong address, ulong size, uint fillValue)
    {
        if (!IsValidRange(address, size))
        {
            throw SubmissionScheduler.Fatal($"The DCC fill range is invalid: address=0x{address:X16} size=0x{size:X16}.");
        }

        // A DCC fill repeats one byte code; only the known deferred-clear codes count as clear.
        var code = (byte)fillValue;
        var dccClearMask = fillValue != code * 0x01010101u ? 0u : code switch
        {
            0x00 or 0x20 or 0x40 or 0x80 or 0xc0 => uint.MaxValue,
            _ => 0u,
        };
        using var held = _lock.Hold();
        if (!_surfaceMetadata.TryGetValue(address, out var found))
        {
            // The fill may precede color-target discovery; a pending entry stays invisible until then.
            _surfaceMetadata.Add(address, new SurfaceMetadata { Kind = SurfaceMetadataKind.PendingDcc, ClearMask = dccClearMask, FillValue = fillValue, FillSize = size });
            return false;
        }

        if (found.Kind == SurfaceMetadataKind.PendingDcc)
        {
            found.ClearMask = dccClearMask;
            found.FillValue = fillValue;
            found.FillSize = size;
            return false;
        }

        if (found.Kind == SurfaceMetadataKind.Dcc)
        {
            found.ClearMask = dccClearMask;
            found.FillValue = fillValue;
            found.FillSize = size;
            return true;
        }

        return false;
    }

    // A shader read of a DCC-compressed surface decompresses through the metadata its descriptor
    // names, so a fast-cleared surface reads back its clear color. A surface cleared and then only
    // sampled never binds as a target again: the pending clear lands here, in command order before
    // the read. The register code (0x20) needs the target's clear words and stays with the target bind.
    public void ApplyPendingDccClear(ResourceSlotIdentifier imageIdentifier, ulong metadataAddress)
    {
        using var held = _lock.Hold();
        var image = _slots[imageIdentifier];
        if (image.Description.IsVolume || !image.Backing.Exists ||
            !_surfaceMetadata.TryGetValue(metadataAddress, out var metadata) ||
            metadata.Kind != SurfaceMetadataKind.Dcc ||
            !ImageRequestBuilders.IsFixedDccClearFormat(image.Description.PixelFormat) ||
            !ImageRequestBuilders.TryFixedDccClearValue((byte)metadata.FillValue, out var value))
        {
            return;
        }

        // Only the slices the image holds; the fill also covers slices of a larger surface.
        var layers = Math.Min(image.Backing.Layers, 32u);
        var sliceMask = layers == 32 ? uint.MaxValue : (1u << (int)layers) - 1;
        if ((metadata.ClearMask & sliceMask) == 0)
        {
            return;
        }

        var command = _scheduler.Current;
        if (command.IsInvalid)
        {
            throw SubmissionScheduler.Fatal($"A pending DCC clear has no command buffer: address=0x{image.Description.Data.Address:X16}.");
        }

        command.EndRendering();
        var native = new CommandBuffer(command.Handle);
        for (var layer = 0u; layer < layers;)
        {
            if ((metadata.ClearMask & (1u << (int)layer)) == 0)
            {
                layer++;
                continue;
            }

            var first = layer;
            while (layer < layers && (metadata.ClearMask & (1u << (int)layer)) != 0)
            {
                metadata.ClearMask &= ~(1u << (int)layer);
                layer++;
            }

            var range = new SubresourceRange(0, image.Backing.MipLevels, first, layer - first);
            image.Transition(ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit, range, native);
            var vkRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, image.Backing.MipLevels, first, layer - first);
            _device.Vk.CmdClearColorImage(native, image.Backing.Handle, ImageLayout.TransferDstOptimal, &value, 1, &vkRange);
        }

        WatchImage(imageIdentifier);
        TakeGpuOwnership(image);
    }

    public bool SetMetadataSlice(ulong address, uint slice, bool isClear)
    {
        using var held = _lock.Hold();
        if (!_surfaceMetadata.TryGetValue(address, out var found) || found.Kind == SurfaceMetadataKind.PendingDcc || slice >= 32)
        {
            return false;
        }

        if (isClear)
        {
            found.ClearMask |= 1u << (int)slice;
        }
        else
        {
            found.ClearMask &= ~(1u << (int)slice);
        }

        return true;
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

// Zero limits preserve compact bindings. Admission depends only on the immutable plan and host
// limits, never on descriptor equality or the current image formats. Samplers still coalesce.
public readonly record struct StableImageDescriptorLimits(
    uint SampledImages, uint StorageImages, uint Samplers, uint StorageBuffers, uint Resources)
{
    internal bool Fits(ShaderResourcePlan plan)
    {
        var info = plan.Info;
        // The caller passes the stage's own budget: graphics stages share a set when a pipeline is built whole
        // (ShaderDescriptorLimits.GraphicsImageLimits). Linked, heap and candidate layouts need a separate
        // bound on their full demand; dynamic storage images can add a descriptor for every mip.
        if (Resources == 0 || info.Images.Count == 0 ||
            plan.RequiresSpecializationMemory || plan.IndirectImages.Count != 0 || plan.BufferCandidateTables.Count != 0 ||
            info.UsesBindlessHeap || info.UsesCallRecords || info.UsesCallMarks ||
            Gen5ShaderCallLinker.IsLinked(plan.Graph.Program) ||
            plan.Graph.Program.Instructions.Any(instruction => instruction.Opcode is "SSwappcB64" or "SCallB64") ||
            plan.DescriptorSources.Any(source => source.IndirectImage is not null) ||
            info.Images.Any(image => image.IndirectRoot != DescriptorConstants.NoIndex ||
                image.MipMode != ImageMipMode.None || image.MipCount != 1 ||
                image.ResourceClass is not (ImageResourceClass.Sampled or ImageResourceClass.Storage)))
            return false;

        ulong sampled = (ulong)info.Images.Count(image => image.ResourceClass == ImageResourceClass.Sampled);
        ulong storage = (ulong)info.Images.Count - sampled;
        // Point-filter copies and then depth-compare copies can each double the sampler count.
        ulong samplers = (ulong)info.Samplers.Count * 4;
        // Reserve all optional ordinary helpers: flattened table, shader data and mip statistics.
        ulong buffers = (ulong)info.Buffers.Count + 3;
        if (info.UsesDeviceAddresses) buffers += 2; // Page table and fault buffer.
        if (info.UsesDeviceAddressFallback) buffers++;
        if (BindingLayout.UsesGlobalDataShare(plan.Graph.Program)) buffers++;
        return sampled <= SampledImages && storage <= StorageImages && samplers <= Samplers &&
            buffers <= StorageBuffers && sampled + storage + buffers <= Resources;
    }

    // Whether a plan that is not stable can still give each of its own images a binding instead of grouping equal
    // descriptors. Indirect candidates and runtime tables are not known yet: they keep their own bindings either way,
    // so the plan's own images, a binding per mip where a mip is chosen at run time, take at most half of each limit.
    internal bool FitsEachImage(ShaderResourcePlan plan)
    {
        var info = plan.Info;
        if (Resources == 0 || info.Images.Count < 2 || Gen5ShaderCallLinker.IsLinked(plan.Graph.Program)) return false;
        ulong sampled = 0, storage = 0;
        foreach (var image in info.Images)
        {
            if (image.IndirectRoot != DescriptorConstants.NoIndex) continue;
            ulong descriptors = image.MipMode == ImageMipMode.None ? 1UL : Math.Max(1UL, image.MipCount);
            if (image.ResourceClass == ImageResourceClass.Sampled) sampled += descriptors;
            else storage += descriptors;
        }

        ulong buffers = (ulong)info.Buffers.Count + 6;
        return sampled <= SampledImages / 2 && storage <= StorageImages / 2 &&
            sampled + storage + buffers <= Resources / 2;
    }
}

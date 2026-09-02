// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Stubs;

/// <summary>
/// Success stubs for trophy, character-encoding and telemetry ABI calls that
/// Void Terrarium (and other titles) invoke during startup. They were
/// previously unresolved and returned NOT_FOUND, and a title that gates its
/// UI/text initialization on these succeeding then skips ahead and never draws
/// its content (a black screen with only a clear pass). These return success
/// (and a non-zero handle where an out pointer is expected) so init proceeds.
/// </summary>
public static class GameServiceStubs
{
    private sealed class VoicePortState
    {
        public int PortType { get; init; } = -1;
        public uint Bitrate { get; set; } = 48_000;
        public float Volume { get; set; } = 1.0f;
        public ushort EdgeCount { get; set; }
    }

    private static readonly object VoiceSync = new();
    // Shared, never written: the source of the silence a read yields.
    private static readonly byte[] VoiceSilence = new byte[4096];
    private const uint MaxVoiceReadBytes = 1u << 20;
    // Size the port-info structure is read and written at. Not derived from a
    // published ABI; it is the extent this stub touches.
    private const int VoicePortInfoBytes = 32;
    private static readonly Dictionary<uint, VoicePortState> VoicePorts = new();
    private static uint NextVoicePort = 1;
    private static bool VoiceStarted;

    private static int Ok(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    // Writes a small non-zero handle to the pointer in the given register so
    // the caller treats the object as created; returns success.
    private static int OkWithHandle(CpuContext ctx, CpuRegister outPointerRegister)
    {
        var outAddress = ctx[outPointerRegister];
        if (outAddress != 0)
        {
            Span<byte> handle = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(handle, 1);
            _ = ctx.Memory.TryWrite(outAddress, handle);
        }

        return Ok(ctx);
    }

    // ---- NpTrophy2: trophy context/handle registration at boot ----
    public static int NpTrophy2CreateContext(CpuContext ctx) => OkWithHandle(ctx, CpuRegister.Rdi);
    public static int NpTrophy2CreateHandle(CpuContext ctx) => OkWithHandle(ctx, CpuRegister.Rdi);
    public static int NpTrophy2RegisterContext(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "4IzqhhUQ3nk", ExportName = "sceNpTrophy2GetGameInfo",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2GetGameInfo(CpuContext ctx) => Ok(ctx);

    // ---- CES: Shift-JIS <-> Unicode conversion setup (Japanese text) ----

    [SysAbiExport(Nid = "ZiDCxUUGbec", ExportName = "sceCesUcsProfileInitSJis1997Cp932",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceLibcInternal")]
    public static int CesUcsProfileInitSJis1997Cp932(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "538bRGc6Zo8", ExportName = "sceCesMbcsUcsContextInit",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceLibcInternal")]
    public static int CesMbcsUcsContextInit(CpuContext ctx) => Ok(ctx);

    // ---- NpUniversalDataSystem: gameplay telemetry events ----
    public static int NpUniversalDataSystemCreateEvent(CpuContext ctx) => OkWithHandle(ctx, CpuRegister.Rdi);
    public static int NpUniversalDataSystemPostEvent(CpuContext ctx) => Ok(ctx);
    public static int NpUniversalDataSystemDestroyEvent(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "0HBYxYAjmf0", ExportName = "sceNpGameIntentTerminate",
        Target = Generation.Gen5, LibraryName = "libSceNpGameIntent")]
    public static int NpGameIntentTerminate(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "jqb7HntFQFc", ExportName = "sceWebBrowserDialogInitialize",
        Target = Generation.Gen5, LibraryName = "libSceWebBrowserDialog")]
    public static int WebBrowserDialogInitialize(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "ocHtyBwHfys", ExportName = "sceWebBrowserDialogTerminate",
        Target = Generation.Gen5, LibraryName = "libSceWebBrowserDialog")]
    public static int WebBrowserDialogTerminate(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "kvYEw2lBndk", ExportName = "sceGameLiveStreamingInitialize",
        Target = Generation.Gen5, LibraryName = "libSceGameLiveStreaming")]
    public static int GameLiveStreamingInitialize(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "isruqthpYcw", ExportName = "sceSharePlayInitialize",
        Target = Generation.Gen5, LibraryName = "libSceSharePlay")]
    public static int SharePlayInitialize(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "0IL1keINExQ", ExportName = "sceShareTerminate",
        Target = Generation.Gen5, LibraryName = "libSceShareUtility")]
    public static int ShareTerminate(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "YBiIdcDPrxs", ExportName = "sceShareFeaturePermit",
        Target = Generation.Gen5, LibraryName = "libSceShareUtility")]
    public static int ShareFeaturePermit(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "9TrhuGzberQ", ExportName = "sceVoiceInit",
        Target = Generation.Gen5, LibraryName = "libSceVoice")]
    public static int VoiceInit(CpuContext ctx)
    {
        lock (VoiceSync)
        {
            VoicePorts.Clear();
            NextVoicePort = 1;
            VoiceStarted = false;
        }

        return Ok(ctx);
    }

    [SysAbiExport(Nid = "clyKUyi3RYU", ExportName = "sceVoiceSetThreadsParams",
        Target = Generation.Gen5, LibraryName = "libSceVoice")]
    public static int VoiceSetThreadsParams(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "nXpje5yNpaE", ExportName = "sceVoiceCreatePort",
        Target = Generation.Gen5, LibraryName = "libSceVoice")]
    public static int VoiceCreatePort(CpuContext ctx)
    {
        var outAddress = ctx[CpuRegister.Rdi];
        if (outAddress == 0)
        {
            return Ok(ctx);
        }

        var paramAddress = ctx[CpuRegister.Rsi];
        var portType = -1;
        var bitrate = 48_000u;
        var volume = 1.0f;
        if (paramAddress != 0)
        {
            Span<byte> param = stackalloc byte[16];
            if (ctx.Memory.TryRead(paramAddress, param))
            {
                portType = BinaryPrimitives.ReadInt32LittleEndian(param);
                volume = BitConverter.Int32BitsToSingle(
                    BinaryPrimitives.ReadInt32LittleEndian(param[8..]));
                if (volume <= 0.0f || float.IsNaN(volume))
                {
                    volume = 1.0f;
                }

                if (portType == 2)
                {
                    var requestedBitrate =
                        BinaryPrimitives.ReadInt32LittleEndian(param[12..]);
                    if (requestedBitrate > 0)
                    {
                        bitrate = unchecked((uint)requestedBitrate);
                    }
                }
            }
        }

        uint portId;
        lock (VoiceSync)
        {
            // Port ids are a byte on the guest side, so the counter wraps.
            // Skip ids still held by a live port: reusing one would silently
            // alias two ports onto the same state.
            if (VoicePorts.Count >= 0xfe)
            {
                return Ok(ctx);
            }

            do
            {
                portId = NextVoicePort++;
                if (NextVoicePort >= 0xff)
                {
                    NextVoicePort = 1;
                }
            }
            while (VoicePorts.ContainsKey(portId));

            VoicePorts[portId] = new VoicePortState
            {
                PortType = portType,
                Bitrate = bitrate,
                Volume = volume,
            };
        }

        Span<byte> result = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(result, portId);
        _ = ctx.Memory.TryWrite(outAddress, result);
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "b7kJI+nx2hg", ExportName = "sceVoiceDeletePort",
        Target = Generation.Gen5, LibraryName = "libSceVoice")]
    public static int VoiceDeletePort(CpuContext ctx)
    {
        lock (VoiceSync)
        {
            VoicePorts.Remove(unchecked((uint)ctx[CpuRegister.Rdi]));
        }

        return Ok(ctx);
    }

    [SysAbiExport(Nid = "oV9GAdJ23Gw", ExportName = "sceVoiceConnectIPortToOPort",
        Target = Generation.Gen5, LibraryName = "libSceVoice")]
    public static int VoiceConnectIPortToOPort(CpuContext ctx)
    {
        lock (VoiceSync)
        {
            if (VoicePorts.TryGetValue(unchecked((uint)ctx[CpuRegister.Rdi]), out var input))
            {
                input.EdgeCount = 1;
            }

            if (VoicePorts.TryGetValue(unchecked((uint)ctx[CpuRegister.Rsi]), out var output))
            {
                output.EdgeCount = 1;
            }
        }

        return Ok(ctx);
    }

    [SysAbiExport(Nid = "ajVj3QG2um4", ExportName = "sceVoiceDisconnectIPortFromOPort",
        Target = Generation.Gen5, LibraryName = "libSceVoice")]
    public static int VoiceDisconnectIPortFromOPort(CpuContext ctx)
    {
        lock (VoiceSync)
        {
            if (VoicePorts.TryGetValue(unchecked((uint)ctx[CpuRegister.Rdi]), out var input))
            {
                input.EdgeCount = 0;
            }

            if (VoicePorts.TryGetValue(unchecked((uint)ctx[CpuRegister.Rsi]), out var output))
            {
                output.EdgeCount = 0;
            }
        }

        return Ok(ctx);
    }

    [SysAbiExport(Nid = "Oo0S5PH7FIQ", ExportName = "sceVoiceEnd",
        Target = Generation.Gen5, LibraryName = "libSceVoice")]
    public static int VoiceEnd(CpuContext ctx)
    {
        lock (VoiceSync)
        {
            VoicePorts.Clear();
            NextVoicePort = 1;
            VoiceStarted = false;
        }

        return Ok(ctx);
    }

    // GTA V starts the voice subsystem after creating its audio ports. Kyty
    // exposes this as a side-effect-free success path when no host voice
    // backend is present; keeping the NID registered prevents the import
    // resolver from falling back to an unresolved call.
    [SysAbiExport(Nid = "54phPH2LZls", ExportName = "sceVoiceStart",
        Target = Generation.Gen5, LibraryName = "libSceVoice")]
    public static int VoiceStart(CpuContext ctx)
    {
        lock (VoiceSync)
        {
            VoiceStarted = true;
        }

        return Ok(ctx);
    }

    [SysAbiExport(Nid = "Ao2YNSA7-Qo", ExportName = "sceVoiceStop",
        Target = Generation.Gen5, LibraryName = "libSceVoice")]
    public static int VoiceStop(CpuContext ctx)
    {
        lock (VoiceSync)
        {
            VoiceStarted = false;
        }

        return Ok(ctx);
    }

    [SysAbiExport(Nid = "cQ6DGsQEjV4", ExportName = "sceVoiceReadFromOPort",
        Target = Generation.Gen5, LibraryName = "libSceVoice")]
    public static int VoiceReadFromOPort(CpuContext ctx)
    {
        var sizeAddress = ctx[CpuRegister.Rdx];
        if (sizeAddress == 0)
        {
            return Ok(ctx);
        }

        Span<byte> sizeBytes = stackalloc byte[sizeof(uint)];
        if (ctx.Memory.TryRead(sizeAddress, sizeBytes))
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(sizeBytes);
            var dataAddress = ctx[CpuRegister.Rsi];
            if (dataAddress != 0 && size != 0)
            {
                // No host capture backend, so the port yields silence. Written
                // from a shared zero buffer in chunks rather than one allocation
                // per call: this runs at audio rate.
                var remaining = Math.Min(size, MaxVoiceReadBytes);
                var offset = 0u;
                while (remaining != 0)
                {
                    var chunk = (int)Math.Min(remaining, (uint)VoiceSilence.Length);
                    if (!ctx.Memory.TryWrite(
                            dataAddress + offset,
                            VoiceSilence.AsSpan(0, chunk)))
                    {
                        break;
                    }

                    offset += (uint)chunk;
                    remaining -= (uint)chunk;
                }
            }

            BinaryPrimitives.WriteUInt32LittleEndian(sizeBytes, 0);
            _ = ctx.Memory.TryWrite(sizeAddress, sizeBytes);
        }

        return Ok(ctx);
    }

    [SysAbiExport(Nid = "YeJl6yDlhW0", ExportName = "sceVoiceWriteToIPort",
        Target = Generation.Gen5, LibraryName = "libSceVoice")]
    public static int VoiceWriteToIPort(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "CrLqDwWLoXM", ExportName = "sceVoiceGetPortInfo",
        Target = Generation.Gen5, LibraryName = "libSceVoice")]
    public static int VoiceGetPortInfo(CpuContext ctx)
    {
        var infoAddress = ctx[CpuRegister.Rsi];
        if (infoAddress == 0)
        {
            return Ok(ctx);
        }

        var portId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var portType = -1;
        ushort edgeCount = 0;
        bool started;
        lock (VoiceSync)
        {
            if (VoicePorts.TryGetValue(portId, out var port))
            {
                portType = port.PortType;
                edgeCount = port.EdgeCount;
            }

            started = VoiceStarted;
        }

        // Only the fields this stub actually models are written. The rest of
        // the structure is read back and returned unchanged rather than zeroed:
        // its layout is not established here, and overwriting fields whose
        // meaning is unknown is more likely to mislead a caller than leaving
        // what it already had. A read failure also validates the pointer, which
        // a partial guard around a single field could not do.
        Span<byte> info = stackalloc byte[VoicePortInfoBytes];
        if (!ctx.Memory.TryRead(infoAddress, info))
        {
            return Ok(ctx);
        }

        BinaryPrimitives.WriteInt32LittleEndian(info, portType);
        BinaryPrimitives.WriteInt32LittleEndian(info[4..], started ? 1 : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(info[16..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(info[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(info[24..], edgeCount);
        _ = ctx.Memory.TryWrite(infoAddress, info);
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "elcxZTEfHZM", ExportName = "sceVoiceGetPortAttr",
        Target = Generation.Gen5, LibraryName = "libSceVoice")]
    public static int VoiceGetPortAttr(CpuContext ctx)
    {
        var valueAddress = ctx[CpuRegister.Rdx];
        var size = unchecked((int)ctx[CpuRegister.Rcx]);
        if (valueAddress == 0 || size <= 0 || size > 1 << 20)
        {
            return Ok(ctx);
        }

        var value = new byte[size];
        if (unchecked((int)ctx[CpuRegister.Rsi]) == 1001 && size >= sizeof(bool))
        {
            value[0] = 1;
        }

        _ = ctx.Memory.TryWrite(valueAddress, value);
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "cJLufzou6bc", ExportName = "sceVoiceGetBitRate",
        Target = Generation.Gen5, LibraryName = "libSceVoice")]
    public static int VoiceGetBitRate(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rsi];
        if (outputAddress == 0)
        {
            return Ok(ctx);
        }

        var bitrate = 48_000u;
        lock (VoiceSync)
        {
            if (VoicePorts.TryGetValue(unchecked((uint)ctx[CpuRegister.Rdi]), out var port))
            {
                bitrate = port.Bitrate;
            }
        }

        Span<byte> value = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(value, bitrate);
        _ = ctx.Memory.TryWrite(outputAddress, value);
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "QBFoAIjJoXQ", ExportName = "sceVoiceSetVolume",
        Target = Generation.Gen5, LibraryName = "libSceVoice")]
    public static int VoiceSetVolume(CpuContext ctx)
    {
        lock (VoiceSync)
        {
            if (VoicePorts.TryGetValue(unchecked((uint)ctx[CpuRegister.Rdi]), out var port))
            {
                port.Volume = BitConverter.Int32BitsToSingle(unchecked((int)ctx[CpuRegister.Rsi]));
            }
        }

        return Ok(ctx);
    }

    [SysAbiExport(Nid = "jjkCjneOYSs", ExportName = "sceVoiceGetVolume",
        Target = Generation.Gen5, LibraryName = "libSceVoice")]
    public static int VoiceGetVolume(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rsi];
        if (outputAddress == 0)
        {
            return Ok(ctx);
        }

        var volume = 1.0f;
        lock (VoiceSync)
        {
            if (VoicePorts.TryGetValue(unchecked((uint)ctx[CpuRegister.Rdi]), out var port))
            {
                volume = port.Volume;
            }
        }

        Span<byte> value = stackalloc byte[sizeof(float)];
        BinaryPrimitives.WriteInt32LittleEndian(
            value, BitConverter.SingleToInt32Bits(volume));
        _ = ctx.Memory.TryWrite(outputAddress, value);
        return Ok(ctx);
    }

    [SysAbiExport(Nid = "dPj4ZtRcIWk", ExportName = "sceContentSearchInit",
        Target = Generation.Gen5, LibraryName = "libSceContentSearch")]
    public static int ContentSearchInit(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "zoxb0wEChEM", ExportName = "sceContentDeleteInitialize",
        Target = Generation.Gen5, LibraryName = "libSceContentDelete")]
    public static int ContentDeleteInitialize(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "Fc8qxlKINYQ", ExportName = "sceVideoRecordingSetInfo",
        Target = Generation.Gen5, LibraryName = "libSceVideoRecording")]
    public static int VideoRecordingSetInfo(CpuContext ctx) => Ok(ctx);

    // Captured from GTA V Enhanced (PPSA04264); not in the public NID catalog.
    // Side-effect-free success — same as unresolved stub behavior that kept boot
    // moving; reverse the ABI before writing guest memory.
    #pragma warning disable SHEM006
    [SysAbiExport(Nid = "Ikfdt-rIqCE", ExportName = "sceUnknownIkfdt",
        Target = Generation.Gen5, LibraryName = "libKernel")]
    public static int UnknownIkfdt(CpuContext ctx) => Ok(ctx);
    #pragma warning restore SHEM006
}

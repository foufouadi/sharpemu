// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using FFmpeg.AutoGen;

namespace SharpEmu.Libs.Codec;

/// <summary>
/// Owns one FFmpeg H.264 decode session for a single sceVideodec2 decoder
/// handle. Ghost of Yotei demuxes its own .bsf container and hands
/// Videodec2Exports.Videodec2Decode pre-demuxed Annex-B access units
/// directly (no file to open, no container to read) -- so unlike
/// upstream/main's FfmpegNativeBinkFrameSource (which owns an
/// AVFormatContext and demuxes a file), this only ever opens a bare
/// AVCodecContext for AV_CODEC_ID_H264 and feeds it packets the caller
/// already has in guest memory.
/// </summary>
internal sealed unsafe class Videodec2Decoder : IDisposable
{
    // Originally assumed NV12 (the PS5 hardware decoder's real output
    // format) on the theory the game's own compute shaders would consume it
    // from a guest buffer -- proven wrong by live evidence: the "output"
    // struct sceVideodec2Decode hands SharpEmu (rdx, ptr+size at +0x08/+0x10)
    // is a FIXED 4096-byte guest allocation on every single call, orders of
    // magnitude too small for any real frame (1920x1080 NV12 needs
    // 3,110,400 bytes) -- it is a placeholder/scratch struct the game
    // pre-populates, not a real per-frame pixel target. Bink2 in this same
    // codebase (Bink2MovieBridge / VulkanVideoPresenter.Submit) already
    // solves this identical problem by decoding straight to a host-side
    // BGRA buffer and handing it to VulkanVideoPresenter.Submit for direct
    // presentation, bypassing guest memory/the game's own compositor
    // entirely -- sceVideodec2Decode now does the same, hence BGRA (what
    // Submit requires) instead of NV12.
    private const AVPixelFormat OutputPixelFormat = AVPixelFormat.AV_PIX_FMT_BGRA;

    private static bool _rootPathInitialized;
    private static readonly object InitGate = new();

    private readonly object _gate = new();
    private AVCodecContext* _codecContext;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private SwsContext* _swsContext;
    private int _swsSourceWidth;
    private int _swsSourceHeight;
    private AVPixelFormat _swsSourceFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    private bool _disposed;

    private Videodec2Decoder(AVCodecContext* codecContext, AVFrame* frame, AVPacket* packet)
    {
        _codecContext = codecContext;
        _frame = frame;
        _packet = packet;
    }

    /// <summary>
    /// Opens a new H.264 decode session, or returns null if FFmpeg is
    /// unavailable (libraries missing/failed to load) or the decoder
    /// couldn't be opened -- callers treat null the same as any other
    /// sceVideodec2CreateDecoder failure, falling back to the pre-existing
    /// "no picture" stub behavior rather than crashing.
    /// </summary>
    public static Videodec2Decoder? TryCreate()
    {
        EnsureRootPathInitialized();

        AVCodecContext* codecContext = null;
        AVFrame* frame = null;
        AVPacket* packet = null;
        try
        {
            var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
            if (codec == null)
            {
                return null;
            }

            codecContext = ffmpeg.avcodec_alloc_context3(codec);
            if (codecContext == null)
            {
                return null;
            }

            if (ffmpeg.avcodec_open2(codecContext, codec, null) < 0)
            {
                ffmpeg.avcodec_free_context(&codecContext);
                return null;
            }

            frame = ffmpeg.av_frame_alloc();
            packet = ffmpeg.av_packet_alloc();
            if (frame == null || packet == null)
            {
                if (frame != null)
                {
                    ffmpeg.av_frame_free(&frame);
                }

                if (packet != null)
                {
                    ffmpeg.av_packet_free(&packet);
                }

                ffmpeg.avcodec_free_context(&codecContext);
                return null;
            }

            return new Videodec2Decoder(codecContext, frame, packet);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException)
        {
            // FFmpeg's native libraries are optional infrastructure (see
            // SharpEmu.CLI.csproj's FetchFfmpegRuntime/PublishFfmpegRuntime);
            // a build that skipped or failed that download must degrade to
            // the stub behavior, not crash the emulator.
            if (codecContext != null)
            {
                ffmpeg.avcodec_free_context(&codecContext);
            }

            return null;
        }
    }

    private static void EnsureRootPathInitialized()
    {
        if (_rootPathInitialized)
        {
            return;
        }

        lock (InitGate)
        {
            if (_rootPathInitialized)
            {
                return;
            }

            _rootPathInitialized = true;
            // SharpEmu.CLI.csproj publishes FFmpeg's shared libraries into a
            // "plugins" subfolder next to the executable rather than flat
            // beside it (NativeLibraryFolderName). Same layout and same
            // re-Initialize()-after-RootPath dance as upstream/main's
            // FfmpegNativeBinkFrameSource: ffmpeg's static constructor binds
            // against the default (empty) RootPath on first touch -- which
            // is the RootPath assignment itself -- so every function
            // resolved during that first pass would otherwise permanently
            // throw NotSupportedException.
            ffmpeg.RootPath = Path.Combine(AppContext.BaseDirectory, "plugins");
            DynamicallyLoadedBindings.Initialize();
        }
    }

    /// <summary>
    /// Feeds one Annex-B access unit to the decoder and, if a picture is
    /// ready, converts it to BGRA (what VulkanVideoPresenter.Submit expects,
    /// the same host-side-buffer path Bink2 already uses for direct
    /// presentation) into a freshly allocated <paramref name="bgraFrame"/>.
    /// Returns false on a hard decode error (caller should treat like any
    /// other decode failure); a true result with <paramref name="hasPicture"/>
    /// false is the normal "fed the packet, nothing to output yet" case the
    /// existing stub already handled correctly (H.264 buffers several
    /// frames of reordering delay before the first picture comes out).
    /// </summary>
    public bool TryDecode(
        ReadOnlySpan<byte> accessUnit,
        out byte[]? bgraFrame,
        out bool hasPicture,
        out uint width,
        out uint height)
    {
        bgraFrame = null;
        hasPicture = false;
        width = 0;
        height = 0;

        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            ffmpeg.av_packet_unref(_packet);
            var buffer = ffmpeg.av_malloc((nuint)accessUnit.Length + (nuint)ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE);
            if (buffer == null)
            {
                return false;
            }

            fixed (byte* source = accessUnit)
            {
                Buffer.MemoryCopy(source, buffer, accessUnit.Length, accessUnit.Length);
            }

            new Span<byte>((byte*)buffer + accessUnit.Length, ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE).Clear();

            _packet->data = (byte*)buffer;
            _packet->size = accessUnit.Length;

            var sendResult = ffmpeg.avcodec_send_packet(_codecContext, _packet);
            ffmpeg.av_freep(&buffer);
            _packet->data = null;
            _packet->size = 0;
            if (sendResult < 0 && sendResult != ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                return false;
            }

            var receiveResult = ffmpeg.avcodec_receive_frame(_codecContext, _frame);
            if (receiveResult == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receiveResult == ffmpeg.AVERROR_EOF)
            {
                // No picture yet -- normal mid-stream state, not a failure.
                return true;
            }

            if (receiveResult < 0)
            {
                return false;
            }

            try
            {
                bgraFrame = ConvertFrameToBgraLocked(out width, out height);
                if (bgraFrame == null)
                {
                    return false;
                }

                hasPicture = true;
                return true;
            }
            finally
            {
                ffmpeg.av_frame_unref(_frame);
            }
        }
    }

    /// <summary>
    /// sceVideodec2Flush's counterpart to TryDecode: signals end-of-stream
    /// to the decoder (H.264 buffers several frames of reordering delay, so
    /// there can be pictures still queued up with no more AUs coming) and
    /// pulls one of the remaining frames out, if any. Safe to call
    /// repeatedly -- once fully drained, avcodec_receive_frame keeps
    /// returning AVERROR_EOF and this just reports "no picture" each time,
    /// matching the pre-existing stub's Flush behavior.
    /// </summary>
    public bool TryDrain(out byte[]? bgraFrame, out bool hasPicture, out uint width, out uint height)
    {
        bgraFrame = null;
        hasPicture = false;
        width = 0;
        height = 0;

        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            var sendResult = ffmpeg.avcodec_send_packet(_codecContext, null);
            if (sendResult < 0 && sendResult != ffmpeg.AVERROR_EOF)
            {
                return false;
            }

            var receiveResult = ffmpeg.avcodec_receive_frame(_codecContext, _frame);
            if (receiveResult == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receiveResult == ffmpeg.AVERROR_EOF)
            {
                return true;
            }

            if (receiveResult < 0)
            {
                return false;
            }

            try
            {
                bgraFrame = ConvertFrameToBgraLocked(out width, out height);
                if (bgraFrame == null)
                {
                    return false;
                }

                hasPicture = true;
                return true;
            }
            finally
            {
                ffmpeg.av_frame_unref(_frame);
            }
        }
    }

    /// <summary>
    /// Converts <see cref="_frame"/> to a tightly packed BGRA buffer sized
    /// exactly width*height*4 -- what VulkanVideoPresenter.Submit requires
    /// (it rejects anything else, see its own length check). Returns null
    /// on any FFmpeg failure.
    /// </summary>
    private byte[]? ConvertFrameToBgraLocked(out uint width, out uint height)
    {
        width = (uint)_frame->width;
        height = (uint)_frame->height;
        var sourceFormat = (AVPixelFormat)_frame->format;

        if (_swsContext == null ||
            _swsSourceWidth != _frame->width ||
            _swsSourceHeight != _frame->height ||
            _swsSourceFormat != sourceFormat)
        {
            if (_swsContext != null)
            {
                ffmpeg.sws_freeContext(_swsContext);
            }

            _swsContext = ffmpeg.sws_getContext(
                _frame->width, _frame->height, sourceFormat,
                _frame->width, _frame->height, OutputPixelFormat,
                ffmpeg.SWS_BILINEAR, null, null, null);
            if (_swsContext == null)
            {
                return null;
            }

            _swsSourceWidth = _frame->width;
            _swsSourceHeight = _frame->height;
            _swsSourceFormat = sourceFormat;
        }

        var bgraFrame = new byte[checked((int)(width * height * 4))];
        fixed (byte* destinationPtr = bgraFrame)
        {
            var dstData = new byte_ptrArray4();
            var dstLinesize = new int_array4();
            ffmpeg.av_image_fill_arrays(
                ref dstData, ref dstLinesize, destinationPtr,
                OutputPixelFormat, _frame->width, _frame->height, 1);

            var srcData = new byte_ptrArray8();
            var srcLinesize = new int_array8();
            for (var i = 0; i < 4; i++)
            {
                srcData[(uint)i] = _frame->data[(uint)i];
                srcLinesize[(uint)i] = _frame->linesize[(uint)i];
            }

            ffmpeg.sws_scale(
                _swsContext, srcData, srcLinesize, 0, _frame->height,
                dstData, dstLinesize);
        }

        return bgraFrame;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_swsContext != null)
            {
                ffmpeg.sws_freeContext(_swsContext);
                _swsContext = null;
            }

            if (_packet != null)
            {
                var packet = _packet;
                ffmpeg.av_packet_free(&packet);
                _packet = null;
            }

            if (_frame != null)
            {
                var frame = _frame;
                ffmpeg.av_frame_free(&frame);
                _frame = null;
            }

            if (_codecContext != null)
            {
                var codecContext = _codecContext;
                ffmpeg.avcodec_free_context(&codecContext);
                _codecContext = null;
            }
        }
    }
}

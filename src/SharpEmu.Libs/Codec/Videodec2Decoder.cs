// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Threading.Channels;
using FFmpeg.AutoGen;
using SharpEmu.Libs.VideoOut;

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
///
/// Three-stage pipeline, none of it on the calling guest thread:
///
///   Decode() -> AU queue -> decode worker -> frame queue -> scheduler -> Submit
///
/// A first attempt at this (see the yotei-videodec2-async-attempt-regressed
/// memory) only did the first half -- offloading decode off the guest
/// thread -- and broke playback completely: with nothing pacing frames to
/// real time, the guest thread (freed from its accidental decode-latency
/// throttle) fed and decoded the whole clip in a fraction of a second,
/// overwriting a single "latest frame" slot far faster than anything could
/// display it. Two things fix that, proven by a live investigation before
/// this pass: (1) the game's own AU descriptor and FFmpeg packets carry NO
/// timestamp at all (dumped live: tag/ptr/len/constant-filler/sequential-
/// index, nothing else) -- so PTS-based scheduling isn't an option here --
/// but (2) FFmpeg does auto-detect a constant framerate from the H.264
/// stream's own SPS/VUI (measured: 30/1 on Ghost of Yotei's intro), so a
/// fixed-interval scheduler (present every 1/framerate) is both sufficient
/// and simple. The frame queue between the worker and the scheduler is
/// bounded (see FrameQueueCapacity) specifically so an unthrottled decode
/// worker can't race arbitrarily far ahead and buffer the whole clip in
/// memory (1920x1080 BGRA is ~7.9 MB/frame -- unbounded would be gigabytes
/// for a short clip); a full queue makes the worker block, which is exactly
/// the backpressure needed to keep it roughly in step with playback.
///
/// One hard constraint drove the guest-facing half of this design:
/// Videodec2Exports.Videodec2Decode's outputInfoAddress argument is a
/// per-call GUEST STACK address (see that method's own doc comment: "the
/// flag byte lives in uninitialized stack"). Neither the worker nor the
/// scheduler thread may ever write to guest memory -- by the time either
/// finishes work, the guest thread may already be several calls (and stack
/// frames) further along, and writing into a stale/reused stack slot is
/// exactly the class of bug that caused the notice-screen canary smash
/// referenced in that file. So this class exposes two independent, guest-
/// memory-free signals: TryConsumeProtocolReadySignal (lightweight
/// metadata only -- lets Decode()/Flush() tell the game "a picture is
/// ready" in order, once per decoded frame, without ever touching pixels)
/// and the frame queue itself (pixels only, consumed exclusively by this
/// class's own scheduler thread, which calls VulkanVideoPresenter.Submit
/// directly -- host-side only, no guest memory involved there either).
///
/// Measured live (SHARPEMU_VIDEODEC2_SCHED_TRACE, not left wired up): 541
/// consecutive Submit intervals on Ghost of Yotei's intro, median 33.2ms,
/// p90 34.5ms, max 35.3ms, zero over 50ms -- the scheduler holds pace
/// essentially exactly to the stream's declared 30fps. Any remaining
/// visible frame-rate dips are downstream of this class (VulkanVideoPresenter's
/// own render loop / concurrent GPU submission from the rest of the game),
/// not in this decode/pacing pipeline.
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

    // A handful of frames of lookahead is enough to absorb decode-time
    // jitter (measured spikes up to ~29ms on Ghost of Yotei) without a
    // meaningful memory cost (4 * ~7.9MB at 1080p) or introducing visible
    // extra latency before playback starts.
    private const int FrameQueueCapacity = 4;

    // Used when the stream doesn't declare a usable framerate (den == 0) --
    // never observed on Ghost of Yotei (measured 30/1), a defensive
    // fallback so the scheduler always has a sane interval rather than
    // dividing by zero or spinning.
    private const double FallbackFps = 30.0;

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

    // Producer (guest thread, via EnqueueAccessUnit/RequestDrain) -> decode
    // worker. Unbounded: Ghost of Yotei's whole intro is only ~17 MB of
    // access units total and the game hands them over far faster than
    // real playback rate, so this never grows unreasonably large -- the
    // actual pacing backpressure lives on the frame queue below instead,
    // where it also bounds memory (raw AU bytes are tiny compared to
    // decoded BGRA frames).
    private readonly Channel<byte[]?> _workChannel =
        Channel.CreateUnbounded<byte[]?>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

    // Decode worker -> scheduler. Bounded and blocking-on-full: see the
    // class-level doc comment for why (backpressure instead of buffering
    // the whole clip). FFmpeg's own avcodec_receive_frame already resolves
    // B-frame reordering into display order before a frame reaches here,
    // so this queue only needs to stay FIFO, not re-sort anything.
    private readonly Channel<(byte[] Bgra, uint Width, uint Height)> _frameQueue =
        Channel.CreateBounded<(byte[], uint, uint)>(new BoundedChannelOptions(FrameQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });

    private readonly Thread _worker;
    private readonly Thread _scheduler;

    // Dispose cancels this instead of relying on channel drain-to-empty:
    // completing the writers alone would make the worker/scheduler process
    // their entire remaining backlog before exiting (could be the whole
    // rest of the movie), racing Dispose's own FFmpeg-context teardown just
    // below. Cancelling unblocks both loops' waits promptly.
    private readonly CancellationTokenSource _workerCts = new();

    // Lightweight protocol-readiness signal -- metadata only, no pixel
    // data. This is what Decode()/Flush() poll (see the class-level doc
    // comment on why they can't just read the frame queue directly: that
    // queue is for the scheduler's eyes only). _producedCount/
    // _reportedCount let TryConsumeProtocolReadySignal report "ready"
    // exactly once per frame the worker actually produced, in order,
    // without needing the pixels themselves.
    private readonly object _protocolGate = new();
    private long _producedCount;
    private long _reportedCount;
    private uint _lastWidth;
    private uint _lastHeight;

    private Videodec2Decoder(AVCodecContext* codecContext, AVFrame* frame, AVPacket* packet)
    {
        _codecContext = codecContext;
        _frame = frame;
        _packet = packet;
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "SharpEmu Videodec2 Worker",
        };
        _scheduler = new Thread(SchedulerLoop)
        {
            IsBackground = true,
            Name = "SharpEmu Videodec2 Scheduler",
        };
        _worker.Start();
        _scheduler.Start();
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
    /// Hands one Annex-B access unit to the decode worker and returns
    /// immediately -- does not decode inline. Safe to call back-to-back as
    /// fast as the guest thread produces access units.
    /// </summary>
    public void EnqueueAccessUnit(byte[] accessUnit)
    {
        // TryWrite on an unbounded channel only fails once the writer has
        // completed (Dispose already ran) -- silently dropping here matches
        // every other "decoder gone/unavailable" path in this class.
        _workChannel.Writer.TryWrite(accessUnit);
    }

    /// <summary>
    /// Queues an end-of-stream drain request: once the worker has finished
    /// every access unit enqueued so far, it signals end-of-stream to
    /// FFmpeg and pushes one more buffered picture, if any, through the
    /// same frame queue/protocol-signal pair as a normal decode.
    /// </summary>
    public void RequestDrain()
    {
        _workChannel.Writer.TryWrite(null);
    }

    /// <summary>
    /// Non-blocking: true exactly once per frame the worker has produced
    /// (in order), letting Decode()/Flush() tell the game a picture is
    /// ready without ever touching the pixels themselves (those go through
    /// the frame queue to this class's own scheduler instead -- see the
    /// class-level doc comment on why guest memory can only be written
    /// synchronously from the guest's own call).
    /// </summary>
    public bool TryConsumeProtocolReadySignal(out uint width, out uint height)
    {
        lock (_protocolGate)
        {
            if (_reportedCount >= _producedCount)
            {
                width = 0;
                height = 0;
                return false;
            }

            _reportedCount++;
            width = _lastWidth;
            height = _lastHeight;
            return true;
        }
    }

    private void WorkerLoop()
    {
        var reader = _workChannel.Reader;
        var token = _workerCts.Token;
        while (true)
        {
            byte[]? item;
            try
            {
                if (!reader.WaitToReadAsync(token).AsTask().GetAwaiter().GetResult())
                {
                    return;
                }

                if (!reader.TryRead(out item))
                {
                    continue;
                }
            }
            catch (ChannelClosedException)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var decodedOk = item is null
                ? DrainCoreLocked(out var bgraFrame, out var hasPicture, out var width, out var height)
                : DecodeCoreLocked(item, out bgraFrame, out hasPicture, out width, out height);

            if (!decodedOk || !hasPicture || bgraFrame is null)
            {
                continue;
            }

            try
            {
                // Blocks (backpressure) if the scheduler hasn't kept up --
                // deliberate, see FrameQueueCapacity's own comment.
                _frameQueue.Writer.WriteAsync((bgraFrame, width, height), token).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ChannelClosedException)
            {
                return;
            }

            lock (_protocolGate)
            {
                _producedCount++;
                _lastWidth = width;
                _lastHeight = height;
            }
        }
    }

    private void SchedulerLoop()
    {
        var reader = _frameQueue.Reader;
        var token = _workerCts.Token;
        var haveDeadline = false;
        var nextDeadline = DateTime.MinValue;
        var frameInterval = TimeSpan.FromSeconds(1.0 / FallbackFps);

        while (true)
        {
            (byte[] Bgra, uint Width, uint Height) item;
            try
            {
                if (!reader.WaitToReadAsync(token).AsTask().GetAwaiter().GetResult())
                {
                    return;
                }

                if (!reader.TryRead(out item))
                {
                    continue;
                }
            }
            catch (ChannelClosedException)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!haveDeadline)
            {
                // The stream's declared framerate isn't reliably known
                // until FFmpeg has parsed the first frame's SPS/VUI (i.e.
                // not until the first frame reaches here) -- read it once,
                // lazily, rather than at construction time.
                var rate = _codecContext->framerate;
                var fps = rate.den > 0 && rate.num > 0
                    ? (double)rate.num / rate.den
                    : FallbackFps;
                frameInterval = TimeSpan.FromSeconds(1.0 / fps);
                nextDeadline = DateTime.UtcNow;
                haveDeadline = true;
            }

            var now = DateTime.UtcNow;
            if (nextDeadline > now)
            {
                try
                {
                    Task.Delay(nextDeadline - now, token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            VulkanVideoPresenter.Submit(item.Bgra, item.Width, item.Height);
            nextDeadline += frameInterval;

            // If we fell far enough behind (e.g. a slow decode spike) that
            // the next deadline is already in the past, resync to "now"
            // instead of trying to burn through a backlog of deadlines
            // back-to-back with no pacing at all -- the exact failure mode
            // this whole design exists to avoid.
            if (nextDeadline < DateTime.UtcNow)
            {
                nextDeadline = DateTime.UtcNow;
            }
        }
    }

    /// <summary>
    /// Feeds one Annex-B access unit to the decoder and, if a picture is
    /// ready, converts it to BGRA. Runs on the decode worker only -- never
    /// called from the guest thread. Returns false on a hard decode error;
    /// a true result with <paramref name="hasPicture"/> false is the
    /// normal "fed the packet, nothing to output yet" case (H.264 buffers
    /// several frames of reordering delay before the first picture comes
    /// out).
    /// </summary>
    private bool DecodeCoreLocked(
        byte[] accessUnit,
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
    /// sceVideodec2Flush's counterpart to DecodeCoreLocked: signals
    /// end-of-stream to the decoder and pulls one of the remaining buffered
    /// frames out, if any. Runs on the decode worker only, queued via
    /// RequestDrain so it processes after every access unit queued before
    /// it. Safe to run repeatedly -- once fully drained, avcodec_receive_
    /// frame keeps returning AVERROR_EOF and this just reports "no
    /// picture" each time.
    /// </summary>
    private bool DrainCoreLocked(out byte[]? bgraFrame, out bool hasPicture, out uint width, out uint height)
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
    /// on any FFmpeg failure. Only ever called under <see cref="_gate"/>.
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
        }

        // Cancel (not just complete the writers) so the worker/scheduler
        // don't try to process their entire remaining backlog before
        // exiting -- see _workerCts's own comment. Outside _gate: the
        // worker itself needs _gate to finish whatever single item it's
        // mid-call on.
        _workerCts.Cancel();
        _workChannel.Writer.TryComplete();
        _frameQueue.Writer.TryComplete();
        _worker.Join(TimeSpan.FromSeconds(2));
        _scheduler.Join(TimeSpan.FromSeconds(2));
        _workerCts.Dispose();

        lock (_gate)
        {
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

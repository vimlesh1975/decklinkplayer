using System.Globalization;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using FFmpeg.AutoGen.Bindings.DynamicallyLoaded;

namespace ffmpegplayer;

internal sealed unsafe class NativeFfmpegFrameDecoder : IDisposable
{
    private const string CasparSharedFfmpegPath = @"D:\vimlesh\casparcg-server-210226";

    private AVFormatContext* _formatContext;
    private AVCodecContext* _codecContext;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private SwsContext* _swsContext;
    private int _videoStreamIndex = -1;
    private int _outputWidth;
    private int _outputHeight;
    private readonly bool _fastReverseDecode;
    private bool _disposed;

    public NativeFfmpegFrameDecoder(string path, int outputWidth, int outputHeight, bool fastReverseDecode = false)
    {
        _fastReverseDecode = fastReverseDecode;
        InitializeNativeLibraries();
        Open(path, outputWidth, outputHeight);
    }

    public static bool IsAvailable()
    {
        var path = FindSharedFfmpegPath();
        return path is not null &&
            File.Exists(Path.Combine(path, "avformat-61.dll")) &&
            File.Exists(Path.Combine(path, "avcodec-61.dll")) &&
            File.Exists(Path.Combine(path, "avutil-59.dll")) &&
            File.Exists(Path.Combine(path, "swscale-8.dll"));
    }

    public byte[] DecodeFrame(TimeSpan position)
    {
        ThrowIfDisposed();

        var output = new byte[checked(_outputWidth * _outputHeight * 2)];
        Seek(position);

        var targetSeconds = position.TotalSeconds;
        var bestFrame = false;
        var decodedAnyFrame = false;
        var decodedFrames = 0;

        while (ffmpeg.av_read_frame(_formatContext, _packet) >= 0)
        {
            try
            {
                if (_packet->stream_index != _videoStreamIndex)
                {
                    continue;
                }

                ThrowIfError(ffmpeg.avcodec_send_packet(_codecContext, _packet), "send video packet");
                while (true)
                {
                    var receiveResult = ffmpeg.avcodec_receive_frame(_codecContext, _frame);
                    if (receiveResult == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receiveResult == ffmpeg.AVERROR_EOF)
                    {
                        break;
                    }

                    ThrowIfError(receiveResult, "receive video frame");
                    decodedAnyFrame = true;
                    decodedFrames++;
                    ConvertFrame(output);

                    var frameSeconds = GetFrameSeconds(_frame);
                    if (!frameSeconds.HasValue || frameSeconds.Value >= targetSeconds || decodedFrames >= 120)
                    {
                        bestFrame = true;
                        break;
                    }
                }
            }
            finally
            {
                ffmpeg.av_packet_unref(_packet);
            }

            if (bestFrame)
            {
                return output;
            }
        }

        if (decodedAnyFrame)
        {
            return output;
        }

        throw new InvalidOperationException("No video frame decoded at seek position.");
    }

    public List<DecodedVideoFrame> DecodeFrames(
        TimeSpan start,
        int frameCount,
        TimeSpan frameInterval,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (frameCount <= 0)
        {
            return [];
        }

        if (frameInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Frame interval must be positive.");
        }

        if (start < TimeSpan.Zero)
        {
            start = TimeSpan.Zero;
        }

        Seek(start);

        var frameStepSeconds = frameInterval.TotalSeconds;
        var halfFrameSeconds = frameStepSeconds / 2d;
        var startSeconds = start.TotalSeconds;
        var targetSeconds = startSeconds;
        var maxTargetSeconds = startSeconds + Math.Max(0, frameCount - 1) * frameStepSeconds;
        var frames = new List<DecodedVideoFrame>(frameCount);
        var convertedFrame = new byte[checked(_outputWidth * _outputHeight * 2)];
        var decodedFrames = 0;

        while (ffmpeg.av_read_frame(_formatContext, _packet) >= 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (_packet->stream_index != _videoStreamIndex)
                {
                    continue;
                }

                ThrowIfError(ffmpeg.avcodec_send_packet(_codecContext, _packet), "send video packet");
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var receiveResult = ffmpeg.avcodec_receive_frame(_codecContext, _frame);
                    if (receiveResult == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receiveResult == ffmpeg.AVERROR_EOF)
                    {
                        break;
                    }

                    ThrowIfError(receiveResult, "receive video frame");
                    decodedFrames++;
                    var frameSeconds = GetFrameSeconds(_frame) ??
                        startSeconds + Math.Max(0, decodedFrames - 1) * frameStepSeconds;
                    if (frameSeconds + halfFrameSeconds < targetSeconds)
                    {
                        continue;
                    }

                    ConvertFrame(convertedFrame);
                    while (frames.Count < frameCount && targetSeconds <= frameSeconds + halfFrameSeconds)
                    {
                        var frameCopy = new byte[convertedFrame.Length];
                        Buffer.BlockCopy(convertedFrame, 0, frameCopy, 0, convertedFrame.Length);
                        frames.Add(new DecodedVideoFrame(TimeSpan.FromSeconds(targetSeconds), frameCopy));
                        targetSeconds = startSeconds + frames.Count * frameStepSeconds;
                    }

                    if (frames.Count >= frameCount)
                    {
                        return frames;
                    }

                    if (frameSeconds > maxTargetSeconds + frameStepSeconds * 4d)
                    {
                        return frames;
                    }
                }
            }
            finally
            {
                ffmpeg.av_packet_unref(_packet);
            }
        }

        return frames;
    }

    private void Open(string path, int outputWidth, int outputHeight)
    {
        _outputWidth = outputWidth;
        _outputHeight = outputHeight;

        AVFormatContext* formatContext = null;
        ThrowIfError(ffmpeg.avformat_open_input(&formatContext, path, null, null), $"open media '{path}'");
        _formatContext = formatContext;

        ThrowIfError(ffmpeg.avformat_find_stream_info(_formatContext, null), "read media stream info");

        for (var i = 0; i < _formatContext->nb_streams; i++)
        {
            var stream = _formatContext->streams[i];
            if (stream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
            {
                _videoStreamIndex = i;
                break;
            }
        }

        if (_videoStreamIndex < 0)
        {
            throw new InvalidOperationException("No video stream found.");
        }

        var codecParameters = _formatContext->streams[_videoStreamIndex]->codecpar;
        var codec = ffmpeg.avcodec_find_decoder(codecParameters->codec_id);
        if (codec is null)
        {
            throw new InvalidOperationException($"No FFmpeg decoder found for codec id {codecParameters->codec_id}.");
        }

        _codecContext = ffmpeg.avcodec_alloc_context3(codec);
        if (_codecContext is null)
        {
            throw new InvalidOperationException("Unable to allocate FFmpeg codec context.");
        }

        ThrowIfError(ffmpeg.avcodec_parameters_to_context(_codecContext, codecParameters), "copy codec parameters");
        _codecContext->thread_count = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
        _codecContext->thread_type = ffmpeg.FF_THREAD_FRAME | ffmpeg.FF_THREAD_SLICE;
        if (_fastReverseDecode)
        {
            _codecContext->skip_frame = AVDiscard.AVDISCARD_NONREF;
        }

        ThrowIfError(ffmpeg.avcodec_open2(_codecContext, codec, null), "open video decoder");

        _packet = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
        if (_packet is null || _frame is null)
        {
            throw new InvalidOperationException("Unable to allocate FFmpeg packet/frame.");
        }

        _swsContext = ffmpeg.sws_getContext(
            _codecContext->width,
            _codecContext->height,
            (AVPixelFormat)_codecContext->pix_fmt,
            outputWidth,
            outputHeight,
            AVPixelFormat.AV_PIX_FMT_UYVY422,
            ffmpeg.SWS_BILINEAR,
            null,
            null,
            null);
        if (_swsContext is null)
        {
            throw new InvalidOperationException("Unable to create FFmpeg scaler.");
        }
    }

    private void Seek(TimeSpan position)
    {
        if (position < TimeSpan.Zero)
        {
            position = TimeSpan.Zero;
        }

        var stream = _formatContext->streams[_videoStreamIndex];
        var timestamp = ffmpeg.av_rescale_q(
            (long)(position.TotalSeconds * ffmpeg.AV_TIME_BASE),
            new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE },
            stream->time_base);

        ThrowIfError(ffmpeg.av_seek_frame(_formatContext, _videoStreamIndex, timestamp, ffmpeg.AVSEEK_FLAG_BACKWARD), "seek video stream");
        ffmpeg.avcodec_flush_buffers(_codecContext);
    }

    private void ConvertFrame(byte[] output)
    {
        fixed (byte* outputPtr = output)
        {
            var sourceData = _frame->data.ToArray();
            var sourceLines = _frame->linesize.ToArray();
            var destinationData = new byte_ptr4 { [0] = outputPtr };
            var destinationLines = new int4 { [0] = _outputWidth * 2 };
            var scaled = ffmpeg.sws_scale(
                _swsContext,
                sourceData,
                sourceLines,
                0,
                _codecContext->height,
                destinationData,
                destinationLines);
            if (scaled <= 0)
            {
                throw new InvalidOperationException("FFmpeg scaler did not return a frame.");
            }
        }
    }

    private double? GetFrameSeconds(AVFrame* frame)
    {
        var timestamp = frame->best_effort_timestamp;
        if (timestamp == ffmpeg.AV_NOPTS_VALUE)
        {
            return null;
        }

        var timeBase = _formatContext->streams[_videoStreamIndex]->time_base;
        return timestamp * ffmpeg.av_q2d(timeBase);
    }

    private static void InitializeNativeLibraries()
    {
        var path = FindSharedFfmpegPath()
            ?? throw new InvalidOperationException("Shared FFmpeg DLLs were not found.");

        if (!string.Equals(DynamicallyLoadedBindings.LibrariesPath, path, StringComparison.OrdinalIgnoreCase))
        {
            DynamicallyLoadedBindings.LibrariesPath = path;
            DynamicallyLoadedBindings.Initialize();
        }
    }

    private static string? FindSharedFfmpegPath()
    {
        foreach (var path in new[] { AppContext.BaseDirectory, CasparSharedFfmpegPath })
        {
            if (File.Exists(Path.Combine(path, "avformat-61.dll")) &&
                File.Exists(Path.Combine(path, "avcodec-61.dll")) &&
                File.Exists(Path.Combine(path, "avutil-59.dll")) &&
                File.Exists(Path.Combine(path, "swscale-8.dll")))
            {
                return path;
            }
        }

        return null;
    }

    private static void ThrowIfError(int errorCode, string action)
    {
        if (errorCode >= 0)
        {
            return;
        }

        Span<byte> buffer = stackalloc byte[1024];
        fixed (byte* bufferPtr = buffer)
        {
            ffmpeg.av_strerror(errorCode, bufferPtr, (ulong)buffer.Length);
            var message = Marshal.PtrToStringAnsi((IntPtr)bufferPtr) ?? errorCode.ToString(CultureInfo.InvariantCulture);
            throw new InvalidOperationException($"FFmpeg failed to {action}: {message}");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_swsContext is not null)
        {
            ffmpeg.sws_freeContext(_swsContext);
            _swsContext = null;
        }

        if (_frame is not null)
        {
            var frame = _frame;
            ffmpeg.av_frame_free(&frame);
            _frame = null;
        }

        if (_packet is not null)
        {
            var packet = _packet;
            ffmpeg.av_packet_free(&packet);
            _packet = null;
        }

        if (_codecContext is not null)
        {
            var codecContext = _codecContext;
            ffmpeg.avcodec_free_context(&codecContext);
            _codecContext = null;
        }

        if (_formatContext is not null)
        {
            var formatContext = _formatContext;
            ffmpeg.avformat_close_input(&formatContext);
            _formatContext = null;
        }

        _disposed = true;
    }
}

internal sealed record DecodedVideoFrame(TimeSpan Position, byte[] Data);

using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using DeckLinkAPI;

namespace ffmpegplayer;

internal sealed class DeckLinkSdkPlayer
{
    private const int BytesPerPixelUyvy = 2;
    private const int AudioSampleRate = 48000;
    private const int AudioBytesPerSample = 4;
    private const int AudioChunkSampleFrames = AudioSampleRate / 100;
    private const int AudioWriteZeroRetryDelayMs = 2;
    private const int AudioWriteStallLogMilliseconds = 500;
    private static readonly object HeldVideoOutputGate = new();
    private static HeldVideoOutput? s_heldVideoOutput;

    public string FormatDecoderCommand(
        PlayRequest request,
        bool throttleAudioRealtime = false,
        bool monitorPcAudio = false)
    {
        var commands = new List<string>
        {
            "Video: " + FormatCommand(request.FfmpegPath, BuildVideoDecoderArguments(request)),
        };

        if (!request.NoAudio)
        {
            commands.Add("Audio: " + FormatCommand(request.FfmpegPath, BuildAudioDecoderArguments(request, throttleAudioRealtime)));
            if (monitorPcAudio)
            {
                commands.Add("PC Audio: " + FormatCommand(GetFfplayPath(request.FfmpegPath), BuildPcAudioMonitorArguments(request)));
            }
        }

        return string.Join(Environment.NewLine, commands);
    }

    public static void ReleaseHeldVideoOutput()
    {
        var held = TakeHeldVideoOutput();
        if (held is not null)
        {
            ReleaseHeldVideoOutput(held);
        }
    }

    public async Task<ProcessResult> PlayAsync(
        PlayRequest request,
        Action<string>? logLine = null,
        CancellationToken cancellationToken = default,
        PlaybackPauseController? pauseController = null,
        bool renderInitialFrameWhilePaused = false,
        Action<byte[], int, int>? previewFrame = null,
        int previewFrameInterval = 5,
        Action<double, double>? audioMeter = null,
        bool monitorPcAudio = false)
    {
        var mode = ResolveDisplayMode(request);
        var acquiredOutput = AcquireDeckLinkOutput(request.Device, mode.DisplayMode, logLine);
        var deckLink = acquiredOutput.DeckLink;
        var output = acquiredOutput.Output;
        var videoOutputEnabled = acquiredOutput.VideoOutputAlreadyEnabled;
        var videoOutputHeld = false;

        IDeckLinkDisplayMode displayModeInfo;
        output.GetDisplayMode(mode.DisplayMode, out displayModeInfo);

        var width = displayModeInfo.GetWidth();
        var height = displayModeInfo.GetHeight();
        displayModeInfo.GetFrameRate(out var frameDuration, out var timeScale);

        var rowBytes = width * BytesPerPixelUyvy;
        var frameBytes = checked(rowBytes * height);
        var frameBuffer = new byte[frameBytes];
        var outputLock = new object();

        Process videoDecoder;
        try
        {
            videoDecoder = StartVideoDecoder(request);
        }
        catch
        {
            if (videoOutputEnabled)
            {
                try
                {
                    output.DisableVideoOutput();
                }
                catch
                {
                    // Best effort cleanup when decoder startup fails.
                }
            }

            ReleaseCom(output);
            ReleaseCom(deckLink);
            throw;
        }

        Process? audioDecoder = null;
        Process? pcAudioMonitor = null;
        var stderr = new List<string>();
        var videoStderrTask = Task.Run(
            () => PumpErrorAsync(videoDecoder, "video-decoder", stderr, logLine, cancellationToken),
            cancellationToken);
        Task? audioStderrTask = null;
        Task? audioPumpTask = null;
        Task? pcAudioMonitorStderrTask = null;
        var audioOutputEnabled = false;

        var killedByCancellation = false;
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            killedByCancellation = true;
            TryKill(videoDecoder);
            TryKill(audioDecoder);
            TryKill(pcAudioMonitor);
        });

        try
        {
            if (!videoOutputEnabled)
            {
                output.EnableVideoOutput(mode.DisplayMode, _BMDVideoOutputFlags.bmdVideoOutputFlagDefault);
                videoOutputEnabled = true;
            }
            else
            {
                logLine?.Invoke("Reusing held DeckLink video output so seek does not go blank.");
            }

            if (!request.NoAudio)
            {
                output.EnableAudioOutput(
                    _BMDAudioSampleRate.bmdAudioSampleRate48kHz,
                    _BMDAudioSampleType.bmdAudioSampleType32bitInteger,
                    (uint)request.AudioChannels,
                    _BMDAudioOutputStreamType.bmdAudioOutputStreamContinuous);

                audioOutputEnabled = true;
                audioDecoder = StartAudioDecoder(request, throttleRealtime: false);
                audioStderrTask = Task.Run(
                    () => PumpErrorAsync(audioDecoder, "audio-decoder", stderr, logLine, cancellationToken),
                    cancellationToken);
                audioPumpTask = Task.Run(
                    () => PumpAudioAsync(output, outputLock, audioDecoder, request.AudioChannels, pauseController, logLine, audioMeter, cancellationToken),
                    cancellationToken);
                if (monitorPcAudio)
                {
                    pcAudioMonitor = StartPcAudioMonitor(request);
                    pcAudioMonitorStderrTask = Task.Run(
                        () => PumpErrorAsync(pcAudioMonitor, "pc-audio", stderr, logLine, cancellationToken),
                        cancellationToken);
                    logLine?.Invoke("PC audio monitor started with bundled ffplay.exe.");
                }
            }

            var audioDescription = request.NoAudio
                ? "video only"
                : $"{request.AudioChannels}ch 32-bit PCM audio";
            logLine?.Invoke($"DeckLink SDK output enabled: {request.Device}, {width}x{height}, {FormatRate(frameDuration, timeScale)} fps, {audioDescription}.");
            logLine?.Invoke("FFmpeg is used only for decoding. DeckLink video/audio output is handled by the Blackmagic SDK.");

            var frameNumber = 0L;
            var stopwatch = Stopwatch.StartNew();
            var frameTicks = Stopwatch.Frequency * frameDuration / timeScale;
            var pausedTicks = 0L;
            var renderedInitialPausedFrame = false;

            while (!cancellationToken.IsCancellationRequested)
            {
                var shouldRenderInitialPausedFrame =
                    renderInitialFrameWhilePaused &&
                    pauseController?.IsPaused == true &&
                    frameNumber == 0 &&
                    !renderedInitialPausedFrame;

                if (pauseController?.IsPaused == true && !shouldRenderInitialPausedFrame)
                {
                    var pauseStartedTicks = stopwatch.ElapsedTicks;
                    await pauseController.WaitIfPausedAsync(cancellationToken);
                    pausedTicks += stopwatch.ElapsedTicks - pauseStartedTicks;
                }

                if (!await ReadExactFrameAsync(videoDecoder.StandardOutput.BaseStream, frameBuffer, cancellationToken))
                {
                    break;
                }

                IDeckLinkMutableVideoFrame_v14_2_1? mutableFrame = null;
                try
                {
                    output.CreateVideoFrame(
                        width,
                        height,
                        rowBytes,
                        _BMDPixelFormat.bmdFormat8BitYUV,
                        _BMDFrameFlags.bmdFrameFlagDefault,
                        out mutableFrame);

                    mutableFrame.GetBytes(out var destination);
                    Marshal.Copy(frameBuffer, 0, destination, frameBytes);

                    lock (outputLock)
                    {
                        output.DisplayVideoFrameSync((IDeckLinkVideoFrame_v14_2_1)mutableFrame);
                    }
                }
                finally
                {
                    ReleaseCom(mutableFrame);
                }

                frameNumber++;
                if (previewFrame is not null &&
                    (frameNumber == 1 || (previewFrameInterval > 0 && frameNumber % previewFrameInterval == 0)))
                {
                    var previewBuffer = new byte[frameBytes];
                    Buffer.BlockCopy(frameBuffer, 0, previewBuffer, 0, frameBytes);
                    previewFrame(previewBuffer, width, height);
                }

                if (shouldRenderInitialPausedFrame)
                {
                    renderedInitialPausedFrame = true;
                }

                if (frameNumber % 25 == 0)
                {
                    logLine?.Invoke($"sdk_frame={frameNumber}");
                }

                var targetTicks = frameNumber * frameTicks;
                var remainingTicks = targetTicks - (stopwatch.ElapsedTicks - pausedTicks);
                if (remainingTicks > 0)
                {
                    var delayMs = (int)Math.Min(remainingTicks * 1000 / Stopwatch.Frequency, 100);
                    if (delayMs > 0)
                    {
                        await Task.Delay(delayMs, cancellationToken);
                    }
                }
            }

            TryKill(videoDecoder);
            TryKill(audioDecoder);
            TryKill(pcAudioMonitor);

            await videoDecoder.WaitForExitAsync(CancellationToken.None);
            if (audioDecoder is not null)
            {
                await audioDecoder.WaitForExitAsync(CancellationToken.None);
                audioDecoder.Dispose();
                audioDecoder = null;
            }
            if (pcAudioMonitor is not null)
            {
                await pcAudioMonitor.WaitForExitAsync(CancellationToken.None);
                pcAudioMonitor.Dispose();
                pcAudioMonitor = null;
            }

            await IgnoreCancellationAsync(videoStderrTask);
            await IgnoreCancellationAsync(audioStderrTask);
            await IgnoreCancellationAsync(audioPumpTask);
            await IgnoreCancellationAsync(pcAudioMonitorStderrTask);

            return new ProcessResult(videoDecoder.ExitCode, string.Empty, string.Join(Environment.NewLine, stderr), killedByCancellation);
        }
        finally
        {
            TryKill(videoDecoder);
            videoDecoder.Dispose();
            TryKill(audioDecoder);
            TryKill(pcAudioMonitor);
            if (audioDecoder is not null)
            {
                audioDecoder.Dispose();
            }
            if (pcAudioMonitor is not null)
            {
                pcAudioMonitor.Dispose();
            }

            if (audioOutputEnabled)
            {
                try
                {
                    output.FlushBufferedAudioSamples();
                }
                catch
                {
                    // Best effort cleanup when the card is already stopped.
                }

                try
                {
                    output.DisableAudioOutput();
                }
                catch
                {
                    // Best effort cleanup when the card is already stopped.
                }
            }

            var shouldHoldVideoOutput =
                killedByCancellation &&
                videoOutputEnabled &&
                pauseController?.PreserveVideoOutputOnStop == true;

            if (shouldHoldVideoOutput)
            {
                HoldVideoOutput(request.Device, mode.DisplayMode, deckLink, output, logLine);
                videoOutputHeld = true;
            }
            else if (videoOutputEnabled)
            {
                try
                {
                    output.DisableVideoOutput();
                }
                catch
                {
                    // Best effort cleanup when the card is already stopped.
                }
            }

            if (!videoOutputHeld)
            {
                ReleaseCom(output);
                ReleaseCom(deckLink);
            }
        }
    }

    public async Task<ProcessResult> PlayPreviewOnlyAsync(
        PlayRequest request,
        Action<string>? logLine = null,
        CancellationToken cancellationToken = default,
        PlaybackPauseController? pauseController = null,
        bool renderInitialFrameWhilePaused = false,
        Action<byte[], int, int>? previewFrame = null,
        Action<double, double>? audioMeter = null,
        bool monitorPcAudio = false)
    {
        var size = ParseVideoSize(request.VideoSize)
            ?? throw new InvalidOperationException("Preview-only playback needs a valid video size.");
        var width = size.Width;
        var height = size.Height;
        var frameBytes = checked(width * height * BytesPerPixelUyvy);
        var frameBuffer = new byte[frameBytes];

        using var videoDecoder = StartVideoDecoder(request);
        Process? audioDecoder = null;
        Process? pcAudioMonitor = null;
        var stderr = new List<string>();
        var videoStderrTask = Task.Run(
            () => PumpErrorAsync(videoDecoder, "preview-video-decoder", stderr, logLine, cancellationToken),
            cancellationToken);
        Task? audioStderrTask = null;
        Task? audioPumpTask = null;
        Task? pcAudioMonitorStderrTask = null;
        var killedByCancellation = false;

        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            killedByCancellation = true;
            TryKill(videoDecoder);
            TryKill(audioDecoder);
            TryKill(pcAudioMonitor);
        });

        try
        {
            if (!request.NoAudio)
            {
                audioDecoder = StartAudioDecoder(request, throttleRealtime: true);
                audioStderrTask = Task.Run(
                    () => PumpErrorAsync(audioDecoder, "preview-audio-decoder", stderr, logLine, cancellationToken),
                    cancellationToken);
                audioPumpTask = Task.Run(
                    () => PumpAudioMeterOnlyAsync(audioDecoder, request.AudioChannels, pauseController, logLine, audioMeter, cancellationToken),
                    cancellationToken);
                if (monitorPcAudio)
                {
                    pcAudioMonitor = StartPcAudioMonitor(request);
                    pcAudioMonitorStderrTask = Task.Run(
                        () => PumpErrorAsync(pcAudioMonitor, "preview-pc-audio", stderr, logLine, cancellationToken),
                        cancellationToken);
                    logLine?.Invoke("PC audio monitor started with bundled ffplay.exe.");
                }
            }

            var audioDescription = request.NoAudio
                ? "video only"
                : $"{request.AudioChannels}ch audio metering";
            logLine?.Invoke($"Preview-only playback enabled: {width}x{height}, {NormalizeRateString(request.FrameRate)} fps, {audioDescription}.");
            logLine?.Invoke("DeckLink output is disabled. FFmpeg is decoding only for the in-app preview.");

            var frameNumber = 0L;
            var stopwatch = Stopwatch.StartNew();
            var frameTicks = GetFrameTicks(request.FrameRate);
            var pausedTicks = 0L;
            var renderedInitialPausedFrame = false;

            while (!cancellationToken.IsCancellationRequested)
            {
                var shouldRenderInitialPausedFrame =
                    renderInitialFrameWhilePaused &&
                    pauseController?.IsPaused == true &&
                    frameNumber == 0 &&
                    !renderedInitialPausedFrame;

                if (pauseController?.IsPaused == true && !shouldRenderInitialPausedFrame)
                {
                    var pauseStartedTicks = stopwatch.ElapsedTicks;
                    await pauseController.WaitIfPausedAsync(cancellationToken);
                    pausedTicks += stopwatch.ElapsedTicks - pauseStartedTicks;
                }

                if (!await ReadExactFrameAsync(videoDecoder.StandardOutput.BaseStream, frameBuffer, cancellationToken))
                {
                    break;
                }

                previewFrame?.Invoke(frameBuffer, width, height);

                frameNumber++;
                if (shouldRenderInitialPausedFrame)
                {
                    renderedInitialPausedFrame = true;
                }

                if (frameNumber % 100 == 0)
                {
                    logLine?.Invoke($"preview_frame={frameNumber}");
                }

                var targetTicks = frameNumber * frameTicks;
                var remainingTicks = targetTicks - (stopwatch.ElapsedTicks - pausedTicks);
                if (remainingTicks > 0)
                {
                    var delayMs = (int)Math.Min(remainingTicks * 1000 / Stopwatch.Frequency, 100);
                    if (delayMs > 0)
                    {
                        await Task.Delay(delayMs, cancellationToken);
                    }
                }
            }

            TryKill(videoDecoder);
            TryKill(audioDecoder);
            TryKill(pcAudioMonitor);

            await videoDecoder.WaitForExitAsync(CancellationToken.None);
            if (audioDecoder is not null)
            {
                await audioDecoder.WaitForExitAsync(CancellationToken.None);
                audioDecoder.Dispose();
                audioDecoder = null;
            }
            if (pcAudioMonitor is not null)
            {
                await pcAudioMonitor.WaitForExitAsync(CancellationToken.None);
                pcAudioMonitor.Dispose();
                pcAudioMonitor = null;
            }

            await IgnoreCancellationAsync(videoStderrTask);
            await IgnoreCancellationAsync(audioStderrTask);
            await IgnoreCancellationAsync(audioPumpTask);
            await IgnoreCancellationAsync(pcAudioMonitorStderrTask);

            return new ProcessResult(videoDecoder.ExitCode, string.Empty, string.Join(Environment.NewLine, stderr), killedByCancellation);
        }
        finally
        {
            TryKill(videoDecoder);
            TryKill(audioDecoder);
            TryKill(pcAudioMonitor);
            if (audioDecoder is not null)
            {
                audioDecoder.Dispose();
            }
            if (pcAudioMonitor is not null)
            {
                pcAudioMonitor.Dispose();
            }
        }
    }

    internal static (IDeckLink DeckLink, IDeckLinkOutput_v14_2_1 Output, bool VideoOutputAlreadyEnabled) AcquireDeckLinkOutput(
        string requestedDevice,
        _BMDDisplayMode displayMode,
        Action<string>? logLine)
    {
        HeldVideoOutput? heldToRelease = null;
        lock (HeldVideoOutputGate)
        {
            if (s_heldVideoOutput is not null &&
                string.Equals(s_heldVideoOutput.Device, requestedDevice, StringComparison.OrdinalIgnoreCase) &&
                s_heldVideoOutput.DisplayMode == displayMode)
            {
                var held = s_heldVideoOutput;
                s_heldVideoOutput = null;
                return (held.DeckLink, held.Output, true);
            }

            heldToRelease = s_heldVideoOutput;
            s_heldVideoOutput = null;
        }

        if (heldToRelease is not null)
        {
            logLine?.Invoke("Releasing held DeckLink frame because the replacement output changed.");
            ReleaseHeldVideoOutput(heldToRelease);
        }

        var deckLink = FindDeckLink(requestedDevice);
        return (deckLink, (IDeckLinkOutput_v14_2_1)deckLink, false);
    }

    internal static void HoldVideoOutput(
        string requestedDevice,
        _BMDDisplayMode displayMode,
        IDeckLink deckLink,
        IDeckLinkOutput_v14_2_1 output,
        Action<string>? logLine)
    {
        var held = new HeldVideoOutput(requestedDevice, displayMode, deckLink, output);
        HeldVideoOutput? previous;
        lock (HeldVideoOutputGate)
        {
            previous = s_heldVideoOutput;
            s_heldVideoOutput = held;
        }

        if (previous is not null)
        {
            ReleaseHeldVideoOutput(previous);
        }

        logLine?.Invoke("Holding last DeckLink frame during replacement playout.");
    }

    private static HeldVideoOutput? TakeHeldVideoOutput()
    {
        lock (HeldVideoOutputGate)
        {
            var held = s_heldVideoOutput;
            s_heldVideoOutput = null;
            return held;
        }
    }

    private static void ReleaseHeldVideoOutput(HeldVideoOutput held)
    {
        try
        {
            held.Output.DisableVideoOutput();
        }
        catch
        {
            // Best effort cleanup when the held card output is already gone.
        }

        ReleaseCom(held.Output);
        ReleaseCom(held.DeckLink);
    }

    internal static SdkDisplayMode ResolveDisplayMode(PlayRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.FormatCode) &&
            ModeByCode.TryGetValue(request.FormatCode, out var modeByCode))
        {
            return modeByCode;
        }

        var width = 1920;
        var height = 1080;
        if (!string.IsNullOrWhiteSpace(request.VideoSize))
        {
            var parts = request.VideoSize.Split('x', 'X');
            if (parts.Length == 2)
            {
                _ = int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out width);
                _ = int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out height);
            }
        }

        var modeCode = ResolveModeCodeFromGeometry(width, height, NormalizeRateCode(request.FrameRate), request.IsInterlaced);
        if (modeCode is not null && ModeByCode.TryGetValue(modeCode, out var modeByGeometry))
        {
            return modeByGeometry;
        }

        throw new InvalidOperationException($"DeckLink SDK engine does not yet map mode '{request.FormatCode ?? request.VideoSize + " " + request.FrameRate}'.");
    }

    internal static IDeckLink FindDeckLink(string requestedDevice)
    {
        var iterator = new CDeckLinkIteratorClass();
        try
        {
            while (true)
            {
                IDeckLink deckLink;
                try
                {
                    iterator.Next(out deckLink);
                }
                catch (COMException)
                {
                    break;
                }

                if (deckLink is null)
                {
                    break;
                }

                deckLink.GetDisplayName(out var displayName);
                deckLink.GetModelName(out var modelName);

                if (string.Equals(displayName, requestedDevice, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(modelName, requestedDevice, StringComparison.OrdinalIgnoreCase) ||
                    displayName.Contains(requestedDevice, StringComparison.OrdinalIgnoreCase))
                {
                    return deckLink;
                }

                ReleaseCom(deckLink);
            }
        }
        finally
        {
            ReleaseCom(iterator);
        }

        throw new InvalidOperationException($"DeckLink SDK device not found: {requestedDevice}");
    }

    private static Process StartVideoDecoder(PlayRequest request)
    {
        return StartDecoder(request.FfmpegPath, BuildVideoDecoderArguments(request));
    }

    private static Process StartAudioDecoder(PlayRequest request, bool throttleRealtime = false)
    {
        return StartDecoder(request.FfmpegPath, BuildAudioDecoderArguments(request, throttleRealtime));
    }

    private static Process StartPcAudioMonitor(PlayRequest request)
    {
        return StartDecoder(GetFfplayPath(request.FfmpegPath), BuildPcAudioMonitorArguments(request));
    }

    private static string GetFfplayPath(string ffmpegPath)
    {
        var directory = Path.GetDirectoryName(ffmpegPath) ?? AppContext.BaseDirectory;
        var ffplayPath = Path.Combine(directory, "ffplay.exe");
        if (File.Exists(ffplayPath))
        {
            return ffplayPath;
        }

        throw new InvalidOperationException($"ffplay.exe not found next to {Path.GetFileName(ffmpegPath)} for PC audio monitor.");
    }

    private static Process StartDecoder(string ffmpegPath, IReadOnlyList<string> arguments)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        return process;
    }

    private static IReadOnlyList<string> BuildVideoDecoderArguments(PlayRequest request)
    {
        var isStillImage = IsImageFile(request.InputPath);
        var args = new List<string>
        {
            "-hide_banner",
            "-nostats",
        };

        if (request.Loop && !request.UseTestPattern && !isStillImage)
        {
            args.Add("-stream_loop");
            args.Add("-1");
        }

        if (request.UseTestPattern)
        {
            args.Add("-f");
            args.Add("lavfi");
            args.Add("-i");
            args.Add($"testsrc2=size={request.VideoSize ?? "1920x1080"}:rate={NormalizeRateString(request.FrameRate)}");
        }
        else
        {
            // The SDK player clocks frames itself; avoiding -re makes seek startup much faster.
            AddSeekArguments(args, request.StartOffset);
            if (isStillImage)
            {
                args.Add("-loop");
                args.Add("1");
                args.Add("-framerate");
                args.Add(NormalizeRateString(request.FrameRate));
            }

            args.Add("-i");
            args.Add(request.InputPath);
        }

        AddDurationArguments(args, request.Duration);
        args.Add("-map");
        args.Add("0:v:0");
        args.Add("-an");
        args.Add("-vf");
        args.Add(BuildVideoFilter(request));

        if (!string.IsNullOrWhiteSpace(request.VideoSize))
        {
            args.Add("-s");
            args.Add(request.VideoSize);
        }

        args.Add("-pix_fmt");
        args.Add("uyvy422");
        args.Add("-f");
        args.Add("rawvideo");
        args.Add("pipe:1");
        return args;
    }

    private static IReadOnlyList<string> BuildAudioDecoderArguments(PlayRequest request, bool throttleRealtime)
    {
        var args = new List<string>
        {
            "-hide_banner",
            "-nostats",
        };

        if (request.Loop && !request.UseTestPattern)
        {
            args.Add("-stream_loop");
            args.Add("-1");
        }

        if (throttleRealtime)
        {
            args.Add("-re");
        }

        if (request.UseTestPattern)
        {
            args.Add("-f");
            args.Add("lavfi");
            args.Add("-i");
            args.Add($"anullsrc=r={AudioSampleRate}:cl=stereo");
        }
        else
        {
            AddSeekArguments(args, request.StartOffset);
            args.Add("-i");
            args.Add(request.InputPath);
        }

        AddDurationArguments(args, request.Duration);
        args.Add("-map");
        args.Add("0:a:0");
        args.Add("-vn");
        var audioFilters = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.AudioFilter))
        {
            audioFilters.Add(request.AudioFilter);
        }

        audioFilters.Add("aresample=async=1000:first_pts=0");
        args.Add("-af");
        args.Add(string.Join(",", audioFilters));
        args.Add("-ac");
        args.Add(request.AudioChannels.ToString(CultureInfo.InvariantCulture));
        args.Add("-ar");
        args.Add(AudioSampleRate.ToString(CultureInfo.InvariantCulture));
        args.Add("-sample_fmt");
        args.Add("s32");
        args.Add("-f");
        args.Add("s32le");
        args.Add("pipe:1");
        return args;
    }

    private static string BuildVideoFilter(PlayRequest request)
    {
        var filters = new List<string>
        {
            "setpts=PTS-STARTPTS",
        };

        if (!string.IsNullOrWhiteSpace(request.FrameRate))
        {
            filters.Add($"fps={NormalizeRateString(request.FrameRate)}:start_time=0");
        }

        if (!string.IsNullOrWhiteSpace(request.VideoFilter))
        {
            filters.Add(request.VideoFilter);
        }

        filters.Add("format=uyvy422");
        return string.Join(",", filters);
    }

    private static IReadOnlyList<string> BuildPcAudioMonitorArguments(PlayRequest request)
    {
        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel",
            "warning",
            "-nodisp",
            "-vn",
            "-sync",
            "audio",
            "-volume",
            "100",
        };

        if (request.Loop && !request.UseTestPattern)
        {
            args.Add("-loop");
            args.Add("0");
        }
        else
        {
            args.Add("-autoexit");
        }

        if (request.UseTestPattern)
        {
            args.Add("-f");
            args.Add("lavfi");
            args.Add("-i");
            args.Add($"anullsrc=r={AudioSampleRate}:cl=stereo");
        }
        else
        {
            AddSeekArguments(args, request.StartOffset);
            AddDurationArguments(args, request.Duration);
            args.Add(request.InputPath);
        }

        return args;
    }

    private static async Task<bool> ReadExactFrameAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    private static void AddSeekArguments(List<string> args, TimeSpan startOffset)
    {
        if (startOffset <= TimeSpan.Zero)
        {
            return;
        }

        args.Add("-ss");
        args.Add(FfmpegDeckLink.FormatFfmpegTimestamp(startOffset));
    }

    private static void AddDurationArguments(List<string> args, TimeSpan? duration)
    {
        if (!duration.HasValue || duration.Value <= TimeSpan.Zero)
        {
            return;
        }

        args.Add("-t");
        args.Add(FfmpegDeckLink.FormatFfmpegTimestamp(duration.Value));
    }

    private static async Task PumpErrorAsync(
        Process process,
        string prefix,
        List<string> stderr,
        Action<string>? logLine,
        CancellationToken cancellationToken)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync(cancellationToken) is { } line)
            {
                lock (stderr)
                {
                    stderr.Add($"{prefix}: {line}");
                }

                logLine?.Invoke($"{prefix}: {line}");
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during Stop.
        }
    }

    private static async Task PumpAudioAsync(
        IDeckLinkOutput_v14_2_1 output,
        object outputLock,
        Process audioDecoder,
        int channels,
        PlaybackPauseController? pauseController,
        Action<string>? logLine,
        Action<double, double>? audioMeter,
        CancellationToken cancellationToken)
    {
        var bytesPerSampleFrame = checked(channels * AudioBytesPerSample);
        var bufferBytes = checked(AudioChunkSampleFrames * bytesPerSampleFrame);
        var managedBuffer = new byte[bufferBytes];
        var unmanagedBuffer = Marshal.AllocHGlobal(bufferBytes);
        var totalWritten = 0L;
        var meterPeakLeft = 0L;
        var meterPeakRight = 0L;
        var meterSampleFrames = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (pauseController is not null)
                {
                    await pauseController.WaitIfPausedAsync(cancellationToken);
                }

                var bytesRead = await ReadAlignedAudioBlockAsync(
                    audioDecoder.StandardOutput.BaseStream,
                    managedBuffer,
                    bytesPerSampleFrame,
                    cancellationToken);

                if (bytesRead <= 0)
                {
                    break;
                }

                var sampleFrameCount = bytesRead / bytesPerSampleFrame;
                if (audioMeter is not null)
                {
                    AccumulateMeterPeaks(
                        managedBuffer,
                        sampleFrameCount,
                        bytesPerSampleFrame,
                        channels,
                        ref meterPeakLeft,
                        ref meterPeakRight);
                    meterSampleFrames += sampleFrameCount;
                    if (meterSampleFrames >= AudioSampleRate / 10)
                    {
                        audioMeter(ToDbfs(meterPeakLeft), ToDbfs(meterPeakRight));
                        meterPeakLeft = 0;
                        meterPeakRight = 0;
                        meterSampleFrames = 0;
                    }
                }

                Marshal.Copy(managedBuffer, 0, unmanagedBuffer, bytesRead);

                var written = await WriteAudioSamplesFullyAsync(
                    output,
                    outputLock,
                    unmanagedBuffer,
                    sampleFrameCount,
                    bytesPerSampleFrame,
                    logLine,
                    cancellationToken);

                var previousTotal = totalWritten;
                totalWritten += written;
                if (totalWritten / AudioSampleRate != previousTotal / AudioSampleRate)
                {
                    logLine?.Invoke($"sdk_audio_samples={totalWritten}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during Stop.
        }
        finally
        {
            Marshal.FreeHGlobal(unmanagedBuffer);
        }
    }

    private static async Task PumpAudioMeterOnlyAsync(
        Process audioDecoder,
        int channels,
        PlaybackPauseController? pauseController,
        Action<string>? logLine,
        Action<double, double>? audioMeter,
        CancellationToken cancellationToken)
    {
        var bytesPerSampleFrame = checked(channels * AudioBytesPerSample);
        var bufferBytes = checked(AudioChunkSampleFrames * bytesPerSampleFrame);
        var managedBuffer = new byte[bufferBytes];
        var totalRead = 0L;
        var meterPeakLeft = 0L;
        var meterPeakRight = 0L;
        var meterSampleFrames = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (pauseController is not null)
                {
                    await pauseController.WaitIfPausedAsync(cancellationToken);
                }

                var bytesRead = await ReadAlignedAudioBlockAsync(
                    audioDecoder.StandardOutput.BaseStream,
                    managedBuffer,
                    bytesPerSampleFrame,
                    cancellationToken);

                if (bytesRead <= 0)
                {
                    break;
                }

                var sampleFrameCount = bytesRead / bytesPerSampleFrame;
                if (audioMeter is not null)
                {
                    AccumulateMeterPeaks(
                        managedBuffer,
                        sampleFrameCount,
                        bytesPerSampleFrame,
                        channels,
                        ref meterPeakLeft,
                        ref meterPeakRight);
                    meterSampleFrames += sampleFrameCount;
                    if (meterSampleFrames >= AudioSampleRate / 10)
                    {
                        audioMeter(ToDbfs(meterPeakLeft), ToDbfs(meterPeakRight));
                        meterPeakLeft = 0;
                        meterPeakRight = 0;
                        meterSampleFrames = 0;
                    }
                }

                var previousTotal = totalRead;
                totalRead += sampleFrameCount;
                if (totalRead / AudioSampleRate != previousTotal / AudioSampleRate)
                {
                    logLine?.Invoke($"preview_audio_samples={totalRead}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during Stop.
        }
    }

    private static async Task<uint> WriteAudioSamplesFullyAsync(
        IDeckLinkOutput_v14_2_1 output,
        object outputLock,
        IntPtr buffer,
        int sampleFrameCount,
        int bytesPerSampleFrame,
        Action<string>? logLine,
        CancellationToken cancellationToken)
    {
        var totalWritten = 0u;
        var zeroWriteWait = Stopwatch.StartNew();
        var loggedStall = false;

        while (totalWritten < sampleFrameCount)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var remaining = (uint)sampleFrameCount - totalWritten;
            uint written;
            lock (outputLock)
            {
                output.WriteAudioSamplesSync(
                    IntPtr.Add(buffer, checked((int)totalWritten * bytesPerSampleFrame)),
                    remaining,
                    out written);
            }

            if (written > 0)
            {
                totalWritten += written;
                zeroWriteWait.Restart();
                loggedStall = false;
                continue;
            }

            if (!loggedStall && zeroWriteWait.ElapsedMilliseconds >= AudioWriteStallLogMilliseconds)
            {
                logLine?.Invoke($"sdk_audio_wait remaining={remaining}");
                loggedStall = true;
            }

            await Task.Delay(AudioWriteZeroRetryDelayMs, cancellationToken);
        }

        return totalWritten;
    }

    private static void AccumulateMeterPeaks(
        byte[] buffer,
        int sampleFrameCount,
        int bytesPerSampleFrame,
        int channels,
        ref long peakLeft,
        ref long peakRight)
    {
        for (var sampleFrame = 0; sampleFrame < sampleFrameCount; sampleFrame++)
        {
            var baseOffset = sampleFrame * bytesPerSampleFrame;
            var left = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(baseOffset, AudioBytesPerSample));
            var right = channels > 1
                ? BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(baseOffset + AudioBytesPerSample, AudioBytesPerSample))
                : left;

            peakLeft = Math.Max(peakLeft, Math.Abs((long)left));
            peakRight = Math.Max(peakRight, Math.Abs((long)right));
        }
    }

    private static double ToDbfs(long peak)
    {
        if (peak <= 0)
        {
            return -90;
        }

        var normalized = Math.Min(1.0, peak / (double)int.MaxValue);
        return Math.Max(-90, 20 * Math.Log10(normalized));
    }

    private static async Task<int> ReadAlignedAudioBlockAsync(
        Stream stream,
        byte[] buffer,
        int bytesPerSampleFrame,
        CancellationToken cancellationToken)
    {
        var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
        while (bytesRead > 0 && bytesRead % bytesPerSampleFrame != 0 && bytesRead < buffer.Length)
        {
            var needed = Math.Min(bytesPerSampleFrame - bytesRead % bytesPerSampleFrame, buffer.Length - bytesRead);
            var extra = await stream.ReadAsync(buffer.AsMemory(bytesRead, needed), cancellationToken);
            if (extra == 0)
            {
                break;
            }

            bytesRead += extra;
        }

        return bytesRead - bytesRead % bytesPerSampleFrame;
    }

    private static async Task IgnoreCancellationAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Expected during Stop.
        }
    }

    private static void TryKill(Process? process)
    {
        try
        {
            if (process is not null && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort while stopping.
        }
    }

    internal static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.ReleaseComObject(value);
        }
    }

    private static string NormalizeRateCode(string? frameRate)
    {
        return NormalizeRateString(frameRate) switch
        {
            "24000/1001" or "23.98" or "23.976" => "2398",
            "24" or "24000/1000" => "24",
            "25" or "25000/1000" => "25",
            "30000/1001" or "29.97" or "29.970" => "2997",
            "30" or "30000/1000" => "30",
            "50" or "50000/1000" => "50",
            "60000/1001" or "59.94" or "59.940" => "5994",
            "60" or "60000/1000" => "60",
            _ => "25",
        };
    }

    private static (int Width, int Height)? ParseVideoSize(string? videoSize)
    {
        if (string.IsNullOrWhiteSpace(videoSize))
        {
            return null;
        }

        var parts = videoSize.Split('x', 'X');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) ||
            width <= 0 ||
            height <= 0)
        {
            return null;
        }

        return (width, height);
    }

    private static long GetFrameTicks(string? frameRate)
    {
        var fps = ParseFrameRate(frameRate);
        return Math.Max(1, (long)Math.Round(Stopwatch.Frequency / fps));
    }

    private static double ParseFrameRate(string? frameRate)
    {
        var normalized = NormalizeRateString(frameRate);
        if (normalized.Contains('/'))
        {
            var parts = normalized.Split('/');
            if (parts.Length == 2 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) &&
                numerator > 0 &&
                denominator > 0)
            {
                return numerator / denominator;
            }
        }

        if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) && fps > 0)
        {
            return fps;
        }

        return 25;
    }

    private static string NormalizeRateString(string? frameRate)
    {
        return string.IsNullOrWhiteSpace(frameRate) ? "25" : frameRate;
    }

    private static string FormatRate(long frameDuration, long timeScale)
    {
        return (timeScale / (double)frameDuration).ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string FormatCommand(string fileName, IReadOnlyList<string> arguments)
    {
        return string.Join(" ", new[] { Quote(fileName) }.Concat(arguments.Select(Quote)));
    }

    private static bool IsImageFile(string path)
    {
        return ImageExtensions.Contains(Path.GetExtension(path));
    }

    private static string Quote(string value)
    {
        return value.Any(char.IsWhiteSpace) || value.Contains('"')
            ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : value;
    }

    private static string? ResolveModeCodeFromGeometry(int width, int height, string rateCode, bool isInterlaced)
    {
        return (width, height, rateCode, isInterlaced) switch
        {
            (720, 486, _, _) => "ntsc",
            (720, 576, _, _) => "pal",

            (1280, 720, "50", _) => "hp50",
            (1280, 720, "5994", _) => "hp59",
            (1280, 720, "60", _) => "hp60",

            (1920, 1080, "2398", false) => "23ps",
            (1920, 1080, "24", false) => "24ps",
            (1920, 1080, "25", false) => "Hp25",
            (1920, 1080, "2997", false) => "Hp29",
            (1920, 1080, "30", false) => "Hp30",
            (1920, 1080, "50", false) => "Hp50",
            (1920, 1080, "5994", false) => "Hp59",
            (1920, 1080, "60", false) => "Hp60",
            (1920, 1080, "25", true) => "Hi50",
            (1920, 1080, "2997", true) => "Hi59",
            (1920, 1080, "30", true) => "Hi60",

            (2048, 1080, "2398", _) => "2d23",
            (2048, 1080, "24", _) => "2d24",
            (2048, 1080, "25", _) => "2d25",
            (2048, 1080, "2997", _) => "2d29",
            (2048, 1080, "30", _) => "2d30",
            (2048, 1080, "50", _) => "2d50",
            (2048, 1080, "5994", _) => "2d59",
            (2048, 1080, "60", _) => "2d60",

            (3840, 2160, "2398", _) => "4k23",
            (3840, 2160, "24", _) => "4k24",
            (3840, 2160, "25", _) => "4k25",
            (3840, 2160, "2997", _) => "4k29",
            (3840, 2160, "30", _) => "4k30",
            (3840, 2160, "50", _) => "4k50",
            (3840, 2160, "5994", _) => "4k59",
            (3840, 2160, "60", _) => "4k60",

            (4096, 2160, "2398", _) => "4d23",
            (4096, 2160, "24", _) => "4d24",
            (4096, 2160, "25", _) => "4d25",
            (4096, 2160, "2997", _) => "4d29",
            (4096, 2160, "30", _) => "4d30",
            (4096, 2160, "50", _) => "4d50",
            (4096, 2160, "5994", _) => "4d59",
            (4096, 2160, "60", _) => "4d60",

            _ => null,
        };
    }

    private static readonly Dictionary<string, SdkDisplayMode> ModeByCode = new(StringComparer.Ordinal)
    {
        ["pal"] = new(_BMDDisplayMode.bmdModePAL),
        ["ntsc"] = new(_BMDDisplayMode.bmdModeNTSC),
        ["23ps"] = new(_BMDDisplayMode.bmdModeHD1080p2398),
        ["24ps"] = new(_BMDDisplayMode.bmdModeHD1080p24),
        ["Hp25"] = new(_BMDDisplayMode.bmdModeHD1080p25),
        ["Hp29"] = new(_BMDDisplayMode.bmdModeHD1080p2997),
        ["Hp30"] = new(_BMDDisplayMode.bmdModeHD1080p30),
        ["Hp50"] = new(_BMDDisplayMode.bmdModeHD1080p50),
        ["Hp59"] = new(_BMDDisplayMode.bmdModeHD1080p5994),
        ["Hp60"] = new(_BMDDisplayMode.bmdModeHD1080p6000),
        ["Hi50"] = new(_BMDDisplayMode.bmdModeHD1080i50),
        ["Hi59"] = new(_BMDDisplayMode.bmdModeHD1080i5994),
        ["Hi60"] = new(_BMDDisplayMode.bmdModeHD1080i6000),
        ["hp50"] = new(_BMDDisplayMode.bmdModeHD720p50),
        ["hp59"] = new(_BMDDisplayMode.bmdModeHD720p5994),
        ["hp60"] = new(_BMDDisplayMode.bmdModeHD720p60),
        ["2d23"] = new(_BMDDisplayMode.bmdMode2kDCI2398),
        ["2d24"] = new(_BMDDisplayMode.bmdMode2kDCI24),
        ["2d25"] = new(_BMDDisplayMode.bmdMode2kDCI25),
        ["2d29"] = new(_BMDDisplayMode.bmdMode2kDCI2997),
        ["2d30"] = new(_BMDDisplayMode.bmdMode2kDCI30),
        ["2d50"] = new(_BMDDisplayMode.bmdMode2kDCI50),
        ["2d59"] = new(_BMDDisplayMode.bmdMode2kDCI5994),
        ["2d60"] = new(_BMDDisplayMode.bmdMode2kDCI60),
        ["4k23"] = new(_BMDDisplayMode.bmdMode4K2160p2398),
        ["4k24"] = new(_BMDDisplayMode.bmdMode4K2160p24),
        ["4k25"] = new(_BMDDisplayMode.bmdMode4K2160p25),
        ["4k29"] = new(_BMDDisplayMode.bmdMode4K2160p2997),
        ["4k30"] = new(_BMDDisplayMode.bmdMode4K2160p30),
        ["4k50"] = new(_BMDDisplayMode.bmdMode4K2160p50),
        ["4k59"] = new(_BMDDisplayMode.bmdMode4K2160p5994),
        ["4k60"] = new(_BMDDisplayMode.bmdMode4K2160p60),
        ["4d23"] = new(_BMDDisplayMode.bmdMode4kDCI2398),
        ["4d24"] = new(_BMDDisplayMode.bmdMode4kDCI24),
        ["4d25"] = new(_BMDDisplayMode.bmdMode4kDCI25),
        ["4d29"] = new(_BMDDisplayMode.bmdMode4kDCI2997),
        ["4d30"] = new(_BMDDisplayMode.bmdMode4kDCI30),
        ["4d50"] = new(_BMDDisplayMode.bmdMode4kDCI50),
        ["4d59"] = new(_BMDDisplayMode.bmdMode4kDCI5994),
        ["4d60"] = new(_BMDDisplayMode.bmdMode4kDCI60),
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bmp",
        ".gif",
        ".jpeg",
        ".jpg",
        ".png",
        ".tga",
        ".tif",
        ".tiff",
        ".webp",
    };

    private sealed record HeldVideoOutput(
        string Device,
        _BMDDisplayMode DisplayMode,
        IDeckLink DeckLink,
        IDeckLinkOutput_v14_2_1 Output);

    internal sealed record SdkDisplayMode(_BMDDisplayMode DisplayMode);
}

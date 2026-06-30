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
    private const int DecoderNaturalExitGraceMilliseconds = 3000;
    private static readonly object HeldVideoOutputGate = new();
    private static HeldVideoOutput? s_heldVideoOutput;

    public string FormatDecoderCommand(
        PlayRequest request,
        bool throttleAudioRealtime = false,
        bool monitorPcAudio = false,
        bool useInternalPcAudioMonitor = false)
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
                commands.Add(useInternalPcAudioMonitor
                    ? "PC Audio: internal WaveOut monitor (speed follows playback controls)"
                    : "PC Audio: " + FormatCommand(GetFfplayPath(request.FfmpegPath), BuildPcAudioMonitorArguments(request)));
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
        bool monitorPcAudio = false,
        bool holdVideoOutputOnNaturalEnd = false,
        Action<TimeSpan>? playbackPosition = null)
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
        ReverseWaveOutAudioOutput? pcAudioOutput = null;
        var stderr = new List<string>();
        var videoStderrTask = Task.Run(
            () => PumpErrorAsync(videoDecoder, "video-decoder", stderr, logLine, cancellationToken),
            cancellationToken);
        Task? audioStderrTask = null;
        Task? audioPumpTask = null;
        var audioOutputEnabled = false;

        var killedByCancellation = false;
        var completedSuccessfully = false;
        var displayedVideoFrame = false;
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            killedByCancellation = true;
            TryKill(videoDecoder);
            TryKill(audioDecoder);
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
                if (monitorPcAudio)
                {
                    try
                    {
                        pcAudioOutput = new ReverseWaveOutAudioOutput(request.AudioChannels, maxPendingBuffers: 6);
                        logLine?.Invoke("PC audio monitor started with internal speed-following WaveOut output.");
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or COMException)
                    {
                        logLine?.Invoke($"PC audio monitor unavailable: {ex.Message}");
                    }
                }

                audioPumpTask = Task.Run(
                    () => PumpAudioAsync(output, outputLock, audioDecoder, request.AudioChannels, pauseController, logLine, audioMeter, pcAudioOutput, cancellationToken),
                    cancellationToken);
            }

            var audioDescription = request.NoAudio
                ? "video only"
                : $"{request.AudioChannels}ch 32-bit PCM audio";
            logLine?.Invoke($"DeckLink SDK output enabled: {request.Device}, {width}x{height}, {FormatRate(frameDuration, timeScale)} fps, {audioDescription}.");
            logLine?.Invoke("FFmpeg is used only for decoding. DeckLink video/audio output is handled by the Blackmagic SDK.");

            var frameNumber = 0L;
            var stopwatch = Stopwatch.StartNew();
            var frameTicks = Math.Max(1L, Stopwatch.Frequency * frameDuration / Math.Max(1L, timeScale));
            var sourceFrameTime = TimeSpan.FromSeconds(frameDuration / (double)Math.Max(1L, timeScale));
            var sourceFrameCarry = 0d;
            var sourceFramesRead = 0L;
            var renderedInitialPausedFrame = false;
            var reachedVideoEnd = false;

            while (!cancellationToken.IsCancellationRequested)
            {
                var shouldRenderInitialPausedFrame =
                    renderInitialFrameWhilePaused &&
                    pauseController?.IsPaused == true &&
                    frameNumber == 0 &&
                    !renderedInitialPausedFrame;

                if (pauseController?.IsPaused == true && !shouldRenderInitialPausedFrame)
                {
                    stopwatch.Stop();
                    await pauseController.WaitIfPausedAsync(cancellationToken);
                    stopwatch.Start();
                }

                var speed = GetForwardPlaybackSpeed(pauseController);
                var framesToRead = GetSourceFrameReadCount(speed, ref sourceFrameCarry, frameNumber > 0);
                var readAnyFrame = false;
                for (var sourceFrame = 0; sourceFrame < framesToRead; sourceFrame++)
                {
                    if (!await ReadExactFrameAsync(videoDecoder.StandardOutput.BaseStream, frameBuffer, cancellationToken))
                    {
                        break;
                    }

                    readAnyFrame = true;
                    sourceFramesRead++;
                }

                if (framesToRead > 0 && !readAnyFrame)
                {
                    reachedVideoEnd = frameNumber > 0 && !cancellationToken.IsCancellationRequested;
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

                    displayedVideoFrame = true;
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

                if (playbackPosition is not null &&
                    (frameNumber == 1 || (previewFrameInterval > 0 && frameNumber % previewFrameInterval == 0)))
                {
                    playbackPosition(GetDisplayedSourcePosition(request.StartOffset, sourceFrameTime, sourceFramesRead));
                }

                if (shouldRenderInitialPausedFrame)
                {
                    renderedInitialPausedFrame = true;
                }

                if (frameNumber % 100 == 0)
                {
                    logLine?.Invoke($"sdk_frame={frameNumber}");
                }

                var targetTicks = frameNumber * frameTicks;
                var remainingTicks = targetTicks - stopwatch.ElapsedTicks;
                if (remainingTicks > 0)
                {
                    var delayMs = (int)Math.Min(remainingTicks * 1000 / Stopwatch.Frequency, 100);
                    if (delayMs > 0)
                    {
                        await Task.Delay(delayMs, cancellationToken);
                    }
                }
            }

            var forceDecoderKill = killedByCancellation || cancellationToken.IsCancellationRequested;
            await WaitForDecoderExitOrKillAsync(videoDecoder, forceDecoderKill, "video decoder", logLine);
            if (audioDecoder is not null)
            {
                await WaitForDecoderExitOrKillAsync(audioDecoder, forceDecoderKill, "audio decoder", logLine);
            }

            await IgnoreCancellationAsync(videoStderrTask);
            await IgnoreCancellationAsync(audioStderrTask);
            await IgnoreCancellationAsync(audioPumpTask);

            var rawExitCode = videoDecoder.ExitCode;
            completedSuccessfully = !killedByCancellation && reachedVideoEnd;
            var exitCode = completedSuccessfully ? 0 : rawExitCode;
            if (completedSuccessfully && rawExitCode != 0)
            {
                logLine?.Invoke($"video decoder exited with code {rawExitCode} after EOF; treating playlist playback as complete.");
            }

            return new ProcessResult(exitCode, string.Empty, string.Join(Environment.NewLine, stderr), killedByCancellation);
        }
        finally
        {
            TryKill(videoDecoder);
            videoDecoder.Dispose();
            TryKill(audioDecoder);
            pcAudioOutput?.Dispose();
            if (audioDecoder is not null)
            {
                audioDecoder.Dispose();
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
                videoOutputEnabled &&
                ((killedByCancellation && pauseController?.PreserveVideoOutputOnStop == true) ||
                    (holdVideoOutputOnNaturalEnd && !killedByCancellation && displayedVideoFrame));

            if (shouldHoldVideoOutput)
            {
                HoldVideoOutput(
                    request.Device,
                    mode.DisplayMode,
                    deckLink,
                    output,
                    logLine);
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
        bool monitorPcAudio = false,
        Action<TimeSpan>? playbackPosition = null)
    {
        var size = ParseVideoSize(request.VideoSize)
            ?? throw new InvalidOperationException("Preview-only playback needs a valid video size.");
        var width = size.Width;
        var height = size.Height;
        var frameBytes = checked(width * height * BytesPerPixelUyvy);
        var frameBuffer = new byte[frameBytes];

        using var videoDecoder = StartVideoDecoder(request);
        Process? audioDecoder = null;
        ReverseWaveOutAudioOutput? pcAudioOutput = null;
        var stderr = new List<string>();
        var videoStderrTask = Task.Run(
            () => PumpErrorAsync(videoDecoder, "preview-video-decoder", stderr, logLine, cancellationToken),
            cancellationToken);
        Task? audioStderrTask = null;
        Task? audioPumpTask = null;
        var killedByCancellation = false;

        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            killedByCancellation = true;
            TryKill(videoDecoder);
            TryKill(audioDecoder);
        });

        try
        {
            if (!request.NoAudio)
            {
                audioDecoder = StartAudioDecoder(request, throttleRealtime: false);
                audioStderrTask = Task.Run(
                    () => PumpErrorAsync(audioDecoder, "preview-audio-decoder", stderr, logLine, cancellationToken),
                    cancellationToken);
                if (monitorPcAudio)
                {
                    try
                    {
                        pcAudioOutput = new ReverseWaveOutAudioOutput(request.AudioChannels, maxPendingBuffers: 6);
                        logLine?.Invoke("PC audio monitor started with internal speed-following WaveOut output.");
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or COMException)
                    {
                        logLine?.Invoke($"PC audio monitor unavailable: {ex.Message}");
                    }
                }

                audioPumpTask = Task.Run(
                    () => PumpAudioMeterOnlyAsync(audioDecoder, request.AudioChannels, pauseController, logLine, audioMeter, pcAudioOutput, cancellationToken),
                    cancellationToken);
            }

            var audioDescription = request.NoAudio
                ? "video only"
                : $"{request.AudioChannels}ch audio metering";
            logLine?.Invoke($"Preview-only playback enabled: {width}x{height}, {NormalizeRateString(request.FrameRate)} fps, {audioDescription}.");
            logLine?.Invoke("DeckLink output is disabled. FFmpeg is decoding only for the in-app preview.");

            var frameNumber = 0L;
            var stopwatch = Stopwatch.StartNew();
            var frameTicks = GetFrameTicks(request.FrameRate);
            var sourceFrameTime = TimeSpan.FromSeconds(1d / ParseFrameRate(request.FrameRate));
            var nextFrameDueTicks = stopwatch.ElapsedTicks;
            var sourceFrameCarry = 0d;
            var sourceFramesRead = 0L;
            var renderedInitialPausedFrame = false;
            var reachedVideoEnd = false;

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
                    nextFrameDueTicks += stopwatch.ElapsedTicks - pauseStartedTicks;
                }

                var speed = GetForwardPlaybackSpeed(pauseController);
                var framesToRead = GetSourceFrameReadCount(speed, ref sourceFrameCarry, frameNumber > 0);
                var readAnyFrame = false;
                for (var sourceFrame = 0; sourceFrame < framesToRead; sourceFrame++)
                {
                    if (!await ReadExactFrameAsync(videoDecoder.StandardOutput.BaseStream, frameBuffer, cancellationToken))
                    {
                        break;
                    }

                    readAnyFrame = true;
                    sourceFramesRead++;
                }

                if (framesToRead > 0 && !readAnyFrame)
                {
                    reachedVideoEnd = frameNumber > 0 && !cancellationToken.IsCancellationRequested;
                    break;
                }

                previewFrame?.Invoke(frameBuffer, width, height);
                playbackPosition?.Invoke(GetDisplayedSourcePosition(request.StartOffset, sourceFrameTime, sourceFramesRead));

                frameNumber++;
                if (shouldRenderInitialPausedFrame)
                {
                    renderedInitialPausedFrame = true;
                }

                if (frameNumber % 100 == 0)
                {
                    logLine?.Invoke($"preview_frame={frameNumber}");
                }

                nextFrameDueTicks += frameTicks;
                var remainingTicks = nextFrameDueTicks - stopwatch.ElapsedTicks;
                if (remainingTicks < -Stopwatch.Frequency)
                {
                    nextFrameDueTicks = stopwatch.ElapsedTicks;
                    remainingTicks = 0;
                }

                if (remainingTicks > 0)
                {
                    var delayMs = (int)Math.Min(remainingTicks * 1000 / Stopwatch.Frequency, 100);
                    if (delayMs > 0)
                    {
                        await Task.Delay(delayMs, cancellationToken);
                    }
                }
            }

            var forceDecoderKill = killedByCancellation || cancellationToken.IsCancellationRequested;
            await WaitForDecoderExitOrKillAsync(videoDecoder, forceDecoderKill, "preview video decoder", logLine);
            if (audioDecoder is not null)
            {
                await WaitForDecoderExitOrKillAsync(audioDecoder, forceDecoderKill, "preview audio decoder", logLine);
            }

            await IgnoreCancellationAsync(videoStderrTask);
            await IgnoreCancellationAsync(audioStderrTask);
            await IgnoreCancellationAsync(audioPumpTask);

            var rawExitCode = videoDecoder.ExitCode;
            var exitCode = !killedByCancellation && reachedVideoEnd ? 0 : rawExitCode;
            if (exitCode == 0 && rawExitCode != 0)
            {
                logLine?.Invoke($"preview video decoder exited with code {rawExitCode} after EOF; treating playlist playback as complete.");
            }

            return new ProcessResult(exitCode, string.Empty, string.Join(Environment.NewLine, stderr), killedByCancellation);
        }
        finally
        {
            TryKill(videoDecoder);
            TryKill(audioDecoder);
            pcAudioOutput?.Dispose();
            if (audioDecoder is not null)
            {
                audioDecoder.Dispose();
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
                if (TryValidateHeldVideoOutput(held, displayMode, logLine))
                {
                    return (held.DeckLink, held.Output, true);
                }

                heldToRelease = held;
            }
            else
            {
                heldToRelease = s_heldVideoOutput;
                s_heldVideoOutput = null;
            }
        }

        if (heldToRelease is not null)
        {
            if (!string.Equals(heldToRelease.Device, requestedDevice, StringComparison.OrdinalIgnoreCase) ||
                heldToRelease.DisplayMode != displayMode)
            {
                logLine?.Invoke("Releasing held DeckLink frame because the replacement output changed.");
            }

            ReleaseHeldVideoOutput(heldToRelease);
        }

        var deckLink = FindDeckLink(requestedDevice);
        return (deckLink, (IDeckLinkOutput_v14_2_1)deckLink, false);
    }

    private static TimeSpan GetDisplayedSourcePosition(TimeSpan startOffset, TimeSpan sourceFrameTime, long sourceFramesRead)
    {
        // sourceFramesRead counts consumed frames; the frame on screen is the last one consumed.
        var displayedFrameIndex = Math.Max(0L, sourceFramesRead - 1);
        return startOffset + TimeSpan.FromTicks(sourceFrameTime.Ticks * displayedFrameIndex);
    }

    private static bool TryValidateHeldVideoOutput(
        HeldVideoOutput held,
        _BMDDisplayMode displayMode,
        Action<string>? logLine)
    {
        IDeckLinkDisplayMode? displayModeInfo = null;
        try
        {
            held.Output.GetDisplayMode(displayMode, out displayModeInfo);
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            logLine?.Invoke($"Held DeckLink output is stale; reopening card. {ex.Message}");
            return false;
        }
        finally
        {
            ReleaseCom(displayModeInfo);
        }
    }

    internal static bool TryTakeHeldDeckLinkOutput(
        string requestedDevice,
        _BMDDisplayMode displayMode,
        out IDeckLink? deckLink,
        out IDeckLinkOutput_v14_2_1? output)
    {
        lock (HeldVideoOutputGate)
        {
            if (s_heldVideoOutput is not null &&
                string.Equals(s_heldVideoOutput.Device, requestedDevice, StringComparison.OrdinalIgnoreCase) &&
                s_heldVideoOutput.DisplayMode == displayMode)
            {
                var held = s_heldVideoOutput;
                s_heldVideoOutput = null;
                deckLink = held.DeckLink;
                output = held.Output;
                return true;
            }
        }

        deckLink = null;
        output = null;
        return false;
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

        logLine?.Invoke("Holding last DeckLink frame.");
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

        if (request.TransitionSegment is not null && !request.UseTestPattern)
        {
            return BuildTransitionVideoDecoderArguments(request, request.TransitionSegment);
        }

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

        if (request.TransitionSegment is PlaylistTransitionSegment segment &&
            !request.UseTestPattern &&
            !IsImageFile(segment.NextInputPath) &&
            !IsImageFile(request.InputPath))
        {
            return BuildTransitionAudioDecoderArguments(request, segment, throttleRealtime);
        }

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
        audioFilters.Add("aformat=sample_fmts=s32");
        audioFilters.Add("asetpts=N/SR/TB");
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

    private static IReadOnlyList<string> BuildTransitionVideoDecoderArguments(PlayRequest request, PlaylistTransitionSegment segment)
    {
        var args = new List<string>
        {
            "-hide_banner",
            "-nostats",
        };

        AddTransitionInputArguments(args, request.InputPath, request.StartOffset, request.Duration, request.FrameRate);
        AddTransitionInputArguments(args, segment.NextInputPath, segment.NextStartOffset, segment.NextDuration, request.FrameRate);
        args.Add("-filter_complex");
        args.Add(BuildTransitionVideoFilter(request, segment));
        args.Add("-map");
        args.Add("[vout]");
        args.Add("-an");
        args.Add("-pix_fmt");
        args.Add("uyvy422");
        args.Add("-f");
        args.Add("rawvideo");
        args.Add("pipe:1");
        return args;
    }

    private static IReadOnlyList<string> BuildTransitionAudioDecoderArguments(
        PlayRequest request,
        PlaylistTransitionSegment segment,
        bool throttleRealtime)
    {
        var args = new List<string>
        {
            "-hide_banner",
            "-nostats",
        };

        if (throttleRealtime)
        {
            args.Add("-re");
        }

        AddTransitionInputArguments(args, request.InputPath, request.StartOffset, request.Duration, request.FrameRate);
        if (throttleRealtime)
        {
            args.Add("-re");
        }

        AddTransitionInputArguments(args, segment.NextInputPath, segment.NextStartOffset, segment.NextDuration, request.FrameRate);
        args.Add("-filter_complex");
        args.Add(BuildTransitionAudioFilter(segment));
        args.Add("-map");
        args.Add("[aout]");
        args.Add("-vn");
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

    private static void AddTransitionInputArguments(
        List<string> args,
        string path,
        TimeSpan startOffset,
        TimeSpan? duration,
        string? frameRate)
    {
        if (IsImageFile(path))
        {
            args.Add("-loop");
            args.Add("1");
            args.Add("-framerate");
            args.Add(NormalizeRateString(frameRate));
            AddDurationArguments(args, duration);
            args.Add("-i");
            args.Add(path);
            return;
        }

        AddSeekArguments(args, startOffset);
        AddDurationArguments(args, duration);
        args.Add("-i");
        args.Add(path);
    }

    private static string BuildTransitionVideoFilter(PlayRequest request, PlaylistTransitionSegment segment)
    {
        var rate = NormalizeRateString(request.FrameRate);
        var scaleFilter = BuildScaleFilter(request.VideoSize);
        var transition = MapXfadeTransition(segment.Transition);
        var duration = FormatFilterSeconds(segment.Duration);
        var offset = FormatFilterSeconds(segment.Offset);

        return string.Join(
            ";",
            $"[0:v]setpts=PTS-STARTPTS,fps={rate}:start_time=0,setpts=N/({rate}*TB),{scaleFilter},format=yuv420p[v0]",
            $"[1:v]setpts=PTS-STARTPTS,fps={rate}:start_time=0,setpts=N/({rate}*TB),{scaleFilter},format=yuv420p[v1]",
            $"[v0][v1]xfade=transition={transition}:duration={duration}:offset={offset},fps={rate}:start_time=0,setpts=N/({rate}*TB),format=uyvy422[vout]");
    }

    private static string BuildTransitionAudioFilter(PlaylistTransitionSegment segment)
    {
        var duration = FormatFilterSeconds(segment.Duration);
        return string.Join(
            ";",
            "[0:a]asetpts=PTS-STARTPTS,aresample=async=1000:first_pts=0[a0]",
            "[1:a]asetpts=PTS-STARTPTS,aresample=async=1000:first_pts=0[a1]",
            $"[a0][a1]acrossfade=d={duration}:c1=tri:c2=tri,aresample=async=1000:first_pts=0,aformat=sample_fmts=s32,asetpts=N/SR/TB[aout]");
    }

    private static string BuildScaleFilter(string? videoSize)
    {
        if (!string.IsNullOrWhiteSpace(videoSize))
        {
            var parts = videoSize.Split('x', 'X');
            if (parts.Length == 2 &&
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) &&
                width > 0 &&
                height > 0)
            {
                return $"scale={width}:{height}";
            }
        }

        return "scale=1920:1080";
    }

    private static string MapXfadeTransition(string transition)
    {
        return transition switch
        {
            "Mix" => "fade",
            "Push" => "coverleft",
            "Wipe" => "wipeleft",
            "Slide" => "slideleft",
            "Fade Black" => "fadeblack",
            _ => "fade",
        };
    }

    private static string FormatFilterSeconds(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string BuildVideoFilter(PlayRequest request)
    {
        var rate = NormalizeRateString(request.FrameRate);
        var filters = new List<string>
        {
            "setpts=PTS-STARTPTS",
        };

        if (!string.IsNullOrWhiteSpace(request.VideoFilter))
        {
            filters.Add(request.VideoFilter);
        }

        filters.Add($"fps={rate}:start_time=0");
        filters.Add($"setpts=N/({rate}*TB)");
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

    private static double GetForwardPlaybackSpeed(PlaybackPauseController? pauseController)
    {
        var speed = pauseController?.PlaybackSpeed ?? 1d;
        return speed > 0d && !double.IsNaN(speed) && !double.IsInfinity(speed)
            ? speed
            : 1d;
    }

    private static int GetSourceFrameReadCount(double speed, ref double sourceFrameCarry, bool hasDisplayedFrame)
    {
        if (!hasDisplayedFrame)
        {
            sourceFrameCarry = 0d;
            return 1;
        }

        sourceFrameCarry += speed;
        var framesToRead = (int)Math.Floor(sourceFrameCarry);
        if (framesToRead <= 0)
        {
            return 0;
        }

        sourceFrameCarry -= framesToRead;
        return framesToRead;
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
        ReverseWaveOutAudioOutput? pcAudioOutput,
        CancellationToken cancellationToken)
    {
        var bytesPerSampleFrame = checked(channels * AudioBytesPerSample);
        var bufferBytes = checked(AudioChunkSampleFrames * bytesPerSampleFrame);
        var managedBuffer = new byte[bufferBytes];
        var sourceSpeedBuffer = managedBuffer;
        var unmanagedBuffer = Marshal.AllocHGlobal(bufferBytes);
        var totalWritten = 0L;
        var meterPeakLeft = 0L;
        var meterPeakRight = 0L;
        var meterSampleFrames = 0;
        var sourceSampleFrameCarry = 0d;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (pauseController is not null)
                {
                    await pauseController.WaitIfPausedAsync(cancellationToken);
                }

                var speed = GetForwardPlaybackSpeed(pauseController);
                var speedRead = await ReadSpeedAdjustedAudioBlockAsync(
                    audioDecoder.StandardOutput.BaseStream,
                    managedBuffer,
                    sourceSpeedBuffer,
                    AudioChunkSampleFrames,
                    bytesPerSampleFrame,
                    speed,
                    sourceSampleFrameCarry,
                    cancellationToken);
                sourceSpeedBuffer = speedRead.SourceBuffer;
                sourceSampleFrameCarry = speedRead.SourceSampleFrameCarry;
                var bytesRead = speedRead.BytesRead;

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

                if (pcAudioOutput is not null && written > 0)
                {
                    pcAudioOutput.Enqueue(managedBuffer, checked((int)written * bytesPerSampleFrame));
                }

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
        catch (ObjectDisposedException)
        {
            // The decoder stdout can close while the audio pump is draining at EOF.
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
        ReverseWaveOutAudioOutput? pcAudioOutput,
        CancellationToken cancellationToken)
    {
        var bytesPerSampleFrame = checked(channels * AudioBytesPerSample);
        var bufferBytes = checked(AudioChunkSampleFrames * bytesPerSampleFrame);
        var managedBuffer = new byte[bufferBytes];
        var sourceSpeedBuffer = managedBuffer;
        var totalRead = 0L;
        var meterPeakLeft = 0L;
        var meterPeakRight = 0L;
        var meterSampleFrames = 0;
        var sourceSampleFrameCarry = 0d;
        var stopwatch = Stopwatch.StartNew();
        var nextAudioDueTicks = stopwatch.ElapsedTicks;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (pauseController?.IsPaused == true)
                {
                    var pauseStartedTicks = stopwatch.ElapsedTicks;
                    await pauseController.WaitIfPausedAsync(cancellationToken);
                    nextAudioDueTicks += stopwatch.ElapsedTicks - pauseStartedTicks;
                }

                var speed = GetForwardPlaybackSpeed(pauseController);
                var speedRead = await ReadSpeedAdjustedAudioBlockAsync(
                    audioDecoder.StandardOutput.BaseStream,
                    managedBuffer,
                    sourceSpeedBuffer,
                    AudioChunkSampleFrames,
                    bytesPerSampleFrame,
                    speed,
                    sourceSampleFrameCarry,
                    cancellationToken);
                sourceSpeedBuffer = speedRead.SourceBuffer;
                sourceSampleFrameCarry = speedRead.SourceSampleFrameCarry;
                var bytesRead = speedRead.BytesRead;

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

                if (pcAudioOutput is not null)
                {
                    await pcAudioOutput.WaitForCapacityAsync(cancellationToken);
                    pcAudioOutput.Enqueue(managedBuffer, bytesRead);
                }

                var previousTotal = totalRead;
                totalRead += sampleFrameCount;
                if (totalRead / AudioSampleRate != previousTotal / AudioSampleRate)
                {
                    logLine?.Invoke($"preview_audio_samples={totalRead}");
                }

                if (pcAudioOutput is null)
                {
                    nextAudioDueTicks += sampleFrameCount * Stopwatch.Frequency / AudioSampleRate;
                    var remainingTicks = nextAudioDueTicks - stopwatch.ElapsedTicks;
                    if (remainingTicks < -Stopwatch.Frequency)
                    {
                        nextAudioDueTicks = stopwatch.ElapsedTicks;
                        remainingTicks = 0;
                    }

                    if (remainingTicks > 0)
                    {
                        var delayMs = (int)Math.Min(remainingTicks * 1000 / Stopwatch.Frequency, 100);
                        if (delayMs > 0)
                        {
                            await Task.Delay(delayMs, cancellationToken);
                        }
                    }
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

    private static async Task<AudioSpeedReadResult> ReadSpeedAdjustedAudioBlockAsync(
        Stream stream,
        byte[] outputBuffer,
        byte[] sourceBuffer,
        int outputSampleFrameCapacity,
        int bytesPerSampleFrame,
        double speed,
        double sourceSampleFrameCarry,
        CancellationToken cancellationToken)
    {
        if (Math.Abs(speed - 1d) <= 0.001d)
        {
            var bytesRead = await ReadAlignedAudioBlockAsync(stream, outputBuffer, bytesPerSampleFrame, cancellationToken);
            return new AudioSpeedReadResult(bytesRead, sourceBuffer, 0d);
        }

        sourceSampleFrameCarry += outputSampleFrameCapacity * speed;
        var requestedSourceFrames = Math.Max(1, (int)Math.Floor(sourceSampleFrameCarry));
        sourceSampleFrameCarry -= requestedSourceFrames;

        var requestedSourceBytes = checked(requestedSourceFrames * bytesPerSampleFrame);
        if (sourceBuffer.Length != requestedSourceBytes)
        {
            sourceBuffer = new byte[requestedSourceBytes];
        }

        var sourceBytesRead = await ReadAlignedAudioBlockAsync(
            stream,
            sourceBuffer,
            bytesPerSampleFrame,
            cancellationToken);
        if (sourceBytesRead <= 0)
        {
            return new AudioSpeedReadResult(0, sourceBuffer, sourceSampleFrameCarry);
        }

        var sourceSampleFrames = sourceBytesRead / bytesPerSampleFrame;
        var outputSampleFrames = Math.Min(
            outputSampleFrameCapacity,
            Math.Max(1, (int)Math.Ceiling(sourceSampleFrames / speed)));

        for (var outputFrame = 0; outputFrame < outputSampleFrames; outputFrame++)
        {
            var sourceFrame = Math.Min(
                sourceSampleFrames - 1,
                (int)Math.Floor(outputFrame * sourceSampleFrames / (double)outputSampleFrames));
            Buffer.BlockCopy(
                sourceBuffer,
                sourceFrame * bytesPerSampleFrame,
                outputBuffer,
                outputFrame * bytesPerSampleFrame,
                bytesPerSampleFrame);
        }

        return new AudioSpeedReadResult(
            outputSampleFrames * bytesPerSampleFrame,
            sourceBuffer,
            sourceSampleFrameCarry);
    }

    private readonly record struct AudioSpeedReadResult(
        int BytesRead,
        byte[] SourceBuffer,
        double SourceSampleFrameCarry);

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

    private static async Task WaitForDecoderExitOrKillAsync(
        Process? process,
        bool forceKill,
        string name,
        Action<string>? logLine)
    {
        if (process is null)
        {
            return;
        }

        if (forceKill)
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            return;
        }

        using var timeout = new CancellationTokenSource(DecoderNaturalExitGraceMilliseconds);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            logLine?.Invoke($"{name} did not exit after EOF; killing it.");
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None);
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

    private sealed class ProcessPauseBinding : IDisposable
    {
        private readonly object _gate = new();
        private readonly Process _process;
        private readonly PlaybackPauseController? _pauseController;
        private readonly Action<string>? _logLine;
        private readonly string _name;
        private bool _isSuspended;
        private bool _disposed;

        public ProcessPauseBinding(
            Process process,
            PlaybackPauseController? pauseController,
            Action<string>? logLine,
            string name)
        {
            _process = process;
            _pauseController = pauseController;
            _logLine = logLine;
            _name = name;

            if (_pauseController is null)
            {
                return;
            }

            _pauseController.PauseStateChanged += PauseController_PauseStateChanged;
            if (_pauseController.IsPaused)
            {
                SetSuspended(true);
            }
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

            if (_pauseController is not null)
            {
                _pauseController.PauseStateChanged -= PauseController_PauseStateChanged;
            }
        }

        private void PauseController_PauseStateChanged(bool paused)
        {
            SetSuspended(paused);
        }

        private void SetSuspended(bool suspended)
        {
            lock (_gate)
            {
                if (_disposed || _isSuspended == suspended)
                {
                    return;
                }

                try
                {
                    if (_process.HasExited)
                    {
                        return;
                    }

                    var status = suspended
                        ? NtSuspendProcess(_process.Handle)
                        : NtResumeProcess(_process.Handle);
                    if (status != 0)
                    {
                        _logLine?.Invoke($"{_name}: pause control returned 0x{status:X8}.");
                        return;
                    }

                    _isSuspended = suspended;
                    _logLine?.Invoke(suspended
                        ? $"{_name}: ffplay paused."
                        : $"{_name}: ffplay resumed.");
                }
                catch (Exception ex)
                {
                    _logLine?.Invoke($"{_name}: ffplay pause control failed: {ex.Message}");
                }
            }
        }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr processHandle);

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

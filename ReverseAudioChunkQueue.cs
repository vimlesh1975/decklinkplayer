using System.Diagnostics;
using System.Globalization;

namespace ffmpegplayer;

internal sealed class ReverseAudioChunkQueue : IDisposable
{
    private const int AudioSampleRate = 48000;
    private const int AudioBytesPerSample = 4;
    private static readonly TimeSpan DecodeOutputBlockDuration = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DecodeTimeout = TimeSpan.FromSeconds(3);

    private readonly PlayRequest _request;
    private readonly double _speed;
    private readonly int _bytesPerSampleFrame;
    private readonly Action<string>? _logLine;
    private readonly object _gate = new();
    private readonly Queue<byte[]> _chunks = [];
    private readonly CancellationTokenSource _cancellation = new();
    private TimeSpan _nextDecodeEnd;
    private int _chunkOffset;
    private int _queuedBytes;
    private bool _decodeRunning;
    private bool _sourceEnded;
    private bool _disposed;

    public ReverseAudioChunkQueue(PlayRequest request, double speed, TimeSpan startPosition, Action<string>? logLine)
    {
        _request = request;
        _speed = Math.Clamp(speed, 1d, 2d);
        _nextDecodeEnd = startPosition;
        _bytesPerSampleFrame = checked(request.AudioChannels * AudioBytesPerSample);
        _logLine = logLine;
        StartDecodeIfNeeded();
    }

    public ReverseAudioFrame ReadFrame(TimeSpan frameDuration)
    {
        var sampleFrames = Math.Max(1, (int)Math.Round(frameDuration.TotalSeconds * AudioSampleRate));
        var output = new byte[checked(sampleFrames * _bytesPerSampleFrame)];
        var copied = 0;

        lock (_gate)
        {
            while (copied < output.Length && _chunks.Count > 0)
            {
                var chunk = _chunks.Peek();
                var available = chunk.Length - _chunkOffset;
                var toCopy = Math.Min(output.Length - copied, available);
                Buffer.BlockCopy(chunk, _chunkOffset, output, copied, toCopy);
                copied += toCopy;
                _chunkOffset += toCopy;
                _queuedBytes -= toCopy;

                if (_chunkOffset >= chunk.Length)
                {
                    _chunks.Dequeue();
                    _chunkOffset = 0;
                }
            }
        }

        StartDecodeIfNeeded();
        return new ReverseAudioFrame(output, sampleFrames, copied > 0);
    }

    private void StartDecodeIfNeeded()
    {
        TimeSpan decodeEnd;
        TimeSpan sourceStart;
        TimeSpan sourceDuration;

        lock (_gate)
        {
            if (_disposed || _decodeRunning || _sourceEnded || _queuedBytes >= GetLowWaterBytes())
            {
                return;
            }

            if (_nextDecodeEnd <= TimeSpan.Zero)
            {
                _sourceEnded = true;
                return;
            }

            sourceDuration = TimeSpan.FromTicks((long)Math.Round(DecodeOutputBlockDuration.Ticks * _speed));
            decodeEnd = _nextDecodeEnd;
            sourceStart = decodeEnd - sourceDuration;
            if (sourceStart < TimeSpan.Zero)
            {
                sourceStart = TimeSpan.Zero;
                sourceDuration = decodeEnd;
            }

            if (sourceDuration <= TimeSpan.Zero)
            {
                _sourceEnded = true;
                return;
            }

            _nextDecodeEnd = sourceStart;
            _decodeRunning = true;
        }

        _ = Task.Run(() => DecodeBlockAsync(sourceStart, sourceDuration));
    }

    private int GetLowWaterBytes()
    {
        return checked((int)(AudioSampleRate * 0.25d) * _bytesPerSampleFrame);
    }

    private async Task DecodeBlockAsync(TimeSpan sourceStart, TimeSpan sourceDuration)
    {
        try
        {
            var pcm = await DecodeReverseAudioAsync(sourceStart, sourceDuration, _cancellation.Token);
            if (pcm.Length > 0)
            {
                lock (_gate)
                {
                    if (!_disposed)
                    {
                        _chunks.Enqueue(pcm);
                        _queuedBytes += pcm.Length;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when reverse playback stops.
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _sourceEnded = true;
            }

            _logLine?.Invoke($"Reverse audio disabled: {ex.Message}");
        }
        finally
        {
            lock (_gate)
            {
                _decodeRunning = false;
            }

            StartDecodeIfNeeded();
        }
    }

    private async Task<byte[]> DecodeReverseAudioAsync(TimeSpan sourceStart, TimeSpan sourceDuration, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DecodeTimeout);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _request.FfmpegPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        foreach (var argument in BuildArguments(sourceStart, sourceDuration))
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        try
        {
            await using var output = new MemoryStream();
            var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(output, timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(stdoutTask, process.WaitForExitAsync(timeout.Token), stderrTask);

            if (process.ExitCode != 0)
            {
                var error = FirstLine(stderrTask.Result);
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                    ? $"ffmpeg exited with code {process.ExitCode}"
                    : error);
            }

            var pcm = output.ToArray();
            var alignedLength = pcm.Length - pcm.Length % _bytesPerSampleFrame;
            if (alignedLength != pcm.Length)
            {
                Array.Resize(ref pcm, alignedLength);
            }

            return pcm;
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    private IReadOnlyList<string> BuildArguments(TimeSpan sourceStart, TimeSpan sourceDuration)
    {
        var filters = new List<string> { "areverse" };
        if (_speed > 1.001d)
        {
            filters.Add($"atempo={_speed.ToString("0.###", CultureInfo.InvariantCulture)}");
        }

        filters.Add("aresample=async=0:first_pts=0");

        return
        [
            "-hide_banner",
            "-loglevel",
            "warning",
            "-nostats",
            "-ss",
            FfmpegDeckLink.FormatFfmpegTimestamp(sourceStart),
            "-t",
            FfmpegDeckLink.FormatFfmpegTimestamp(sourceDuration),
            "-i",
            _request.InputPath,
            "-map",
            "0:a:0?",
            "-vn",
            "-af",
            string.Join(",", filters),
            "-ac",
            _request.AudioChannels.ToString(CultureInfo.InvariantCulture),
            "-ar",
            AudioSampleRate.ToString(CultureInfo.InvariantCulture),
            "-sample_fmt",
            "s32",
            "-f",
            "s32le",
            "pipe:1",
        ];
    }

    private static string FirstLine(string text)
    {
        return text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()
            ?.Trim() ?? string.Empty;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort cleanup for a short-lived decoder.
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
            _chunks.Clear();
            _queuedBytes = 0;
        }

        _cancellation.Cancel();
        _cancellation.Dispose();
    }
}

internal readonly record struct ReverseAudioFrame(byte[] Pcm, int SampleFrames, bool HasAudio);

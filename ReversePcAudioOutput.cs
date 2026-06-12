using System.Diagnostics;

namespace ffmpegplayer;

internal sealed class ReversePcAudioOutput : IDisposable
{
    private const int AudioSampleRate = 48000;
    private const int AudioBytesPerSample = 4;
    private const int MaxQueuedBytes = AudioSampleRate * AudioBytesPerSample * 2;

    private readonly Process _process;
    private readonly Action<string>? _logLine;
    private readonly object _gate = new();
    private readonly Queue<byte[]> _queue = [];
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _writerTask;
    private readonly Task _stderrTask;
    private int _queuedBytes;
    private bool _disposed;

    public ReversePcAudioOutput(string ffplayPath, int channels, Action<string>? logLine)
    {
        if (channels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channels), "Audio channels must be positive.");
        }

        _logLine = logLine;
        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffplayPath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        foreach (var argument in BuildArguments(channels))
        {
            _process.StartInfo.ArgumentList.Add(argument);
        }

        _process.Start();
        _writerTask = Task.Run(() => WriteLoopAsync(_cancellation.Token));
        _stderrTask = Task.Run(() => ReadStderrAsync(_cancellation.Token));
    }

    public void Enqueue(byte[] pcm, int byteCount)
    {
        if (byteCount <= 0 || _disposed)
        {
            return;
        }

        byteCount = Math.Min(byteCount, pcm.Length);
        var copy = new byte[byteCount];
        Buffer.BlockCopy(pcm, 0, copy, 0, byteCount);

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            while (_queuedBytes + copy.Length > MaxQueuedBytes && _queue.Count > 0)
            {
                _queuedBytes -= _queue.Dequeue().Length;
            }

            _queue.Enqueue(copy);
            _queuedBytes += copy.Length;
        }

        _signal.Release();
    }

    private async Task WriteLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _signal.WaitAsync(cancellationToken);
                byte[]? pcm = null;
                lock (_gate)
                {
                    if (_queue.Count > 0)
                    {
                        pcm = _queue.Dequeue();
                        _queuedBytes -= pcm.Length;
                    }
                }

                if (pcm is null)
                {
                    continue;
                }

                await _process.StandardInput.BaseStream.WriteAsync(pcm, cancellationToken);
                await _process.StandardInput.BaseStream.FlushAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during Stop.
        }
        catch (Exception ex)
        {
            _logLine?.Invoke($"Reverse PC audio stopped: {ex.Message}");
        }
    }

    private async Task ReadStderrAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync(cancellationToken) is { } line)
            {
                if (line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("failed", StringComparison.OrdinalIgnoreCase))
                {
                    _logLine?.Invoke($"reverse-pc-audio: {line}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during Stop.
        }
        catch
        {
            // Best effort diagnostics only.
        }
    }

    private static IReadOnlyList<string> BuildArguments(int channels)
    {
        return
        [
            "-hide_banner",
            "-loglevel",
            "warning",
            "-nodisp",
            "-fflags",
            "nobuffer",
            "-flags",
            "low_delay",
            "-f",
            "s32le",
            "-ar",
            AudioSampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-ac",
            channels.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-i",
            "pipe:0",
        ];
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
            // Best effort cleanup.
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
            _queue.Clear();
            _queuedBytes = 0;
        }

        _cancellation.Cancel();
        try
        {
            _process.StandardInput.Close();
        }
        catch
        {
            // The process may already be gone.
        }

        TryKill(_process);
        try
        {
            Task.WaitAll([_writerTask, _stderrTask], 250);
        }
        catch
        {
            // Best effort cleanup.
        }

        _signal.Dispose();
        _cancellation.Dispose();
        _process.Dispose();
    }
}

namespace ffmpegplayer;

internal sealed class ReverseDeckLinkAudioOutput : IDisposable
{
    private const int AudioSampleRate = 48000;
    private const int MaxQueuedSampleFrames = AudioSampleRate / 2;
    private const int MaxQueuedBuffers = 16;

    private readonly NativeDeckLinkPreviewOutput _output;
    private readonly Action<string>? _logLine;
    private readonly object _gate = new();
    private readonly Queue<AudioPacket> _queue = [];
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _writerTask;
    private int _queuedSampleFrames;
    private bool _disposed;
    private bool _stopped;
    private DateTime _lastDropLogAt = DateTime.MinValue;

    public ReverseDeckLinkAudioOutput(NativeDeckLinkPreviewOutput output, Action<string>? logLine)
    {
        _output = output;
        _logLine = logLine;
        _writerTask = Task.Run(() => WriteLoopAsync(_cancellation.Token));
    }

    public bool Enqueue(byte[] pcm, int byteCount, int sampleFrames)
    {
        if (byteCount <= 0 || sampleFrames <= 0 || _disposed || _stopped)
        {
            return false;
        }

        byteCount = Math.Min(byteCount, pcm.Length);
        var copy = new byte[byteCount];
        Buffer.BlockCopy(pcm, 0, copy, 0, byteCount);

        var droppedSampleFrames = 0;
        lock (_gate)
        {
            if (_disposed || _stopped)
            {
                return false;
            }

            while ((_queuedSampleFrames + sampleFrames > MaxQueuedSampleFrames || _queue.Count >= MaxQueuedBuffers) &&
                _queue.Count > 0)
            {
                var dropped = _queue.Dequeue();
                _queuedSampleFrames -= dropped.SampleFrames;
                droppedSampleFrames += dropped.SampleFrames;
            }

            _queue.Enqueue(new AudioPacket(copy, sampleFrames));
            _queuedSampleFrames += sampleFrames;
        }

        if (droppedSampleFrames > 0)
        {
            LogDrop(droppedSampleFrames);
        }

        _signal.Release();
        return true;
    }

    private async Task WriteLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _signal.WaitAsync(cancellationToken);
                while (TryDequeue(out var packet))
                {
                    if (!_output.WriteAudioSamples(packet.Pcm, packet.SampleFrames))
                    {
                        StopWriter("Reverse DeckLink audio stopped: audio write stalled or output is unavailable.");
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during Stop.
        }
        catch (Exception ex)
        {
            StopWriter($"Reverse DeckLink audio stopped: {ex.Message}");
        }
    }

    private bool TryDequeue(out AudioPacket packet)
    {
        lock (_gate)
        {
            if (_queue.Count == 0)
            {
                packet = default;
                return false;
            }

            packet = _queue.Dequeue();
            _queuedSampleFrames -= packet.SampleFrames;
            return true;
        }
    }

    private void StopWriter(string message)
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            _queue.Clear();
            _queuedSampleFrames = 0;
        }

        _logLine?.Invoke(message);
    }

    private void LogDrop(int droppedSampleFrames)
    {
        var now = DateTime.UtcNow;
        if (now - _lastDropLogAt < TimeSpan.FromSeconds(1))
        {
            return;
        }

        _lastDropLogAt = now;
        _logLine?.Invoke($"Reverse DeckLink audio dropped {droppedSampleFrames} sample frames to keep shuttle responsive.");
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
            _queuedSampleFrames = 0;
        }

        _cancellation.Cancel();
        _signal.Release();
        try
        {
            _writerTask.Wait(1000);
        }
        catch
        {
            // Best effort cleanup while playback is changing.
        }

        _signal.Dispose();
        _cancellation.Dispose();
    }

    private readonly record struct AudioPacket(byte[] Pcm, int SampleFrames);
}

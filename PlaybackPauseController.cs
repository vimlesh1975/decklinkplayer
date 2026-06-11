namespace ffmpegplayer;

internal sealed class PlaybackPauseController
{
    private readonly object _gate = new();
    private TaskCompletionSource? _resumeSignal;
    private bool _preserveVideoOutputOnStop;
    private double _playbackSpeed = 1d;

    public event Action<bool>? PauseStateChanged;

    public bool PreserveVideoOutputOnStop
    {
        get
        {
            lock (_gate)
            {
                return _preserveVideoOutputOnStop;
            }
        }
        set
        {
            lock (_gate)
            {
                _preserveVideoOutputOnStop = value;
            }
        }
    }

    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _resumeSignal is not null;
            }
        }
    }

    public double PlaybackSpeed
    {
        get
        {
            lock (_gate)
            {
                return _playbackSpeed;
            }
        }
        set
        {
            lock (_gate)
            {
                _playbackSpeed = value > 0d && !double.IsNaN(value) && !double.IsInfinity(value)
                    ? value
                    : 1d;
            }
        }
    }

    public void Pause()
    {
        var changed = false;
        lock (_gate)
        {
            if (_resumeSignal is null)
            {
                _resumeSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                changed = true;
            }
        }

        if (changed)
        {
            PauseStateChanged?.Invoke(true);
        }
    }

    public void Resume()
    {
        TaskCompletionSource? resumeSignal;
        var changed = false;
        lock (_gate)
        {
            resumeSignal = _resumeSignal;
            _resumeSignal = null;
            changed = resumeSignal is not null;
        }

        resumeSignal?.TrySetResult();
        if (changed)
        {
            PauseStateChanged?.Invoke(false);
        }
    }

    public async Task WaitIfPausedAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task? waitTask;
            lock (_gate)
            {
                waitTask = _resumeSignal?.Task;
            }

            if (waitTask is null)
            {
                return;
            }

            await waitTask.WaitAsync(cancellationToken);
        }
    }
}

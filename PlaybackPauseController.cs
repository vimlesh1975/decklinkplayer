namespace ffmpegplayer;

internal sealed class PlaybackPauseController
{
    private readonly object _gate = new();
    private TaskCompletionSource? _resumeSignal;
    private bool _preserveVideoOutputOnStop;

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

    public void Pause()
    {
        lock (_gate)
        {
            _resumeSignal ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Resume()
    {
        TaskCompletionSource? resumeSignal;
        lock (_gate)
        {
            resumeSignal = _resumeSignal;
            _resumeSignal = null;
        }

        resumeSignal?.TrySetResult();
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

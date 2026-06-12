namespace ffmpegplayer;

internal sealed class ReverseFrameCache : IDisposable
{
    private const int MaxCacheFrames = 90;
    private const int MinCacheFrames = 8;
    private const int MaxParallelPrefetchBlocks = 3;
    private const long MaxTotalCacheBytes = 1024L * 1024 * 1024;
    private static readonly TimeSpan PrefetchSwitchWait = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan BlockBoundaryTolerance = TimeSpan.FromMilliseconds(2);

    private readonly NativeFfmpegFrameDecoder _decoder;
    private readonly string _inputPath;
    private readonly int _width;
    private readonly int _height;
    private readonly TimeSpan _cacheFrameInterval;
    private readonly int _cacheFrameStep;
    private readonly int _cacheFrameCount;
    private readonly int _prefetchBlockTarget;
    private readonly Action<string>? _logLine;
    private readonly object _prefetchGate = new();
    private readonly List<ReverseFrameBlock> _prefetchedBlocks = [];
    private readonly List<PrefetchJob> _prefetchJobs = [];
    private readonly bool _fastReverseDecode;
    private ReverseFrameBlock? _currentBlock;
    private bool _disposed;

    public ReverseFrameCache(
        string inputPath,
        int width,
        int height,
        TimeSpan frameDuration,
        double playbackSpeed,
        Action<string>? logLine)
    {
        if (frameDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(frameDuration), "Frame duration must be positive.");
        }

        _inputPath = inputPath;
        _width = width;
        _height = height;
        _cacheFrameStep = GetCacheFrameStep(playbackSpeed);
        _fastReverseDecode = _cacheFrameStep >= 10;
        _decoder = new NativeFfmpegFrameDecoder(inputPath, width, height, _fastReverseDecode);
        _cacheFrameInterval = TimeSpan.FromTicks(frameDuration.Ticks * _cacheFrameStep);
        _prefetchBlockTarget = GetPrefetchBlockTarget(_cacheFrameStep);
        _logLine = logLine;

        var frameBytes = Math.Max(1, checked(width * height * 2));
        var activeBlockCount = 1 + _prefetchBlockTarget;
        var maxBlockBytes = Math.Max(frameBytes * MinCacheFrames, MaxTotalCacheBytes / activeBlockCount);
        var memoryLimitedFrames = (int)Math.Max(MinCacheFrames, Math.Min(MaxCacheFrames, maxBlockBytes / frameBytes));
        _cacheFrameCount = Math.Clamp(Math.Min(memoryLimitedFrames, GetMaxFramesForSpeed(_cacheFrameStep)), MinCacheFrames, MaxCacheFrames);
    }

    public DecodedVideoFrame GetFrame(TimeSpan target, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (target < TimeSpan.Zero)
        {
            target = TimeSpan.Zero;
        }

        if (_currentBlock is null || !_currentBlock.Contains(target, _cacheFrameInterval))
        {
            if (!TryUsePrefetchedBlock(target, cancellationToken))
            {
                ClearPrefetch();
                _currentBlock = DecodeBlockEndingAt(_decoder, target, cancellationToken);
                _logLine?.Invoke($"Reverse cache loaded {_currentBlock.Frames.Count} display frame(s), step {_cacheFrameStep}, from {FormatClock(_currentBlock.Start)} to {FormatClock(_currentBlock.End)}.");
            }
        }

        StartPrefetchPreviousBlocks();
        var currentBlock = _currentBlock ?? throw new InvalidOperationException("Reverse cache did not load a frame block.");
        return currentBlock.GetNearestFrame(target, _cacheFrameInterval);
    }

    private bool TryUsePrefetchedBlock(TimeSpan target, CancellationToken cancellationToken)
    {
        PrefetchJob? jobToWait = null;
        lock (_prefetchGate)
        {
            HarvestCompletedPrefetchUnderLock();
            if (TryTakePrefetchedBlockUnderLock(target, out var block))
            {
                _currentBlock = block;
                _logLine?.Invoke($"Reverse cache switched to prefetched block {FormatClock(block.Start)} to {FormatClock(block.End)}.");
                return true;
            }

            if (_cacheFrameStep >= 10 && TryTakeOlderPrefetchedBlockUnderLock(target, out block))
            {
                _currentBlock = block;
                _logLine?.Invoke($"Reverse cache skipped to ready block {FormatClock(block.Start)} to {FormatClock(block.End)}.");
                return true;
            }

            if (_cacheFrameStep >= 10 && TryTakeAnyUsefulPrefetchedBlockUnderLock(target, out block))
            {
                _currentBlock = block;
                _logLine?.Invoke($"Reverse cache jumped to ready block {FormatClock(block.Start)} to {FormatClock(block.End)}.");
                return true;
            }

            jobToWait = _prefetchJobs.FirstOrDefault(job => TargetCouldUseBlockEndingAt(target, job.BlockEnd));
        }

        if (jobToWait is null)
        {
            return false;
        }

        try
        {
            if (!jobToWait.Task.Wait(PrefetchSwitchWait, cancellationToken))
            {
                lock (_prefetchGate)
                {
                    HarvestCompletedPrefetchUnderLock();
                    if (_cacheFrameStep >= 10 && TryTakeOlderPrefetchedBlockUnderLock(target, out var lateBlock))
                    {
                        _currentBlock = lateBlock;
                        _logLine?.Invoke($"Reverse cache caught late ready block {FormatClock(lateBlock.Start)} to {FormatClock(lateBlock.End)}.");
                        return true;
                    }

                    if (_cacheFrameStep >= 10 && TryTakeAnyUsefulPrefetchedBlockUnderLock(target, out lateBlock))
                    {
                        _currentBlock = lateBlock;
                        _logLine?.Invoke($"Reverse cache jumped to ready block {FormatClock(lateBlock.Start)} to {FormatClock(lateBlock.End)}.");
                        return true;
                    }
                }

                _logLine?.Invoke("Reverse cache prefetch not ready; decoding next block inline.");
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RemovePrefetchJob(jobToWait);
            _logLine?.Invoke($"Reverse cache prefetch skipped: {ex.GetBaseException().Message}");
            return false;
        }

        lock (_prefetchGate)
        {
            HarvestCompletedPrefetchUnderLock();
            if (!TryTakePrefetchedBlockUnderLock(target, out var block))
            {
                if (_cacheFrameStep >= 10 && TryTakeOlderPrefetchedBlockUnderLock(target, out block))
                {
                    _currentBlock = block;
                    _logLine?.Invoke($"Reverse cache skipped to ready block {FormatClock(block.Start)} to {FormatClock(block.End)}.");
                    return true;
                }

                if (_cacheFrameStep >= 10 && TryTakeAnyUsefulPrefetchedBlockUnderLock(target, out block))
                {
                    _currentBlock = block;
                    _logLine?.Invoke($"Reverse cache jumped to ready block {FormatClock(block.Start)} to {FormatClock(block.End)}.");
                    return true;
                }

                return false;
            }

            _currentBlock = block;
            _logLine?.Invoke($"Reverse cache switched to prefetched block {FormatClock(block.Start)} to {FormatClock(block.End)}.");
            return true;
        }
    }

    private bool TryTakePrefetchedBlockUnderLock(TimeSpan target, out ReverseFrameBlock block)
    {
        var index = _prefetchedBlocks.FindIndex(candidate => candidate.Contains(target, _cacheFrameInterval));
        if (index < 0)
        {
            block = null!;
            return false;
        }

        block = _prefetchedBlocks[index];
        _prefetchedBlocks.RemoveRange(0, index + 1);
        return true;
    }

    private bool TryTakeOlderPrefetchedBlockUnderLock(TimeSpan target, out ReverseFrameBlock block)
    {
        var index = _prefetchedBlocks.FindIndex(candidate => candidate.End < target);
        if (index < 0)
        {
            block = null!;
            return false;
        }

        block = _prefetchedBlocks[index];
        _prefetchedBlocks.RemoveRange(0, index + 1);
        return true;
    }

    private bool TryTakeAnyUsefulPrefetchedBlockUnderLock(TimeSpan target, out ReverseFrameBlock block)
    {
        var index = _prefetchedBlocks.FindIndex(candidate => candidate.Start < target);
        if (index < 0)
        {
            block = null!;
            return false;
        }

        block = _prefetchedBlocks[index];
        _prefetchedBlocks.RemoveRange(0, index + 1);
        return true;
    }

    private void StartPrefetchPreviousBlocks()
    {
        if (_currentBlock is null || _currentBlock.Start <= TimeSpan.Zero)
        {
            return;
        }

        lock (_prefetchGate)
        {
            HarvestCompletedPrefetchUnderLock();
            while (_prefetchedBlocks.Count + _prefetchJobs.Count < _prefetchBlockTarget)
            {
                var prefetchEnd = GetNextPrefetchEndUnderLock();
                if (prefetchEnd <= TimeSpan.Zero && HasBlockOrJobEndingAt(TimeSpan.Zero))
                {
                    return;
                }

                if (HasBlockOrJobEndingAt(prefetchEnd))
                {
                    return;
                }

                var cancellation = new CancellationTokenSource();
                var cancellationToken = cancellation.Token;
                var task = Task.Run(
                    () =>
                    {
                        using var decoder = new NativeFfmpegFrameDecoder(_inputPath, _width, _height, _fastReverseDecode);
                        return DecodeBlockEndingAt(decoder, prefetchEnd, cancellationToken);
                    },
                    cancellationToken);
                _prefetchJobs.Add(new PrefetchJob(prefetchEnd, task, cancellation));
            }
        }
    }

    private void HarvestCompletedPrefetchUnderLock()
    {
        for (var index = _prefetchJobs.Count - 1; index >= 0; index--)
        {
            var job = _prefetchJobs[index];
            if (!job.Task.IsCompleted)
            {
                continue;
            }

            _prefetchJobs.RemoveAt(index);
            job.Cancellation.Dispose();
            try
            {
                var block = job.Task.GetAwaiter().GetResult();
                _prefetchedBlocks.Add(block);
                _prefetchedBlocks.Sort((left, right) => right.End.CompareTo(left.End));
                _logLine?.Invoke($"Reverse cache prefetched {block.Frames.Count} display frame(s), step {_cacheFrameStep}, from {FormatClock(block.Start)} to {FormatClock(block.End)}. Queue {_prefetchedBlocks.Count}/{_prefetchBlockTarget}, running {_prefetchJobs.Count}.");
            }
            catch (OperationCanceledException)
            {
                // Expected during stop or speed changes.
            }
            catch (Exception ex)
            {
                _logLine?.Invoke($"Reverse cache prefetch skipped: {ex.GetBaseException().Message}");
            }
        }
    }

    private TimeSpan GetNextPrefetchEndUnderLock()
    {
        var baseStart = GetOldestQueuedStartUnderLock();
        var endTicks = baseStart.Ticks - _cacheFrameInterval.Ticks;
        return endTicks > 0 ? TimeSpan.FromTicks(endTicks) : TimeSpan.Zero;
    }

    private TimeSpan GetOldestQueuedStartUnderLock()
    {
        var oldest = _currentBlock?.Start ?? TimeSpan.Zero;
        foreach (var block in _prefetchedBlocks)
        {
            if (block.Start < oldest)
            {
                oldest = block.Start;
            }
        }

        foreach (var job in _prefetchJobs)
        {
            var jobStart = GetBlockStartForEnd(job.BlockEnd);
            if (jobStart < oldest)
            {
                oldest = jobStart;
            }
        }

        return oldest;
    }

    private bool HasBlockOrJobEndingAt(TimeSpan blockEnd)
    {
        return _prefetchedBlocks.Any(block => NearlyEqual(block.End, blockEnd)) ||
            _prefetchJobs.Any(job => NearlyEqual(job.BlockEnd, blockEnd));
    }

    private void RemovePrefetchJob(PrefetchJob job)
    {
        lock (_prefetchGate)
        {
            if (_prefetchJobs.Remove(job))
            {
                job.Cancellation.Cancel();
                job.Cancellation.Dispose();
            }
        }
    }

    private bool TargetCouldUseBlockEndingAt(TimeSpan target, TimeSpan blockEnd)
    {
        var expectedStart = GetBlockStartForEnd(blockEnd);
        return target >= expectedStart - BlockBoundaryTolerance && target <= blockEnd + BlockBoundaryTolerance;
    }

    private ReverseFrameBlock DecodeBlockEndingAt(
        NativeFfmpegFrameDecoder decoder,
        TimeSpan target,
        CancellationToken cancellationToken)
    {
        var blockStart = GetBlockStartForEnd(target);
        var availableTicks = Math.Max(0, target.Ticks - blockStart.Ticks);
        var frameCount = Math.Clamp((int)(availableTicks / _cacheFrameInterval.Ticks) + 1, 1, _cacheFrameCount);

        var decodedFrames = decoder.DecodeFrames(blockStart, frameCount, _cacheFrameInterval, cancellationToken);
        if (decodedFrames.Count == 0)
        {
            throw new InvalidOperationException("No reverse cache frames decoded.");
        }

        return new ReverseFrameBlock(decodedFrames);
    }

    private TimeSpan GetBlockStartForEnd(TimeSpan target)
    {
        var framesBeforeTarget = Math.Max(0, _cacheFrameCount - 1);
        var requestedStartTicks = target.Ticks - _cacheFrameInterval.Ticks * framesBeforeTarget;
        return requestedStartTicks > 0 ? TimeSpan.FromTicks(requestedStartTicks) : TimeSpan.Zero;
    }

    private static int GetCacheFrameStep(double playbackSpeed)
    {
        var speed = Math.Abs(playbackSpeed);
        if (double.IsNaN(speed) || double.IsInfinity(speed) || speed < 2d)
        {
            return 1;
        }

        return Math.Clamp((int)Math.Floor(speed + 0.001d), 1, 20);
    }

    private static int GetPrefetchBlockTarget(int cacheFrameStep)
    {
        if (cacheFrameStep >= 10)
        {
            return MaxParallelPrefetchBlocks;
        }

        return cacheFrameStep > 1 ? 2 : 1;
    }

    private static int GetMaxFramesForSpeed(int cacheFrameStep)
    {
        if (cacheFrameStep >= 20)
        {
            return 18;
        }

        if (cacheFrameStep >= 10)
        {
            return 24;
        }

        if (cacheFrameStep >= 5)
        {
            return 40;
        }

        return MaxCacheFrames;
    }

    private void ClearPrefetch()
    {
        lock (_prefetchGate)
        {
            foreach (var job in _prefetchJobs)
            {
                job.Cancellation.Cancel();
                job.Cancellation.Dispose();
            }

            _prefetchJobs.Clear();
            _prefetchedBlocks.Clear();
        }
    }

    private static bool NearlyEqual(TimeSpan left, TimeSpan right)
    {
        return Math.Abs((left - right).Ticks) <= TimeSpan.TicksPerMillisecond;
    }

    private static string FormatClock(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return value.ToString(@"hh\:mm\:ss\.fff");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ClearPrefetch();
        _currentBlock = null;
        _decoder.Dispose();
    }

    private sealed class ReverseFrameBlock
    {
        public ReverseFrameBlock(List<DecodedVideoFrame> frames)
        {
            if (frames.Count == 0)
            {
                throw new ArgumentException("Reverse frame block cannot be empty.", nameof(frames));
            }

            Frames = frames;
            Start = frames[0].Position;
            End = frames[^1].Position;
        }

        public List<DecodedVideoFrame> Frames { get; }

        public TimeSpan Start { get; }

        public TimeSpan End { get; }

        public bool Contains(TimeSpan target, TimeSpan frameInterval)
        {
            return target >= Start - BlockBoundaryTolerance && target <= End + BlockBoundaryTolerance;
        }

        public DecodedVideoFrame GetNearestFrame(TimeSpan target, TimeSpan frameInterval)
        {
            var index = (int)Math.Round((target - Start).Ticks / (double)frameInterval.Ticks);
            index = Math.Clamp(index, 0, Frames.Count - 1);
            return Frames[index];
        }
    }

    private sealed record PrefetchJob(
        TimeSpan BlockEnd,
        Task<ReverseFrameBlock> Task,
        CancellationTokenSource Cancellation);
}

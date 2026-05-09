using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ffmpegplayer;

internal sealed class PreviewFrameHelperClient : IDisposable
{
    private readonly string _inputPath;
    private readonly int _width;
    private readonly int _height;
    private readonly Action<string>? _logLine;
    private Process? _process;
    private Task? _stderrPumpTask;
    private bool _disposed;

    public PreviewFrameHelperClient(string inputPath, int width, int height, Action<string>? logLine)
    {
        _inputPath = inputPath;
        _width = width;
        _height = height;
        _logLine = logLine;
    }

    public bool Matches(string inputPath, int width, int height)
    {
        return width == _width &&
            height == _height &&
            string.Equals(inputPath, _inputPath, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<byte[]> DecodeFrameAsync(TimeSpan position, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var process = EnsureStarted();
        using var cancellationRegistration = cancellationToken.Register(() => TryKill(process));

        try
        {
            await process.StandardInput.WriteLineAsync(position.TotalSeconds.ToString("0.#########", CultureInfo.InvariantCulture));
            await process.StandardInput.FlushAsync(cancellationToken);

            var stdout = process.StandardOutput.BaseStream;
            var status = await ReadInt32Async(stdout, cancellationToken);
            if (status > 0)
            {
                var frame = new byte[status];
                await ReadExactAsync(stdout, frame, cancellationToken);
                return frame;
            }

            if (status == PreviewFrameHelperProtocol.ErrorStatus)
            {
                var errorLength = await ReadInt32Async(stdout, cancellationToken);
                if (errorLength is <= 0 or > 1024 * 1024)
                {
                    throw new InvalidOperationException("Preview helper returned an invalid error packet.");
                }

                var errorBytes = new byte[errorLength];
                await ReadExactAsync(stdout, errorBytes, cancellationToken);
                throw new InvalidOperationException(Encoding.UTF8.GetString(errorBytes));
            }

            throw new InvalidOperationException($"Preview helper returned invalid status {status}.");
        }
        catch
        {
            if (process.HasExited)
            {
                _logLine?.Invoke($"Persistent preview helper exited with code {process.ExitCode}.");
            }

            DisposeProcess();
            throw;
        }
    }

    private Process EnsureStarted()
    {
        if (_process is not null && !_process.HasExited)
        {
            return _process;
        }

        DisposeProcess();
        var executablePath = Environment.ProcessPath ?? Application.ExecutablePath;
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        process.StartInfo.ArgumentList.Add("preview-helper");
        process.StartInfo.ArgumentList.Add("--input");
        process.StartInfo.ArgumentList.Add(_inputPath);
        process.StartInfo.ArgumentList.Add("--width");
        process.StartInfo.ArgumentList.Add(_width.ToString(CultureInfo.InvariantCulture));
        process.StartInfo.ArgumentList.Add("--height");
        process.StartInfo.ArgumentList.Add(_height.ToString(CultureInfo.InvariantCulture));

        process.Start();
        process.StandardInput.AutoFlush = true;
        _process = process;
        _stderrPumpTask = Task.Run(() => PumpStderrAsync(process));
        _logLine?.Invoke("Persistent preview helper started.");
        return process;
    }

    private async Task PumpStderrAsync(Process process)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    _logLine?.Invoke($"preview-helper: {line}");
                }
            }
        }
        catch
        {
            // Best effort log pump.
        }
    }

    private void DisposeProcess()
    {
        var process = _process;
        _process = null;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                try
                {
                    process.StandardInput.WriteLine("quit");
                }
                catch
                {
                    // The helper may already be gone.
                }

                if (!process.WaitForExit(250))
                {
                    TryKill(process);
                }
            }
        }
        catch
        {
            TryKill(process);
        }
        finally
        {
            process.Dispose();
        }
    }

    private static async Task<int> ReadInt32Async(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4];
        await ReadExactAsync(stream, buffer, cancellationToken);
        return BinaryPrimitives.ReadInt32LittleEndian(buffer);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("Preview helper ended before sending a full frame.");
            }

            offset += read;
        }
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
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeProcess();
        _stderrPumpTask = null;
    }
}

internal static class PreviewFrameHelperProtocol
{
    public const int ErrorStatus = -1;
}

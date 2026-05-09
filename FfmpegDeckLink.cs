using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ffmpegplayer;

internal sealed partial class FfmpegDeckLink
{
    public const string DefaultPixelFormat = "uyvy422";
    public const int DefaultAudioChannels = 2;
    public const double DefaultPrerollSeconds = 0.5;

    public string FindDefaultFfmpegPath()
    {
        foreach (var candidate in EnumerateFfmpegCandidates())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
    }

    public async Task<IReadOnlyList<string>> ListDevicesAsync(string ffmpegPath, CancellationToken cancellationToken = default)
    {
        var result = await RunProcessAsync(
            ffmpegPath,
            ["-hide_banner", "-f", "decklink", "-list_devices", "1", "-i", "dummy"],
            cancellationToken: cancellationToken);

        var devices = ParseDeviceNames(result.StandardError);
        return devices;
    }

    public async Task<IReadOnlyList<DeckLinkMode>> ListFormatsAsync(
        string ffmpegPath,
        string device,
        CancellationToken cancellationToken = default)
    {
        var result = await RunProcessAsync(
            ffmpegPath,
            ["-hide_banner", "-f", "decklink", "-list_formats", "1", "-i", device],
            cancellationToken: cancellationToken);

        var modes = ParseModes(result.StandardError);
        if (modes.Count == 0 && !string.IsNullOrWhiteSpace(result.StandardError))
        {
            throw new InvalidOperationException(result.StandardError.Trim());
        }

        return modes;
    }

    public async Task<PlayRequest> ResolveModeDefaultsAsync(
        PlayRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.FormatCode))
        {
            return request;
        }

        var modes = await ListFormatsAsync(request.FfmpegPath, request.Device, cancellationToken);
        var mode = modes.FirstOrDefault(m =>
            string.Equals(m.Code, request.FormatCode, StringComparison.OrdinalIgnoreCase));

        if (mode is null)
        {
            throw new InvalidOperationException(
                $"Format code '{request.FormatCode}' was not found for device '{request.Device}'.");
        }

        return ApplyModeDefaults(request, mode);
    }

    public PlayRequest ApplyModeDefaults(PlayRequest request, DeckLinkMode? mode)
    {
        if (mode is null)
        {
            return request;
        }

        return request with
        {
            FormatCode = mode.Code,
            VideoSize = string.IsNullOrWhiteSpace(request.VideoSize) ? $"{mode.Width}x{mode.Height}" : request.VideoSize,
            FrameRate = string.IsNullOrWhiteSpace(request.FrameRate) ? mode.FrameRate : request.FrameRate,
            IsInterlaced = mode.IsInterlaced,
            FieldOrder = mode.FieldOrder,
        };
    }

    public IReadOnlyList<string> BuildPlayArguments(PlayRequest options)
    {
        var isStillImage = IsImageFile(options.InputPath);
        var args = new List<string>
        {
            "-hide_banner",
            "-nostats",
            "-progress",
            "pipe:2",
        };

        if (options.Loop && !options.UseTestPattern && !isStillImage)
        {
            args.Add("-stream_loop");
            args.Add("-1");
        }

        if (options.UseTestPattern)
        {
            args.Add("-f");
            args.Add("lavfi");
            args.Add("-i");
            args.Add($"testsrc2=size={options.VideoSize ?? "1920x1080"}:rate={NormalizeLavfiRate(options.FrameRate)}");


        }
        else
        {
            // Prevent negative/non-monotonic timestamps from stalling realtime DeckLink playout.
            args.Add("-fflags");
            args.Add("+genpts");
            args.Add("-avoid_negative_ts");
            args.Add("make_zero");
            args.Add("-re");
            AddSeekArguments(args, options.StartOffset);
            if (isStillImage)
            {
                args.Add("-loop");
                args.Add("1");
                args.Add("-framerate");
                args.Add(NormalizeLavfiRate(options.FrameRate));
            }

            args.Add("-i");
            args.Add(options.InputPath);

        }

        args.Add("-map");
        args.Add("0:v:0");

        if (options.NoAudio)
        {
            args.Add("-an");
        }
        else
        {
            args.Add("-map");
            args.Add("0:a?");
        }

        var videoFilters = BuildVideoFilters(options);
        if (videoFilters.Count > 0)
        {
            args.Add("-vf");
            args.Add(string.Join(",", videoFilters));
        }

        if (!string.IsNullOrWhiteSpace(options.FrameRate))
        {
            args.Add("-r");
            args.Add(options.FrameRate);
        }

        if (!string.IsNullOrWhiteSpace(options.VideoSize))
        {
            args.Add("-s");
            args.Add(options.VideoSize);
        }

        if (options.IsInterlaced)
        {
            args.Add("-field_order");
            args.Add(GetFieldOrderOption(options.FieldOrder));
        }

        args.Add("-pix_fmt");
        args.Add(options.PixelFormat);

        args.Add("-sn");
        args.Add("-dn");

        if (!options.NoAudio)
        {
            if (!string.IsNullOrWhiteSpace(options.AudioFilter))
            {
                args.Add("-af");
                args.Add(options.AudioFilter);
            }

            args.Add("-ac");
            args.Add(options.AudioChannels.ToString(CultureInfo.InvariantCulture));
            args.Add("-ar");
            args.Add("48000");
            args.Add("-c:a");
            args.Add("pcm_s16le");
        }

        if (!options.Loop && !options.UseTestPattern)
        {
            args.Add("-shortest");
        }

        args.Add("-f");
        args.Add("decklink");

        args.Add("-preroll");
        args.Add(options.PrerollSeconds.ToString("0.###", CultureInfo.InvariantCulture));

        if (!string.IsNullOrWhiteSpace(options.DuplexMode))
        {
            args.Add("-duplex_mode");
            args.Add(options.DuplexMode);
        }

        if (!string.IsNullOrWhiteSpace(options.LinkMode))
        {
            args.Add("-link");
            args.Add(options.LinkMode);
        }

        if (options.LevelA.HasValue)
        {
            args.Add("-level_a");
            args.Add(options.LevelA.Value ? "true" : "false");
        }

        args.Add(options.Device);
        return args;
    }

    public async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        Action<string>? logLine = null,
        CancellationToken cancellationToken = default)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Unable to start '{fileName}': {ex.Message}", ex);
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var killedByCancellation = false;

        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            killedByCancellation = true;
            TryTerminateProcess(process);
        });

        var stdoutTask = PumpReaderAsync(process.StandardOutput, stdout, logLine);
        var stderrTask = PumpReaderAsync(process.StandardError, stderr, logLine);

        await Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync(CancellationToken.None));

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString(), killedByCancellation);
    }

    public string FormatCommand(string fileName, IReadOnlyList<string> arguments)
    {
        return string.Join(" ", new[] { Quote(fileName) }.Concat(arguments.Select(Quote)));
    }

    private static List<string> BuildVideoFilters(PlayRequest options)
    {
        var filters = new List<string>();

        if (!string.IsNullOrWhiteSpace(options.VideoFilter))
        {
            filters.Add(options.VideoFilter);
        }

        filters.Add($"format={options.PixelFormat}");

        if (options.IsInterlaced)
        {
            filters.Add($"setfield={GetSetFieldMode(options.FieldOrder)}");
        }

        return filters;
    }

    private static string GetSetFieldMode(string? fieldOrder)
    {
        return string.Equals(fieldOrder, "lower", StringComparison.OrdinalIgnoreCase) ? "bff" : "tff";
    }

    private static string NormalizeLavfiRate(string? frameRate)
    {
        if (string.Equals(frameRate, "25000/1000", StringComparison.OrdinalIgnoreCase))
        {
            return "25";
        }

        return string.IsNullOrWhiteSpace(frameRate) ? "25" : frameRate;
    }

    private static string GetFieldOrderOption(string? fieldOrder)
    {
        return string.Equals(fieldOrder, "lower", StringComparison.OrdinalIgnoreCase) ? "bb" : "tt";
    }

    private static List<string> ParseDeviceNames(string text)
    {
        var devices = new List<string>();
        foreach (Match match in DeviceRegex().Matches(text))
        {
            devices.Add(match.Groups["name"].Value);
        }

        return devices;
    }

    private static List<DeckLinkMode> ParseModes(string text)
    {
        var modes = new List<DeckLinkMode>();
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) ||
                line.StartsWith("[", StringComparison.Ordinal) ||
                line.StartsWith("Supported formats", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("format_code", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = SplitCodeAndDescription(line);
            if (parts is null)
            {
                continue;
            }

            var descriptionMatch = ModeDescriptionRegex().Match(parts.Value.Description);
            if (!descriptionMatch.Success)
            {
                continue;
            }

            var extra = descriptionMatch.Groups["extra"].Success
                ? descriptionMatch.Groups["extra"].Value
                : null;

            var isInterlaced = extra?.Contains("interlaced", StringComparison.OrdinalIgnoreCase) == true;
            string? fieldOrder = null;
            if (extra?.Contains("upper field first", StringComparison.OrdinalIgnoreCase) == true)
            {
                fieldOrder = "upper";
            }
            else if (extra?.Contains("lower field first", StringComparison.OrdinalIgnoreCase) == true)
            {
                fieldOrder = "lower";
            }

            modes.Add(
                new DeckLinkMode(
                    parts.Value.Code,
                    int.Parse(descriptionMatch.Groups["width"].Value, CultureInfo.InvariantCulture),
                    int.Parse(descriptionMatch.Groups["height"].Value, CultureInfo.InvariantCulture),
                    descriptionMatch.Groups["fps"].Value,
                    isInterlaced,
                    fieldOrder,
                    parts.Value.Description));
        }

        return modes;
    }

    private static (string Code, string Description)? SplitCodeAndDescription(string line)
    {
        var parts = line.Split(['\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 2)
        {
            return (parts[0], string.Join(" ", parts[1..]));
        }

        var match = CodeDescriptionRegex().Match(line);
        if (!match.Success)
        {
            return null;
        }

        return (match.Groups["code"].Value.Trim(), match.Groups["description"].Value.Trim());
    }

    private static async Task PumpReaderAsync(StreamReader reader, StringBuilder buffer, Action<string>? logLine)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            buffer.AppendLine(line);
            logLine?.Invoke(line);
        }
    }

    private static void TryTerminateProcess(Process process)
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
            // Best effort only while FFmpeg is exiting.
        }
    }

    private static IEnumerable<string> EnumerateFfmpegCandidates()
    {
        var roots = new[]
        {
            AppContext.BaseDirectory,
            Environment.CurrentDirectory,
        };

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            var directory = new DirectoryInfo(root);
            for (var depth = 0; directory is not null && depth < 8; depth++, directory = directory.Parent)
            {
                AddCandidate(candidates, directory.FullName, "ffmpeg.exe");
                AddCandidate(candidates, directory.FullName, "bin", "ffmpeg.exe");
                AddCandidate(candidates, directory.FullName, "bin", "Debug", "net10.0", "ffmpeg.exe");
                AddCandidate(candidates, directory.FullName, "bin", "Debug", "net10.0-windows", "ffmpeg.exe");
                AddCandidate(candidates, directory.FullName, "bin", "Release", "net10.0", "ffmpeg.exe");
                AddCandidate(candidates, directory.FullName, "bin", "Release", "net10.0-windows", "ffmpeg.exe");
            }
        }

        return candidates;
    }

    private static void AddCandidate(HashSet<string> candidates, params string[] parts)
    {
        try
        {
            candidates.Add(Path.GetFullPath(Path.Combine(parts)));
        }
        catch
        {
            // Ignore invalid path fragments.
        }
    }

    private static string Quote(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        return value.Any(char.IsWhiteSpace) || value.Contains('"')
            ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : value;
    }

    private static void AddSeekArguments(List<string> args, TimeSpan startOffset)
    {
        if (startOffset <= TimeSpan.Zero)
        {
            return;
        }

        args.Add("-ss");
        args.Add(FormatFfmpegTimestamp(startOffset));
    }

    internal static string FormatFfmpegTimestamp(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        if (value.TotalHours >= 1)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0:00}:{1:00}:{2:00}.{3:000}",
                (int)value.TotalHours,
                value.Minutes,
                value.Seconds,
                value.Milliseconds);
        }

        return value.ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);
    }

    private static bool IsImageFile(string path)
    {
        return ImageExtensions.Contains(Path.GetExtension(path));
    }

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

    [GeneratedRegex("'(?<name>[^']+)'", RegexOptions.Compiled)]
    private static partial Regex DeviceRegex();

    [GeneratedRegex("^(?<code>\\S+)\\s+(?<description>.+)$", RegexOptions.Compiled)]
    private static partial Regex CodeDescriptionRegex();

    [GeneratedRegex("(?<width>\\d+)x(?<height>\\d+) at (?<fps>[0-9/]+) fps(?: \\((?<extra>.+)\\))?", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex ModeDescriptionRegex();

}

internal sealed record DeckLinkMode(
    string Code,
    int Width,
    int Height,
    string FrameRate,
    bool IsInterlaced,
    string? FieldOrder,
    string Description)
{
    public override string ToString()
    {
        var scan = IsInterlaced ? $"interlaced {FieldOrder ?? "unknown"}" : "progressive";
        return $"{Code} - {Width}x{Height} @ {FrameRate} {scan}";
    }
}

internal sealed record PlayRequest(
    string FfmpegPath,
    string InputPath,
    string Device,
    string? FormatCode,
    string? VideoSize,
    string? FrameRate,
    string PixelFormat,
    int AudioChannels,
    double PrerollSeconds,
    string? DuplexMode,
    string? LinkMode,
    bool? LevelA,
    string? VideoFilter,
    string? AudioFilter,
    bool Loop,
    bool NoAudio,
    bool IsInterlaced,
    string? FieldOrder,
    bool UseTestPattern,
    TimeSpan StartOffset = default);

internal sealed record ProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool Cancelled);

using System.Globalization;

namespace ffmpegplayer;

internal static class Cli
{
    private static readonly FfmpegDeckLink DeckLink = new();
    private static readonly DeckLinkSdkPlayer SdkPlayer = new();

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 0 || IsHelpToken(args[0]))
            {
                ShowHelp();
                return 0;
            }

            return args[0].ToLowerInvariant() switch
            {
                "devices" => await RunDevicesAsync(ParseDevicesOptions(args[1..])),
                "formats" => await RunFormatsAsync(ParseFormatsOptions(args[1..])),
                "play" => await RunPlayAsync(ParsePlayOptions(args[1..], dryRun: false)),
                "dry-run" => await RunPlayAsync(ParsePlayOptions(args[1..], dryRun: true)),
                _ => Fail($"Unknown command '{args[0]}'."),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            Console.Error.WriteLine();
            ShowHelp();
            return 1;
        }
    }

    private static async Task<int> RunDevicesAsync(CommonOptions options)
    {
        var devices = await DeckLink.ListDevicesAsync(options.FfmpegPath);
        Console.WriteLine("DeckLink devices visible to FFmpeg:");
        foreach (var device in devices)
        {
            Console.WriteLine($"  {device}");
        }

        return 0;
    }

    private static async Task<int> RunFormatsAsync(FormatsOptions options)
    {
        var modes = await DeckLink.ListFormatsAsync(options.FfmpegPath, options.Device);
        Console.WriteLine($"Supported modes for {options.Device}:");
        foreach (var mode in modes)
        {
            Console.WriteLine($"  {mode}");
        }

        return 0;
    }

    private static async Task<int> RunPlayAsync(PlayOptions options)
    {
        if (!File.Exists(options.Request.InputPath))
        {
            throw new InvalidOperationException($"Input file not found: {options.Request.InputPath}");
        }

        var request = await DeckLink.ResolveModeDefaultsAsync(options.Request);

        Console.WriteLine("Running DeckLink SDK output with FFmpeg decoder command:");
        Console.WriteLine(SdkPlayer.FormatDecoderCommand(request));
        Console.WriteLine();

        if (options.DryRun)
        {
            Console.WriteLine("Dry run only. No media was played.");
            return 0;
        }

        var result = await SdkPlayer.PlayAsync(request, Console.Error.WriteLine);
        return result.ExitCode;
    }

    private static CommonOptions ParseDevicesOptions(string[] args)
    {
        var ffmpegPath = DeckLink.FindDefaultFfmpegPath();

        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            var (name, inlineValue) = SplitOption(token);
            switch (name)
            {
                case "--ffmpeg-path":
                    ffmpegPath = inlineValue ?? ReadNextValue(args, ref i, name);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown option for devices: {token}");
            }
        }

        return new CommonOptions(ffmpegPath);
    }

    private static FormatsOptions ParseFormatsOptions(string[] args)
    {
        var ffmpegPath = DeckLink.FindDefaultFfmpegPath();
        string? device = null;

        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            var (name, inlineValue) = SplitOption(token);
            switch (name)
            {
                case "--ffmpeg-path":
                    ffmpegPath = inlineValue ?? ReadNextValue(args, ref i, name);
                    break;
                case "--device":
                    device = inlineValue ?? ReadNextValue(args, ref i, name);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown option for formats: {token}");
            }
        }

        if (string.IsNullOrWhiteSpace(device))
        {
            throw new InvalidOperationException("The formats command requires --device.");
        }

        return new FormatsOptions(ffmpegPath, device);
    }

    private static PlayOptions ParsePlayOptions(string[] args, bool dryRun)
    {
        var ffmpegPath = DeckLink.FindDefaultFfmpegPath();
        string? input = null;
        string? device = null;
        string? formatCode = null;
        string? videoSize = null;
        string? frameRate = null;
        var pixelFormat = FfmpegDeckLink.DefaultPixelFormat;
        var audioChannels = FfmpegDeckLink.DefaultAudioChannels;
        var preroll = FfmpegDeckLink.DefaultPrerollSeconds;
        string? duplexMode = null;
        string? linkMode = null;
        bool? levelA = null;
        var linkModeWasSet = false;
        var levelAWasSet = false;
        string? videoFilter = null;
        string? audioFilter = null;
        var loop = false;
        var noAudio = false;

        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            var (name, inlineValue) = SplitOption(token);
            switch (name)
            {
                case "--ffmpeg-path":
                    ffmpegPath = inlineValue ?? ReadNextValue(args, ref i, name);
                    break;
                case "--input":
                    input = inlineValue ?? ReadNextValue(args, ref i, name);
                    break;
                case "--device":
                    device = inlineValue ?? ReadNextValue(args, ref i, name);
                    break;
                case "--format-code":
                    formatCode = inlineValue ?? ReadNextValue(args, ref i, name);
                    break;
                case "--video-size":
                    videoSize = inlineValue ?? ReadNextValue(args, ref i, name);
                    break;
                case "--frame-rate":
                    frameRate = inlineValue ?? ReadNextValue(args, ref i, name);
                    break;
                case "--pixel-format":
                    pixelFormat = inlineValue ?? ReadNextValue(args, ref i, name);
                    break;
                case "--audio-channels":
                    audioChannels = int.Parse(inlineValue ?? ReadNextValue(args, ref i, name), CultureInfo.InvariantCulture);
                    break;
                case "--preroll":
                    preroll = double.Parse(inlineValue ?? ReadNextValue(args, ref i, name), CultureInfo.InvariantCulture);
                    break;
                case "--duplex-mode":
                    duplexMode = inlineValue ?? ReadNextValue(args, ref i, name);
                    break;
                case "--link":
                    linkMode = inlineValue ?? ReadNextValue(args, ref i, name);
                    linkModeWasSet = true;
                    break;
                case "--level-a":
                    levelA = ParseOptionalBool(inlineValue ?? ReadNextValue(args, ref i, name));
                    levelAWasSet = true;
                    break;
                case "--video-filter":
                    videoFilter = inlineValue ?? ReadNextValue(args, ref i, name);
                    break;
                case "--audio-filter":
                    audioFilter = inlineValue ?? ReadNextValue(args, ref i, name);
                    break;
                case "--loop":
                    loop = true;
                    break;
                case "--no-audio":
                    noAudio = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                default:
                    throw new InvalidOperationException($"Unknown option for play: {token}");
            }
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            throw new InvalidOperationException("The play command requires --input.");
        }

        if (string.IsNullOrWhiteSpace(device))
        {
            throw new InvalidOperationException("The play command requires --device.");
        }

        if (device.Contains("SDI 4K", StringComparison.OrdinalIgnoreCase))
        {
            duplexMode = null;
            if (!linkModeWasSet)
            {
                linkMode = "single";
            }

            if (!levelAWasSet)
            {
                levelA = true;
            }
        }

        if (string.Equals(linkMode, "unset", StringComparison.OrdinalIgnoreCase))
        {
            linkMode = null;
        }

        var request = new PlayRequest(
            ffmpegPath,
            input,
            device,
            formatCode,
            videoSize,
            frameRate,
            pixelFormat,
            audioChannels,
            preroll,
            duplexMode,
            linkMode,
            levelA,
            videoFilter,
            audioFilter,
            loop,
            noAudio,
            IsInterlaced: false,
            FieldOrder: null,
            UseTestPattern: false);

        return new PlayOptions(request, dryRun);
    }

    private static (string Name, string? Value) SplitOption(string token)
    {
        var separatorIndex = token.IndexOf('=');
        return separatorIndex < 0
            ? (token, null)
            : (token[..separatorIndex], token[(separatorIndex + 1)..]);
    }

    private static string ReadNextValue(string[] args, ref int index, string optionName)
    {
        if (index + 1 >= args.Length)
        {
            throw new InvalidOperationException($"Missing value for {optionName}.");
        }

        index++;
        return args[index];
    }

    private static bool? ParseOptionalBool(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "true" or "1" or "yes" => true,
            "false" or "0" or "no" => false,
            "unset" => null,
            _ => throw new InvalidOperationException($"Expected true, false, or unset, but got '{value}'."),
        };
    }

    private static bool IsHelpToken(string token) =>
        token is "-h" or "--help" or "help";

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine();
        ShowHelp();
        return 1;
    }

    private static void ShowHelp()
    {
        Console.WriteLine(
            """
            DeckLink SDK Player

            Double-click ffmpegplayer.exe to open the desktop GUI.

            CLI usage:
              ffmpegplayer devices [--ffmpeg-path path]
              ffmpegplayer formats --device "DeckLink Duo (1)" [--ffmpeg-path path]
              ffmpegplayer play --input clip.mp4 --device "DeckLink Duo (1)" [options]

            Play options:
              --format-code CODE      Auto-fill size and frame rate from a progressive DeckLink mode like Hp25
              --video-size WxH        Scale output to a DeckLink size like 1920x1080
              --frame-rate FPS        Set output rate like 25 or 30000/1001
              --pixel-format FORMAT   Defaults to uyvy422
              --audio-channels N      Defaults to 2
              --preroll SECONDS       Defaults to 0.5
              --duplex-mode MODE      half, full, or unset
              --link MODE             DeckLink SDI link: single, dual, quad, or unset
              --level-a VALUE         DeckLink 3G-SDI Level A: true, false, or unset
              --video-filter FILTER   Extra FFmpeg video filter(s) to prepend
              --audio-filter FILTER   Extra FFmpeg audio filter(s)
              --loop                  Loop the input forever
              --no-audio              Play video only
              --dry-run               Print the SDK decoder command without running it
            """);
    }

    private sealed record CommonOptions(string FfmpegPath);

    private sealed record FormatsOptions(string FfmpegPath, string Device);

    private sealed record PlayOptions(PlayRequest Request, bool DryRun);
}

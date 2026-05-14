using System.Collections;
using System.Globalization;
using System.Reflection;
using MediaInfo;

namespace ffmpegplayer;

internal sealed record MediaInfoRow(string Section, string Property, string Value);

internal static class MediaInfoProvider
{
    public static IReadOnlyList<MediaInfoRow> Read(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Media path is empty.", nameof(path));
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Media file was not found.", path);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var rows = new List<MediaInfoRow>();
        var fileInfo = new FileInfo(path);
        rows.Add(new MediaInfoRow("File", "Name", fileInfo.Name));
        rows.Add(new MediaInfoRow("File", "Folder", fileInfo.DirectoryName ?? string.Empty));
        rows.Add(new MediaInfoRow("File", "Size", FormatBytes(fileInfo.Length)));
        rows.Add(new MediaInfoRow("File", "Modified", fileInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));

        var media = new MediaInfoWrapper(path, null);
        rows.Add(new MediaInfoRow("MediaInfo", "Library Version", NullToText(media.Version)));
        rows.Add(new MediaInfoRow("MediaInfo", "Loaded", media.Success ? "Yes" : "No"));

        if (media.Duration > 0)
        {
            rows.Add(new MediaInfoRow("MediaInfo", "Duration", FormatDuration(TimeSpan.FromSeconds(media.Duration))));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var textRows = ParseMediaInfoText(media.Text);
        if (textRows.Count > 0)
        {
            rows.AddRange(textRows);
            return rows;
        }

        if (!media.Success)
        {
            rows.Add(new MediaInfoRow("MediaInfo", "Error", "MediaInfo could not read this file."));
            return rows;
        }

        AddPublicScalarProperties(rows, "Summary", media);
        AddEnumerableRows(rows, "Video", media.VideoStreams);
        AddEnumerableRows(rows, "Audio", media.AudioStreams);
        AddEnumerableRows(rows, "Subtitle", media.Subtitles);
        AddEnumerableRows(rows, "Chapter", media.Chapters);
        AddEnumerableRows(rows, "Menu", media.MenuStreams);
        AddPublicScalarProperties(rows, "Tags", media.Tags);

        return rows;
    }

    private static List<MediaInfoRow> ParseMediaInfoText(string? text)
    {
        var rows = new List<MediaInfoRow>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return rows;
        }

        var section = "MediaInfo";
        foreach (var rawLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var separatorIndex = line.IndexOf(" : ", StringComparison.Ordinal);
            if (separatorIndex < 0)
            {
                section = line.Trim();
                continue;
            }

            var property = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 3)..].Trim();
            if (property.Length == 0 && value.Length == 0)
            {
                continue;
            }

            rows.Add(new MediaInfoRow(section, property, value));
        }

        return rows;
    }

    private static void AddEnumerableRows(List<MediaInfoRow> rows, string section, IEnumerable? items)
    {
        if (items is null)
        {
            return;
        }

        var index = 0;
        foreach (var item in items)
        {
            if (item is null)
            {
                continue;
            }

            index++;
            AddPublicScalarProperties(rows, $"{section} {index}", item);
        }
    }

    private static void AddPublicScalarProperties(List<MediaInfoRow> rows, string section, object? source)
    {
        if (source is null)
        {
            return;
        }

        var properties = source.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public);
        foreach (var property in properties)
        {
            if (property.GetIndexParameters().Length > 0 || string.Equals(property.Name, "Text", StringComparison.Ordinal))
            {
                continue;
            }

            object? value;
            try
            {
                value = property.GetValue(source);
            }
            catch (TargetInvocationException)
            {
                continue;
            }

            if (value is null)
            {
                continue;
            }

            if (value is IEnumerable && value is not string)
            {
                continue;
            }

            if (!IsScalarType(property.PropertyType))
            {
                continue;
            }

            var displayValue = FormatValue(value);
            if (string.IsNullOrWhiteSpace(displayValue) || displayValue == "0")
            {
                continue;
            }

            rows.Add(new MediaInfoRow(section, SplitPascalCase(property.Name), displayValue));
        }
    }

    private static bool IsScalarType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive ||
            type.IsEnum ||
            type == typeof(string) ||
            type == typeof(decimal) ||
            type == typeof(DateTime) ||
            type == typeof(TimeSpan);
    }

    private static string FormatValue(object value)
    {
        return value switch
        {
            bool boolValue => boolValue ? "Yes" : "No",
            TimeSpan timeSpan => FormatDuration(timeSpan),
            DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => value.ToString() ?? string.Empty,
        };
    }

    private static string SplitPascalCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var chars = new List<char>(value.Length + 8) { value[0] };
        for (var i = 1; i < value.Length; i++)
        {
            if (char.IsUpper(value[i]) && !char.IsWhiteSpace(value[i - 1]))
            {
                chars.Add(' ');
            }

            chars.Add(value[i]);
        }

        return new string(chars.ToArray());
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalHours >= 1
            ? duration.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture)
            : duration.ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024d && unit < units.Length - 1)
        {
            value /= 1024d;
            unit++;
        }

        return string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1} ({2:N0} bytes)", value, units[unit], bytes);
    }

    private static string NullToText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "--" : value.Trim();
    }
}

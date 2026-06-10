using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using NBMS.Core.Models;

namespace NBMS.Core.Services;

public static class NbmsJson
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static readonly JsonSerializerOptions CompactSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static readonly JsonSerializerOptions PrettyCompactSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var value = await JsonSerializer.DeserializeAsync<T>(stream, SerializerOptions, cancellationToken);
        return value ?? throw new InvalidDataException($"JSON was empty: {path}");
    }

    public static async Task<NbmsChart> ReadChartAsync(string path, CancellationToken cancellationToken = default)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        return ReadChartFromJson(json, path);
    }

    public static NbmsChart ReadChart(string path)
    {
        var json = File.ReadAllText(path);
        return ReadChartFromJson(json, path);
    }

    public static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, value, SerializerOptions, cancellationToken);
        await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
    }

    public static async Task WriteCompactAsync<T>(string path, T value, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, value, CompactSerializerOptions, cancellationToken);
        await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
    }

    public static Task WriteCompactChartAsync(string path, NbmsChart chart, CancellationToken cancellationToken = default)
    {
        var compact = new CompactChartConverter().ToCompact(chart);
        return WriteCompactAsync(path, compact, cancellationToken);
    }

    public static async Task WritePrettyCompactChartAsync(string path, NbmsChart chart, CancellationToken cancellationToken = default)
    {
        var compact = new CompactChartConverter().ToCompact(chart);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, BuildReadableCompactChartJson(compact), new UTF8Encoding(false), cancellationToken);
    }

    private static NbmsChart ReadChartFromJson(string json, string path)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        if (document.RootElement.TryGetProperty("encoding", out var encoding) &&
            encoding.ValueKind == JsonValueKind.String &&
            encoding.GetString()?.Equals("compact-json", StringComparison.OrdinalIgnoreCase) == true)
        {
            var compact = JsonSerializer.Deserialize<CompactChart>(json, CompactSerializerOptions)
                ?? throw new InvalidDataException($"Compact chart JSON was empty: {path}");
            return new CompactChartConverter().FromCompact(compact);
        }

        return JsonSerializer.Deserialize<NbmsChart>(json, SerializerOptions)
            ?? throw new InvalidDataException($"Chart JSON was empty: {path}");
    }

    private static string BuildReadableCompactChartJson(CompactChart chart)
    {
        var builder = new StringBuilder();
        builder.AppendLine("{");
        AppendProperty(builder, "format", chart.Format, hasNext: true);
        AppendProperty(builder, "version", chart.Version, hasNext: true);
        AppendProperty(builder, "encoding", chart.Encoding, hasNext: true);
        AppendProperty(builder, "chartId", chart.ChartId, hasNext: true);
        AppendProperty(builder, "mode", chart.Mode, hasNext: true);
        AppendProperty(builder, "resolution", chart.Resolution, hasNext: true);
        builder.AppendLine("  \"dictionary\": {");
        AppendProperty(builder, "lanes", chart.Dictionary.Lanes, hasNext: true, indent: 4);
        AppendProperty(builder, "audio", chart.Dictionary.Audio, hasNext: true, indent: 4);
        AppendProperty(builder, "media", chart.Dictionary.Media, hasNext: false, indent: 4);
        builder.AppendLine("  },");
        AppendTupleArray(builder, "timing", chart.Timing, hasNext: true);
        AppendTupleArray(builder, "notes", chart.Notes, hasNext: true);
        AppendTupleArray(builder, "backgroundAudio", chart.BackgroundAudio, hasNext: true);
        AppendTupleArray(builder, "mediaEvents", chart.MediaEvents, hasNext: chart.Metadata is not null || chart.Extensions is not null);
        if (chart.Metadata is not null)
        {
            AppendProperty(builder, "metadata", chart.Metadata, hasNext: chart.Extensions is not null);
        }

        if (chart.Extensions is not null)
        {
            AppendProperty(builder, "extensions", chart.Extensions, hasNext: false);
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    private static void AppendProperty(StringBuilder builder, string name, object? value, bool hasNext, int indent = 2)
    {
        builder
            .Append(' ', indent)
            .Append(JsonSerializer.Serialize(name))
            .Append(": ")
            .Append(JsonSerializer.Serialize(value, CompactSerializerOptions));
        if (hasNext)
        {
            builder.Append(',');
        }

        builder.AppendLine();
    }

    private static void AppendTupleArray(StringBuilder builder, string name, IReadOnlyList<object?[]> tuples, bool hasNext)
    {
        builder.Append("  ").Append(JsonSerializer.Serialize(name)).AppendLine(": [");
        for (var index = 0; index < tuples.Count; index++)
        {
            builder
                .Append("    ")
                .Append(JsonSerializer.Serialize(tuples[index], CompactSerializerOptions));
            if (index < tuples.Count - 1)
            {
                builder.Append(',');
            }

            builder.AppendLine();
        }

        builder.Append("  ]");
        if (hasNext)
        {
            builder.Append(',');
        }

        builder.AppendLine();
    }
}

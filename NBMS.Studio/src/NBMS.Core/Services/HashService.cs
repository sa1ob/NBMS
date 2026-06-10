using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NBMS.Core.Models;

namespace NBMS.Core.Services;

public sealed class HashService
{
    public async Task<string> ComputeFileSha256Async(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return "sha256-" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    public string ComputeCanonicalJsonHash<T>(T value)
    {
        var canonical = value is NbmsChart chart
            ? Canonicalize(JsonSerializer.SerializeToElement(
                new CompactChartConverter().ToCompact(chart),
                NbmsJson.CompactSerializerOptions))
            : Canonicalize(JsonSerializer.SerializeToElement(value, NbmsJson.SerializerOptions));
        var bytes = Encoding.UTF8.GetBytes(canonical);
        return "sha256-" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    // NBMSの譜面ハッシュは、空白やキー順に左右されない正規化JSONで計算する。
    private static string Canonicalize(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject()
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .Select(property => JsonSerializer.Serialize(property.Name) + ":" + Canonicalize(property.Value))) + "}",
            JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(Canonicalize)) + "]",
            JsonValueKind.String => JsonSerializer.Serialize(element.GetString()),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            _ => "null"
        };
    }
}

using NBMS.Core.Models;

namespace NBMS.Core.Services;

public static class MediaEventStateResolver
{
    public static IReadOnlyList<MediaEvent> OrderEvents(IEnumerable<MediaEvent> events)
    {
        return events
            .OrderBy(item => item.Tick)
            .ThenBy(item => ResolveSortLayer(item))
            .ThenBy(item => ResolveTypePriority(item.Type))
            .ThenBy(item => item.MediaId, StringComparer.Ordinal)
            .ToList();
    }

    public static IReadOnlyDictionary<int, MediaLayerState> ResolveStateAtTick(
        IEnumerable<MediaEvent> events,
        int startTick)
    {
        var layers = new SortedDictionary<int, MediaLayerState>();
        foreach (var item in OrderEvents(events).Where(item => item.Tick <= startTick))
        {
            var type = NormalizeType(item.Type);
            var layer = ResolveLayer(item.Type, item.Layer);
            if (type.Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                if (layer is null)
                {
                    layers.Clear();
                }
                else
                {
                    layers.Remove(layer.Value);
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(item.MediaId))
            {
                continue;
            }

            layers[layer ?? 0] = new MediaLayerState(
                Layer: layer ?? 0,
                MediaId: item.MediaId,
                Type: type,
                StartedAtTick: item.Tick);
        }

        return layers;
    }

    public static string NormalizeType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return "bga";
        }

        return type.Equals("image", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("video", StringComparison.OrdinalIgnoreCase)
            ? "bga"
            : type;
    }

    public static int? ResolveLayer(string? type, int? layer)
    {
        if (NormalizeType(type).Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            return layer;
        }

        if (layer is not null)
        {
            return layer;
        }

        return NormalizeType(type).Equals("layer", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    public static int ResolveTypePriority(string? type)
    {
        return NormalizeType(type).ToLowerInvariant() switch
        {
            "clear" => 0,
            "bga" => 10,
            "layer" => 20,
            "poor" => 30,
            "banner" => 40,
            "stagefile" => 50,
            "preview" => 60,
            _ => 100
        };
    }

    private static int ResolveSortLayer(MediaEvent item)
    {
        return ResolveLayer(item.Type, item.Layer) ?? int.MinValue;
    }
}

public sealed record MediaLayerState(int Layer, string MediaId, string Type, int StartedAtTick);

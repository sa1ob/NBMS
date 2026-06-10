using NBMS.Core.Models;

namespace NBMS.Studio.App.ViewModels;

public sealed class ChartRow
{
    public string Id { get; set; } = "";
    public string File { get; set; } = "";
    public string Mode { get; set; } = "";
    public int Difficulty { get; set; }
    public string LevelName { get; set; } = "";

    public override string ToString()
    {
        var label = string.IsNullOrWhiteSpace(LevelName) ? Id : LevelName;
        return $"{label} / {Mode} / Lv.{Difficulty}";
    }
}

public sealed class NoteRow
{
    public int Tick { get; set; }
    public string Lane { get; set; } = "";
    public string Type { get; set; } = "tap";
    public string AudioId { get; set; } = "";
    public int? DurationTicks { get; set; }

    public static NoteRow FromNote(NoteEvent note)
    {
        return new NoteRow
        {
            Tick = note.Tick,
            Lane = note.Lane,
            Type = note.Type,
            AudioId = note.AudioId ?? "",
            DurationTicks = note.DurationTicks
        };
    }
}

public sealed class AudioRow
{
    public string AudioId { get; set; } = "";
    public string Codec { get; set; } = "";
    public int DurationMs { get; set; }
    public int SampleRate { get; set; }
    public int Channels { get; set; }
    public bool Encrypted { get; set; }
    public string Path { get; set; } = "";

    public override string ToString()
    {
        var codec = string.IsNullOrWhiteSpace(Codec) ? "unknown" : Codec;
        return $"{AudioId} / {codec} / {Path}";
    }
}

public sealed class MediaRow
{
    public int Tick { get; set; }
    public string MediaId { get; set; } = "";
    public string Type { get; set; } = "";
    public int? Layer { get; set; }

    public static MediaRow FromMediaEvent(MediaEvent mediaEvent)
    {
        return new MediaRow
        {
            Tick = mediaEvent.Tick,
            MediaId = mediaEvent.MediaId,
            Type = mediaEvent.Type,
            Layer = mediaEvent.Layer
        };
    }

    public override string ToString()
    {
        var layer = Layer.HasValue ? $" L{Layer.Value}" : "";
        return $"{Tick} / {MediaId} / {Type}{layer}";
    }
}

public sealed class MediaAssetRow
{
    public string MediaId { get; set; } = "";
    public string Type { get; set; } = "";
    public string MimeType { get; set; } = "";
    public string Path { get; set; } = "";
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? DurationMs { get; set; }

    public override string ToString()
    {
        var type = string.IsNullOrWhiteSpace(Type) ? "unknown" : Type;
        return $"{MediaId} / {type} / {Path}";
    }
}

public sealed class IssueRow
{
    public string Severity { get; set; } = "";
    public string Source { get; set; } = "";
    public string Message { get; set; } = "";
    public string ReferenceType { get; set; } = "";
    public int? Index { get; set; }
    public string AudioId { get; set; } = "";
}

public sealed class TimelineRow
{
    public int Tick { get; set; }
    public double TimeSeconds { get; set; }
    public string Kind { get; set; } = "";
    public string Lane { get; set; } = "";
    public string Detail { get; set; } = "";
    public int? DurationTicks { get; set; }
}

public sealed class MeasureGridLineRow
{
    public int Tick { get; set; }
    public int MeasureNumber { get; set; }
    public int DivisionIndex { get; set; }
    public bool IsMeasureStart { get; set; }
}

public sealed class EventRow
{
    public int Tick { get; set; }
    public double TimeSeconds { get; set; }
    public string Type { get; set; } = "";
    public string Lane { get; set; } = "";
    public string Detail { get; set; } = "";

    public override string ToString()
    {
        return $"{Tick} / {Type} / {Lane} / {Detail}";
    }
}

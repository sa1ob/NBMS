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
}

public sealed class IssueRow
{
    public string Severity { get; set; } = "";
    public string Source { get; set; } = "";
    public string Message { get; set; } = "";
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

public sealed class EventRow
{
    public int Tick { get; set; }
    public double TimeSeconds { get; set; }
    public string Type { get; set; } = "";
    public string Lane { get; set; } = "";
    public string Detail { get; set; } = "";
}

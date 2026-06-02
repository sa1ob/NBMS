namespace NBMS.Core.Models;

public sealed class NbmsProject
{
    public string RootDirectory { get; set; } = "";
    public string HeaderPath { get; set; } = "";
    public NbmsHeader Header { get; set; } = new();
    public List<LoadedChart> Charts { get; set; } = [];
    public AudioManifest? AudioManifest { get; set; }
    public List<ProjectIssue> Issues { get; set; } = [];
}

public sealed class LoadedChart
{
    public ChartReference Reference { get; set; } = new();
    public string Path { get; set; } = "";
    public NbmsChart Chart { get; set; } = new();
}

public sealed class ProjectIssue
{
    public string Severity { get; set; } = "Info";
    public string Source { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class ReferenceIssue
{
    public string ChartId { get; set; } = "";
    public string ReferenceType { get; set; } = "";
    public int Index { get; set; }
    public string AudioId { get; set; } = "";
}

public sealed class TimelineItem
{
    public int Tick { get; set; }
    public double TimeSeconds { get; set; }
    public string Kind { get; set; } = "";
    public string Lane { get; set; } = "";
    public string Detail { get; set; } = "";
}


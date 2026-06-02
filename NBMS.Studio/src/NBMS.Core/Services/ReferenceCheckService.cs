using NBMS.Core.Models;

namespace NBMS.Core.Services;

public sealed class ReferenceCheckService
{
    public List<ReferenceIssue> FindMissingAudioReferences(NbmsProject project)
    {
        var available = project.AudioManifest?.Entries
            .Select(entry => entry.AudioId)
            .ToHashSet(StringComparer.Ordinal) ?? [];

        var issues = new List<ReferenceIssue>();

        foreach (var loadedChart in project.Charts)
        {
            for (var index = 0; index < loadedChart.Chart.Notes.Count; index++)
            {
                var audioId = loadedChart.Chart.Notes[index].AudioId;
                if (!string.IsNullOrWhiteSpace(audioId) && !available.Contains(audioId))
                {
                    issues.Add(new ReferenceIssue
                    {
                        ChartId = loadedChart.Reference.Id,
                        ReferenceType = "note",
                        Index = index,
                        AudioId = audioId
                    });
                }
            }

            for (var index = 0; index < loadedChart.Chart.BackgroundAudio.Count; index++)
            {
                var audioId = loadedChart.Chart.BackgroundAudio[index].AudioId;
                if (!available.Contains(audioId))
                {
                    issues.Add(new ReferenceIssue
                    {
                        ChartId = loadedChart.Reference.Id,
                        ReferenceType = "backgroundAudio",
                        Index = index,
                        AudioId = audioId
                    });
                }
            }
        }

        return issues;
    }
}


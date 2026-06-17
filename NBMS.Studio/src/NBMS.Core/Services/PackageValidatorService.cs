using NBMS.Core.Models;

namespace NBMS.Core.Services;

public sealed class PackageValidatorService
{
    private readonly HashService _hashService = new();
    private readonly AudioArchiveService _audioArchiveService = new();
    private readonly MediaArchiveService _mediaArchiveService = new();
    private readonly ReferenceCheckService _referenceCheckService = new();

    public async Task<List<ProjectIssue>> ValidateProjectAsync(
        NbmsProject project,
        CancellationToken cancellationToken = default)
    {
        var issues = new List<ProjectIssue>();
        AddHeaderIssues(project, issues);
        await AddFileHashIssuesAsync(project, issues, cancellationToken);
        await AddArchiveIntegrityIssuesAsync(project, issues, cancellationToken);
        AddReferenceIssues(project, issues);
        return issues;
    }

    private static void AddHeaderIssues(NbmsProject project, List<ProjectIssue> issues)
    {
        if (!File.Exists(project.HeaderPath))
        {
            issues.Add(CreateIssue("Error", "PKG_HEADER_MISSING", "Header", "header", $"header file missing: {project.HeaderPath}"));
        }

        if (project.Header.Audio is not { File.Length: > 0 })
        {
            issues.Add(CreateIssue("Error", "PKG_AUDIO_REF_MISSING", "Audio", "header.audio", "audio file reference is missing."));
        }

        foreach (var chart in project.Charts)
        {
            if (!File.Exists(chart.Path))
            {
                issues.Add(CreateIssue("Error", "PKG_CHART_FILE_MISSING", chart.Reference.Id, $"chart:{chart.Reference.Id}", $"chart file missing: {chart.Reference.File}"));
            }
        }
    }

    private async Task AddFileHashIssuesAsync(
        NbmsProject project,
        List<ProjectIssue> issues,
        CancellationToken cancellationToken)
    {
        var audioPath = ResolveProjectPath(project, project.Header.Audio.File);
        if (!File.Exists(audioPath))
        {
            issues.Add(CreateIssue("Error", "PKG_AUDIO_FILE_MISSING", "Audio", "header.audio", $"audio pack missing: {project.Header.Audio.File}"));
        }
        else if (!string.IsNullOrWhiteSpace(project.Header.Audio.Hash))
        {
            await AddFileHashIssueIfNeededAsync(issues, "Audio", "header.audio", "PKG_AUDIO_HASH_MISMATCH", project.Header.Audio.Hash, audioPath, cancellationToken);
        }

        if (project.Header.Media is { File.Length: > 0 } media)
        {
            var mediaPath = ResolveProjectPath(project, media.File);
            if (!File.Exists(mediaPath))
            {
                issues.Add(CreateIssue(media.Optional == true ? "Warning" : "Error", "PKG_MEDIA_FILE_MISSING", "Media", "header.media", $"media pack missing: {media.File}"));
            }
            else if (!string.IsNullOrWhiteSpace(media.Hash))
            {
                await AddFileHashIssueIfNeededAsync(issues, "Media", "header.media", "PKG_MEDIA_HASH_MISMATCH", media.Hash, mediaPath, cancellationToken);
            }
        }

        foreach (var chart in project.Charts)
        {
            var actual = _hashService.ComputeCanonicalJsonHash(chart.Chart);
            if (!string.Equals(actual, chart.Reference.Hash, StringComparison.Ordinal))
            {
                issues.Add(CreateIssue(
                    "Error",
                    "PKG_CHART_HASH_MISMATCH",
                    chart.Reference.Id,
                    $"chart:{chart.Reference.Id}",
                    $"chart hash mismatch: expected={chart.Reference.Hash}, actual={actual}"));
            }
        }
    }

    private async Task AddFileHashIssueIfNeededAsync(
        List<ProjectIssue> issues,
        string source,
        string targetReference,
        string code,
        string expected,
        string path,
        CancellationToken cancellationToken)
    {
        var actual = await _hashService.ComputeFileSha256Async(path, cancellationToken);
        if (string.Equals(expected, actual, StringComparison.Ordinal))
        {
            return;
        }

        issues.Add(CreateIssue("Error", code, source, targetReference, $"file hash mismatch: expected={expected}, actual={actual}"));
    }

    private async Task AddArchiveIntegrityIssuesAsync(
        NbmsProject project,
        List<ProjectIssue> issues,
        CancellationToken cancellationToken)
    {
        if (project.AudioManifest is not null)
        {
            var audioPath = ResolveProjectPath(project, project.Header.Audio.File);
            if (File.Exists(audioPath))
            {
                var archiveIssues = await _audioArchiveService.ValidateArchiveEntriesAsync(audioPath, project.AudioManifest, cancellationToken);
                issues.AddRange(archiveIssues.Select(issue => CreateIssue(
                    issue.Severity,
                    "PKG_AUDIO_ARCHIVE_ENTRY_INVALID",
                    "Audio",
                    $"audio:{issue.AssetId}",
                    $"{issue.AssetId} / {issue.Path}: {issue.Message}")));
            }
        }

        if (project.MediaManifest is not null && project.Header.Media is { File.Length: > 0 } media)
        {
            var mediaPath = ResolveProjectPath(project, media.File);
            if (File.Exists(mediaPath))
            {
                var archiveIssues = await _mediaArchiveService.ValidateArchiveEntriesAsync(mediaPath, project.MediaManifest, cancellationToken);
                issues.AddRange(archiveIssues.Select(issue => CreateIssue(
                    issue.Severity,
                    "PKG_MEDIA_ARCHIVE_ENTRY_INVALID",
                    "Media",
                    $"media:{issue.AssetId}",
                    $"{issue.AssetId} / {issue.Path}: {issue.Message}")));
            }
        }
    }

    private void AddReferenceIssues(NbmsProject project, List<ProjectIssue> issues)
    {
        foreach (var issue in _referenceCheckService.FindMissingAudioReferences(project))
        {
            issues.Add(CreateIssue(
                "Error",
                "NBMS_AUDIO_REF_MISSING",
                issue.ChartId,
                $"{issue.ChartId}:{issue.ReferenceType}[{issue.Index}]",
                $"missing audio reference: {issue.ReferenceType}[{issue.Index}] {issue.AudioId}"));
        }
    }

    private static ProjectIssue CreateIssue(
        string severity,
        string code,
        string source,
        string targetReference,
        string message)
    {
        return new ProjectIssue
        {
            Severity = severity,
            Code = code,
            Source = source,
            TargetReference = targetReference,
            Message = message
        };
    }

    private static string ResolveProjectPath(NbmsProject project, string relativePath)
    {
        return Path.GetFullPath(Path.Combine(project.RootDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }
}

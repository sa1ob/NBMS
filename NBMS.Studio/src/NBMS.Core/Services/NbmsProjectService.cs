using NBMS.Core.Extensions;
using NBMS.Core.Extensions.BuiltIn;
using NBMS.Core.Models;

namespace NBMS.Core.Services;

public sealed class NbmsProjectService
{
    private readonly HashService _hashService = new();
    private readonly AudioArchiveService _audioArchiveService = new();
    private readonly MediaArchiveService _mediaArchiveService = new();
    private readonly ReferenceCheckService _referenceCheckService = new();

    public ExtensionRegistry ExtensionRegistry { get; } = CreateDefaultExtensionRegistry();

    public async Task<NbmsProject> OpenHeaderAsync(string headerPath, CancellationToken cancellationToken = default)
    {
        var fullHeaderPath = Path.GetFullPath(headerPath);
        var rootDirectory = Path.GetDirectoryName(fullHeaderPath)
            ?? throw new InvalidDataException("ヘッダーファイルの親ディレクトリを解決できません。");

        var header = await NbmsJson.ReadAsync<NbmsHeader>(fullHeaderPath, cancellationToken);
        var project = new NbmsProject
        {
            RootDirectory = rootDirectory,
            HeaderPath = fullHeaderPath,
            Header = header
        };

        await LoadChartsAsync(project, cancellationToken);
        await LoadAudioManifestAsync(project, cancellationToken);
        await LoadMediaManifestAsync(project, cancellationToken);
        await ValidateHashesAsync(project, cancellationToken);
        AddReferenceIssues(project);

        return project;
    }

    public async Task SaveAsync(NbmsProject project, CancellationToken cancellationToken = default)
    {
        // 保存前に譜面と音源のハッシュを更新し、ヘッダーの整合性を保つ。
        foreach (var chart in project.Charts)
        {
            await NbmsJson.WriteCompactChartAsync(chart.Path, chart.Chart, cancellationToken);
            chart.Reference.Hash = _hashService.ComputeCanonicalJsonHash(chart.Chart);
            chart.Reference.HashAlgorithm = "sha256-compact-canonical-json";
        }

        var audioPath = ResolveProjectPath(project, project.Header.Audio.File);
        if (project.AudioManifest is not null)
        {
            await _audioArchiveService.WriteManifestAsync(audioPath, project.AudioManifest, cancellationToken);
        }

        project.Header.Audio.Hash = await _hashService.ComputeFileSha256Async(audioPath, cancellationToken);
        if (project.Header.Media is { File.Length: > 0 } media && project.MediaManifest is not null)
        {
            var mediaPath = ResolveProjectPath(project, media.File);
            await _mediaArchiveService.WriteManifestAsync(mediaPath, project.MediaManifest, cancellationToken);
            media.Hash = await _hashService.ComputeFileSha256Async(mediaPath, cancellationToken);
        }

        await NbmsJson.WriteAsync(project.HeaderPath, project.Header, cancellationToken);
    }

    private async Task LoadChartsAsync(NbmsProject project, CancellationToken cancellationToken)
    {
        foreach (var chartReference in project.Header.Charts)
        {
            var chartPath = ResolveProjectPath(project, chartReference.File);
            var chart = await NbmsJson.ReadChartAsync(chartPath, cancellationToken);
            project.Charts.Add(new LoadedChart
            {
                Reference = chartReference,
                Path = chartPath,
                Chart = chart
            });
        }
    }

    private async Task LoadAudioManifestAsync(NbmsProject project, CancellationToken cancellationToken)
    {
        var audioPath = ResolveProjectPath(project, project.Header.Audio.File);
        project.AudioManifest = await _audioArchiveService.ReadManifestAsync(audioPath, cancellationToken);
    }

    private async Task LoadMediaManifestAsync(NbmsProject project, CancellationToken cancellationToken)
    {
        if (project.Header.Media is not { File.Length: > 0 } media)
        {
            return;
        }

        var mediaPath = ResolveProjectPath(project, media.File);
        if (!File.Exists(mediaPath))
        {
            project.Issues.Add(new ProjectIssue
            {
                Severity = media.Optional == true ? "Warning" : "Error",
                Source = "Media",
                Message = $"media packが見つかりません: {media.File}"
            });
            return;
        }

        project.MediaManifest = await _mediaArchiveService.ReadManifestAsync(mediaPath, cancellationToken);
    }

    private async Task ValidateHashesAsync(NbmsProject project, CancellationToken cancellationToken)
    {
        var audioPath = ResolveProjectPath(project, project.Header.Audio.File);
        var audioHash = await _hashService.ComputeFileSha256Async(audioPath, cancellationToken);
        if (!string.Equals(audioHash, project.Header.Audio.Hash, StringComparison.Ordinal))
        {
            project.Issues.Add(new ProjectIssue
            {
                Severity = "Error",
                Source = "Audio",
                Message = $"音源パックハッシュが一致しません。expected={project.Header.Audio.Hash}, actual={audioHash}"
            });
        }

        foreach (var chart in project.Charts)
        {
            var chartHash = _hashService.ComputeCanonicalJsonHash(chart.Chart);
            if (!string.Equals(chartHash, chart.Reference.Hash, StringComparison.Ordinal))
            {
                project.Issues.Add(new ProjectIssue
                {
                    Severity = "Error",
                    Source = chart.Reference.Id,
                    Message = $"譜面ハッシュが一致しません。expected={chart.Reference.Hash}, actual={chartHash}"
                });
            }
        }

        if (project.Header.Media is { File.Length: > 0, Hash.Length: > 0 } media)
        {
            var mediaPath = ResolveProjectPath(project, media.File);
            if (File.Exists(mediaPath))
            {
                var mediaHash = await _hashService.ComputeFileSha256Async(mediaPath, cancellationToken);
                if (!string.Equals(mediaHash, media.Hash, StringComparison.Ordinal))
                {
                    project.Issues.Add(new ProjectIssue
                    {
                        Severity = "Error",
                        Source = "Media",
                        Message = $"media pack hashが一致しません。Expected={media.Hash}, actual={mediaHash}"
                    });
                }
            }
        }
    }

    private void AddReferenceIssues(NbmsProject project)
    {
        foreach (var issue in _referenceCheckService.FindMissingAudioReferences(project))
        {
            project.Issues.Add(new ProjectIssue
            {
                Severity = "Error",
                Source = issue.ChartId,
                Message = $"{issue.ReferenceType}[{issue.Index}] が存在しない audioId を参照しています: {issue.AudioId}"
            });
        }
    }

    private static string ResolveProjectPath(NbmsProject project, string relativePath)
    {
        return Path.GetFullPath(Path.Combine(project.RootDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static ExtensionRegistry CreateDefaultExtensionRegistry()
    {
        var registry = new ExtensionRegistry();
        registry.Register(new StopExtensionModule());
        registry.Register(new ScrollExtensionModule());
        return registry;
    }
}

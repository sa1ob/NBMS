namespace NBMS.Core.Services;

public sealed record ArchiveIntegrityIssue(
    string Severity,
    string AssetId,
    string Path,
    string Message);

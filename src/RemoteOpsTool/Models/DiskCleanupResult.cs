namespace RemoteOpsTool.Models;

public sealed record DiskCleanupTarget(
    string Path,
    bool DeleteDirectory);

public sealed record DiskCleanupProgress(
    int Completed,
    int Total,
    string CurrentPath,
    bool TargetSucceeded,
    string Message);

public sealed record DiskCleanupTargetResult(
    string Path,
    bool Success,
    string Message);

public sealed class DiskCleanupResult
{
    public DiskCleanupResult(IReadOnlyList<DiskCleanupTargetResult> targets)
    {
        Targets = targets;
    }

    public IReadOnlyList<DiskCleanupTargetResult> Targets { get; }
    public bool Success => Targets.All(target => target.Success);
    public int SucceededCount => Targets.Count(target => target.Success);
    public int FailedCount => Targets.Count - SucceededCount;
}

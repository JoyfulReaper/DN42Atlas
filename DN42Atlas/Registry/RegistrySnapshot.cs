namespace DN42Atlas.Registry;

public enum RegistrySnapshotStatus
{
    Fresh,
    Stale,
    Dirty,
    Unknown
}

public sealed record RegistrySnapshot(
    string RegistryRoot,
    string? CommitSha,
    DateTimeOffset? CommitTimestamp,
    DateTimeOffset? ObservedAt,
    TimeSpan? Age,
    RegistrySnapshotStatus Status)
{
    public bool IsSafeForAutomaticApproval =>
        Status == RegistrySnapshotStatus.Fresh;
}

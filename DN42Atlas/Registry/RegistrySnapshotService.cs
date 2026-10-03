namespace DN42Atlas.Registry;

public sealed class RegistrySnapshotService
{
    private readonly string registryRoot;
    private readonly TimeSpan maximumAge;
    private readonly IRegistryRepositoryInspector repositoryInspector;
    private readonly TimeProvider timeProvider;

    public RegistrySnapshotService(
        string registryRoot,
        TimeSpan maximumAge,
        IRegistryRepositoryInspector? repositoryInspector = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryRoot);

        if (maximumAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumAge));

        this.registryRoot = Path.GetFullPath(registryRoot);
        this.maximumAge = maximumAge;
        this.repositoryInspector =
            repositoryInspector ?? new GitRegistryRepositoryInspector();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public RegistrySnapshot GetSnapshot()
    {
        if (!Directory.Exists(registryRoot) ||
            !RegistryCheckoutLayout.IsPresent(registryRoot) ||
            !repositoryInspector.TryInspect(
                registryRoot,
                out var repository) ||
            repository is null)
        {
            return Unknown();
        }

        if (!RegistrySnapshotObservationStore.TryRead(
                repository.GitDirectory,
                out var observation) ||
            observation is null ||
            !string.Equals(
                repository.CommitSha,
                observation.CommitSha,
                StringComparison.OrdinalIgnoreCase))
        {
            return repository.IsWorkingTreeClean
                ? Unknown(repository)
                : Dirty(repository);
        }

        var age = timeProvider.GetUtcNow() - observation.ObservedAt.ToUniversalTime();

        if (age < TimeSpan.Zero)
        {
            return repository.IsWorkingTreeClean
                ? Unknown(repository)
                : Dirty(repository);
        }

        return new RegistrySnapshot(
            registryRoot,
            repository.CommitSha,
            repository.CommitTimestamp,
            observation.ObservedAt.ToUniversalTime(),
            age,
            !repository.IsWorkingTreeClean
                ? RegistrySnapshotStatus.Dirty
                : age <= maximumAge
                    ? RegistrySnapshotStatus.Fresh
                    : RegistrySnapshotStatus.Stale);
    }

    private RegistrySnapshot Unknown(
        RegistryRepositoryState? repository = null) =>
        new(
            registryRoot,
            repository?.CommitSha,
            repository?.CommitTimestamp,
            null,
            null,
            RegistrySnapshotStatus.Unknown);

    private RegistrySnapshot Dirty(
        RegistryRepositoryState repository) =>
        new(
            registryRoot,
            repository.CommitSha,
            repository.CommitTimestamp,
            null,
            null,
            RegistrySnapshotStatus.Dirty);
}

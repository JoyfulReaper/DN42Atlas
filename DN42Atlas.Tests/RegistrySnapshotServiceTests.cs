using DN42Atlas.Registry;

namespace DN42Atlas.Tests;

[TestClass]
public sealed class RegistrySnapshotServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 3, 20, 14, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task RecentObservationIsFreshAndSurfacesCommit()
    {
        using var registry = new SnapshotRegistry();
        await registry.ObserveAsync(Now - TimeSpan.FromHours(2));

        var snapshot = registry.Service(TimeSpan.FromHours(72)).GetSnapshot();

        Assert.AreEqual(RegistrySnapshotStatus.Fresh, snapshot.Status);
        Assert.IsTrue(snapshot.IsSafeForAutomaticApproval);
        Assert.AreEqual(SnapshotRegistry.CommitSha, snapshot.CommitSha);
        Assert.AreEqual(Now - TimeSpan.FromHours(2), snapshot.ObservedAt);
        Assert.AreEqual(TimeSpan.FromHours(2), snapshot.Age);
    }

    [TestMethod]
    public async Task ObservationOlderThanThresholdIsStale()
    {
        using var registry = new SnapshotRegistry();
        await registry.ObserveAsync(Now - TimeSpan.FromHours(73));

        var snapshot = registry.Service(TimeSpan.FromHours(72)).GetSnapshot();

        Assert.AreEqual(RegistrySnapshotStatus.Stale, snapshot.Status);
        Assert.IsFalse(snapshot.IsSafeForAutomaticApproval);
    }

    [TestMethod]
    public async Task ObservationAtThresholdBoundaryIsFresh()
    {
        using var registry = new SnapshotRegistry();
        await registry.ObserveAsync(Now - TimeSpan.FromHours(72));

        var snapshot = registry.Service(TimeSpan.FromHours(72)).GetSnapshot();

        Assert.AreEqual(RegistrySnapshotStatus.Fresh, snapshot.Status);
    }

    [TestMethod]
    public void MissingRegistryPathIsUnknownAndUnsafe()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"dn42atlas-missing-{Guid.NewGuid():N}");
        var service = new RegistrySnapshotService(
            path,
            TimeSpan.FromHours(72));

        var snapshot = service.GetSnapshot();

        Assert.AreEqual(RegistrySnapshotStatus.Unknown, snapshot.Status);
        Assert.IsFalse(snapshot.IsSafeForAutomaticApproval);
    }

    [TestMethod]
    public void NonGitRegistryPathIsUnknownAndUnsafe()
    {
        using var registry = new EmptyDirectory();
        CreateRegistryLayout(registry.Path);
        var service = new RegistrySnapshotService(
            registry.Path,
            TimeSpan.FromHours(72));

        var snapshot = service.GetSnapshot();

        Assert.AreEqual(RegistrySnapshotStatus.Unknown, snapshot.Status);
        Assert.IsFalse(snapshot.IsSafeForAutomaticApproval);
    }

    [TestMethod]
    public void MissingObservationIsUnknownAndUnsafe()
    {
        using var registry = new SnapshotRegistry();

        var snapshot = registry.Service(TimeSpan.FromHours(72)).GetSnapshot();

        Assert.AreEqual(RegistrySnapshotStatus.Unknown, snapshot.Status);
        Assert.AreEqual(SnapshotRegistry.CommitSha, snapshot.CommitSha);
        Assert.IsFalse(snapshot.IsSafeForAutomaticApproval);
    }

    [TestMethod]
    public void UnreadableObservationIsUnknownAndUnsafe()
    {
        using var registry = new SnapshotRegistry();
        File.WriteAllText(
            Path.Combine(
                registry.GitDirectory,
                RegistrySnapshotObservationStore.FileName),
            "{not-json");

        var snapshot = registry.Service(TimeSpan.FromHours(72)).GetSnapshot();

        Assert.AreEqual(RegistrySnapshotStatus.Unknown, snapshot.Status);
        Assert.IsFalse(snapshot.IsSafeForAutomaticApproval);
    }

    [TestMethod]
    public async Task ObservationForDifferentCommitIsUnknownAndUnsafe()
    {
        using var registry = new SnapshotRegistry();
        await RegistrySnapshotObservationStore.WriteAsync(
            registry.GitDirectory,
            new RegistrySnapshotObservation(
                "1111111111111111111111111111111111111111",
                Now));

        var snapshot = registry.Service(TimeSpan.FromHours(72)).GetSnapshot();

        Assert.AreEqual(RegistrySnapshotStatus.Unknown, snapshot.Status);
        Assert.IsFalse(snapshot.IsSafeForAutomaticApproval);
    }

    [TestMethod]
    public async Task ConfiguredThresholdIsHonored()
    {
        using var registry = new SnapshotRegistry();
        await registry.ObserveAsync(Now - TimeSpan.FromHours(10));

        var twelveHourSnapshot =
            registry.Service(TimeSpan.FromHours(12)).GetSnapshot();
        var eightHourSnapshot =
            registry.Service(TimeSpan.FromHours(8)).GetSnapshot();

        Assert.AreEqual(
            RegistrySnapshotStatus.Fresh,
            twelveHourSnapshot.Status);
        Assert.AreEqual(
            RegistrySnapshotStatus.Stale,
            eightHourSnapshot.Status);
    }

    private sealed class SnapshotRegistry : IDisposable
    {
        public const string CommitSha =
            "abcdef1234567890abcdef1234567890abcdef12";

        private readonly EmptyDirectory directory = new();
        private readonly IRegistryRepositoryInspector inspector;

        public SnapshotRegistry()
        {
            CreateRegistryLayout(directory.Path);
            GitDirectory = System.IO.Path.Combine(directory.Path, ".git");
            Directory.CreateDirectory(GitDirectory);
            inspector = new FixedRepositoryInspector(
                new RegistryRepositoryState(
                    CommitSha,
                    new DateTimeOffset(
                        2026,
                        10,
                        3,
                        17,
                        30,
                        0,
                        TimeSpan.Zero),
                    GitDirectory));
        }

        public string GitDirectory { get; }

        public RegistrySnapshotService Service(TimeSpan maximumAge) =>
            new(
                directory.Path,
                maximumAge,
                inspector,
                new FixedTimeProvider(Now));

        public Task ObserveAsync(DateTimeOffset observedAt) =>
            RegistrySnapshotObservationStore.WriteAsync(
                GitDirectory,
                new RegistrySnapshotObservation(
                    CommitSha,
                    observedAt));

        public void Dispose() => directory.Dispose();
    }

    private sealed class FixedRepositoryInspector(
        RegistryRepositoryState state) : IRegistryRepositoryInspector
    {
        public bool TryInspect(
            string registryRoot,
            out RegistryRepositoryState? inspectedState)
        {
            inspectedState = state;
            return true;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class EmptyDirectory : IDisposable
    {
        public EmptyDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"dn42atlas-snapshot-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() =>
            Directory.Delete(Path, recursive: true);
    }

    private static void CreateRegistryLayout(string root)
    {
        Directory.CreateDirectory(
            System.IO.Path.Combine(root, "data", "dns"));
        Directory.CreateDirectory(
            System.IO.Path.Combine(root, "data", "inetnum"));
        Directory.CreateDirectory(
            System.IO.Path.Combine(root, "data", "inet6num"));
    }
}

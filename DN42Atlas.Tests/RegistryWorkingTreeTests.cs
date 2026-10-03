using System.Diagnostics;
using DN42Atlas.Registry;

namespace DN42Atlas.Tests;

[TestClass]
public sealed class RegistryWorkingTreeTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 3, 20, 14, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task FreshCleanWorkingTreeIsSafe()
    {
        using var registry = new GitRegistry();
        await registry.ObserveAsync(Now - TimeSpan.FromHours(2));

        var snapshot = registry.GetSnapshot();

        Assert.AreEqual(RegistrySnapshotStatus.Fresh, snapshot.Status);
        Assert.IsTrue(snapshot.IsSafeForAutomaticApproval);
    }

    [TestMethod]
    public async Task ModifiedTrackedRegistryFileIsDirtyAndUnsafe()
    {
        using var registry = new GitRegistry();
        await registry.ObserveAsync(Now - TimeSpan.FromHours(2));
        File.AppendAllText(
            registry.TrackedDomainPath,
            "remarks: modified after observation\n");

        var snapshot = registry.GetSnapshot();

        Assert.AreEqual(RegistrySnapshotStatus.Dirty, snapshot.Status);
        Assert.IsFalse(snapshot.IsSafeForAutomaticApproval);
    }

    [TestMethod]
    public async Task UntrackedRegistryRootFileIsDirtyAndUnsafe()
    {
        using var registry = new GitRegistry();
        await registry.ObserveAsync(Now - TimeSpan.FromHours(2));
        File.WriteAllText(
            Path.Combine(registry.Root, "operator-notes.txt"),
            "untracked");

        var snapshot = registry.GetSnapshot();

        Assert.AreEqual(RegistrySnapshotStatus.Dirty, snapshot.Status);
        Assert.IsFalse(snapshot.IsSafeForAutomaticApproval);
    }

    private sealed class GitRegistry : IDisposable
    {
        public GitRegistry()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"dn42atlas-working-tree-tests-{Guid.NewGuid():N}");
            TrackedDomainPath = Path.Combine(
                Root,
                "data",
                "dns",
                "example.dn42");

            Directory.CreateDirectory(Path.GetDirectoryName(TrackedDomainPath)!);
            Directory.CreateDirectory(Path.Combine(Root, "data", "inetnum"));
            Directory.CreateDirectory(Path.Combine(Root, "data", "inet6num"));
            File.WriteAllText(
                TrackedDomainPath,
                "domain: example.dn42\nmnt-by: EXAMPLE-MNT\n");
            File.WriteAllText(
                Path.Combine(Root, "data", "inetnum", "placeholder"),
                "inetnum: 172.20.0.0 - 172.20.255.255\n");
            File.WriteAllText(
                Path.Combine(Root, "data", "inet6num", "placeholder"),
                "inet6num: fd00:: - fd00:ffff:ffff:ffff:ffff:ffff:ffff:ffff\n");

            RunGit("init");
            RunGit("config", "user.email", "test@example.invalid");
            RunGit("config", "user.name", "DN42Atlas Test");
            RunGit("add", ".");
            RunGit("commit", "-m", "Initial registry snapshot");
        }

        public string Root { get; }

        public string TrackedDomainPath { get; }

        public async Task ObserveAsync(DateTimeOffset observedAt)
        {
            var inspector = new GitRegistryRepositoryInspector();

            Assert.IsTrue(inspector.TryInspect(Root, out var repository));
            Assert.IsNotNull(repository);

            await RegistrySnapshotObservationStore.WriteAsync(
                repository.GitDirectory,
                new RegistrySnapshotObservation(
                    repository.CommitSha,
                    observedAt));
        }

        public RegistrySnapshot GetSnapshot() =>
            new RegistrySnapshotService(
                Root,
                TimeSpan.FromHours(72),
                timeProvider: new FixedTimeProvider(Now))
            .GetSnapshot();

        public void Dispose() =>
            Directory.Delete(Root, recursive: true);

        private void RunGit(params string[] arguments)
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = Root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo);
            Assert.IsNotNull(process);
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.AreEqual(
                0,
                process.ExitCode,
                $"git {string.Join(' ', arguments)} failed: {output} {error}");
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

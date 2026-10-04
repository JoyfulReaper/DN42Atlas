using System.Diagnostics;
using System.Text.Json.Nodes;
using DN42Atlas.Commands;
using DN42Atlas.OptOut.Exclusions;
using DN42Atlas.OptOut.Web;
using DN42Atlas.Policy;
using DN42Atlas.Publishing;
using Microsoft.AspNetCore.Http.HttpResults;

namespace DN42Atlas.Tests;

[TestClass]
public sealed class OperationLockTests
{
    [TestMethod]
    public async Task IndependentInstancesSerializeAndDisposeReleasesOwnership()
    {
        using var files = new TestFiles();
        var state = Path.Combine(files.DirectoryPath, "state.json");
        var published = Path.Combine(files.DirectoryPath, "published");
        using (var first = await AtlasOperationLock.AcquireAsync(state, published))
        {
            Assert.IsNull(await AtlasOperationLock.TryAcquireAsync(state, published, TimeSpan.Zero));
            using var cancellation = new CancellationTokenSource(100);
            await Assert.ThrowsAsync<OperationCanceledException>(() => AtlasOperationLock.AcquireAsync(state, published, cancellation.Token));
        }
        Assert.IsTrue(File.Exists(AtlasOperationLock.GetPath(state, published)));
        using var second = await AtlasOperationLock.TryAcquireAsync(state, published, TimeSpan.Zero);
        Assert.IsNotNull(second);
        if (OperatingSystem.IsLinux())
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(second.Path));
        Assert.ThrowsExactly<InvalidDataException>(() => AtlasOperationLock.GetPath(Path.Combine(published, "state.json"), published));
    }

    [TestMethod]
    public async Task LockOwnerChildOrNormalControl()
    {
        var root = Environment.GetEnvironmentVariable("DN42ATLAS_TEST_LOCK_ROOT");
        if (root == null)
        {
            using var files = new TestFiles();
            using var owner = await AtlasOperationLock.AcquireAsync(Path.Combine(files.DirectoryPath, "state.json"), Path.Combine(files.DirectoryPath, "published"));
            return;
        }
        using var operation = await AtlasOperationLock.AcquireAsync(Path.Combine(root, "state.json"), Path.Combine(root, "published"));
        File.WriteAllText(Path.Combine(root, "ready"), "owned");
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }

    [TestMethod]
    public async Task OwningProcessDeathReleasesPersistentLockFile()
    {
        using var files = new TestFiles();
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(OperationLockTests).Assembly.Location);
        start.ArgumentList.Add("/TestCaseFilter:FullyQualifiedName=DN42Atlas.Tests.OperationLockTests.LockOwnerChildOrNormalControl");
        start.Environment["DN42ATLAS_TEST_LOCK_ROOT"] = files.DirectoryPath;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var state = Path.Combine(files.DirectoryPath, "state.json");
        var published = Path.Combine(files.DirectoryPath, "published");
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!File.Exists(Path.Combine(files.DirectoryPath, "ready")) && !process.HasExited && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            Assert.IsTrue(File.Exists(Path.Combine(files.DirectoryPath, "ready")), process.HasExited ? await stdout + await stderr : "Child did not acquire lock.");
            Assert.IsNull(await AtlasOperationLock.TryAcquireAsync(state, published, TimeSpan.Zero));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await stdout;
            await stderr;
        }
        using var recovered = await AtlasOperationLock.TryAcquireAsync(state, published, TimeSpan.FromSeconds(2));
        Assert.IsNotNull(recovered);
    }

    [TestMethod]
    [DataRow("run")]
    [DataRow("republish")]
    [DataRow("publish-existing")]
    public async Task PublishingCliWaitsBeforeLoadingPolicy(string command)
    {
        using var files = new TestFiles();
        var published = Path.Combine(files.DirectoryPath, "published");
        var state = Path.Combine(files.DirectoryPath, "state.json");
        var raw = files.Write("raw.json", """{"Results":[{"Domain":"blocked.dn42","ProbeAddresses":["fd42::1"]}]}""");
        await ArtifactPublisher.PublishAsync(raw, published, files.LoadPolicy(), state);
        var registry = Path.Combine(files.DirectoryPath, "registry");
        Directory.CreateDirectory(Path.Combine(registry, "data", "dns"));
        File.WriteAllText(Path.Combine(registry, "data", "dns", "blocked"), "domain: blocked.dn42\n");
        using var owner = await AtlasOperationLock.AcquireAsync(state, published);
        using var process = StartCli(files.DirectoryPath, state, files.RuntimePath, registry,
            command == "publish-existing" ? [command, raw] : [command]);
        var stdout = process.StandardOutput.ReadToEndAsync();
        try
        {
            Assert.AreEqual("Waiting for Atlas operation lock.",
                await process.StandardError.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.IsFalse(process.HasExited);
            // Required files do not exist until ownership is obtained. A stale/pre-lock load fails.
            InstallConfig(files);
            await RuntimeExclusionBundle.WriteAtomicAsync(files.RuntimePath, ["blocked.dn42"], []);
            owner.Dispose();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.AreEqual(0, process.ExitCode, await stdout + await process.StandardError.ReadToEndAsync());
            Assert.IsEmpty(JsonNode.Parse(File.ReadAllText(Path.Combine(published, "latest.json")))!["Results"]!.AsArray());
            if (command == "run") Assert.IsNotEmpty(Directory.GetFiles(Path.Combine(files.DirectoryPath, "results"), "web-probe-*.json"));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }

    [TestMethod]
    [DataRow("run")]
    [DataRow("republish")]
    public async Task PublisherCannotOverlapAnOptOutMutation(string command)
    {
        using var f = await MutationFixture.CreateAsync();
        File.WriteAllText(f.Paths.Hosts, "other.dn42");
        InstallConfig(f.Files);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = f.CreateCoordinator(async _ =>
        {
            entered.SetResult();
            await release.Task;
            await RuntimeExclusionMaterializer.MaterializeAsync(f.Store, f.Paths.Runtime);
        });
        var mutation = coordinator.ExcludeAsync(f.Identity, "Domain", "owned.dn42");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var process = StartCli(f.Files.DirectoryPath, f.Paths.State, f.Paths.Runtime,
            Path.Combine(f.Files.DirectoryPath, "registry"), [command]);
        var stdout = process.StandardOutput.ReadToEndAsync();
        try
        {
            Assert.AreEqual("Waiting for Atlas operation lock.",
                await process.StandardError.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.IsFalse(process.HasExited);
            f.AssertListingWithdrawn();
            release.SetResult();
            Assert.AreEqual(MutationStatus.Success, await mutation.WaitAsync(TimeSpan.FromSeconds(5)));
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.AreEqual(0, process.ExitCode, await stdout + await process.StandardError.ReadToEndAsync());
            Assert.IsEmpty(JsonNode.Parse(File.ReadAllText(Path.Combine(f.Paths.Published, "latest.json")))!["Results"]!.AsArray());
        }
        finally
        {
            release.TrySetResult();
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await mutation;
        }
    }

    [TestMethod]
    [DataRow("exclude")]
    [DataRow("include")]
    public async Task WebContentionReturns503WithoutMutatingAnyState(string operation)
    {
        using var f = await MutationFixture.CreateAsync();
        if (operation == "include") Assert.AreEqual(MutationStatus.Success, await f.Coordinator.ExcludeAsync(f.Identity, "Domain", "owned.dn42"));
        var post = await f.ConfirmedPostAsync(operation, "Domain", "owned.dn42");
        var paths = new[] { f.DbPath, f.RawPath, f.Paths.State, f.Paths.Runtime,
            Path.Combine(f.Paths.Published, "index.html"), Path.Combine(f.Paths.Published, "latest.json") };
        var before = paths.Select(File.ReadAllBytes).ToArray();
        using var owner = await AtlasOperationLock.AcquireAsync(f.Paths.State, f.Paths.Published);
        var elapsed = Stopwatch.StartNew();
        var result = (ContentHttpResult)await OperatorEndpoints.PostAsync(post, f.Antiforgery, f.Coordinator);
        Assert.AreEqual(503, result.StatusCode);
        Assert.Contains("Please retry shortly", result.ResponseContent!);
        Assert.AreEqual("30", post.Response.Headers.RetryAfter.ToString());
        Assert.IsTrue(elapsed.Elapsed < TimeSpan.FromSeconds(5));
        Assert.IsFalse(RestrictiveReconciliationFence.IsPending(f.Paths));
        for (var i = 0; i < paths.Length; i++) CollectionAssert.AreEqual(before[i], File.ReadAllBytes(paths[i]));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task StartupAndMaintenanceRecoveryWaitForOwnershipWithoutNestedDeadlock(bool startup)
    {
        using var f = await MutationFixture.CreateAsync();
        RestrictiveReconciliationFence.Establish(f.Paths);
        var runtime = File.ReadAllBytes(f.Paths.Runtime);
        var html = File.ReadAllBytes(Path.Combine(f.Paths.Published, "index.html"));
        using var owner = await AtlasOperationLock.AcquireAsync(f.Paths.State, f.Paths.Published);
        var reconciler = new ExclusionReconciler(f.Store, f.Paths, Microsoft.Extensions.Logging.Abstractions.NullLogger<ExclusionReconciler>.Instance);
        Task recovery = startup ? reconciler.RecoverPendingAsync() : reconciler.ReconcileAsync();
        await Task.Delay(100);
        Assert.IsFalse(recovery.IsCompleted);
        CollectionAssert.AreEqual(runtime, File.ReadAllBytes(f.Paths.Runtime));
        CollectionAssert.AreEqual(html, File.ReadAllBytes(Path.Combine(f.Paths.Published, "index.html")));
        owner.Dispose();
        await recovery.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(RestrictiveReconciliationFence.IsPending(f.Paths));
    }

    [TestMethod]
    public void SystemdTemplatesKeepCrawlerEnvironmentAndPreviewSeparate()
    {
        var root = FindRepositoryRoot();
        var service = File.ReadAllText(Path.Combine(root, "deployment", "systemd", "dn42atlas-run.service"));
        var timer = File.ReadAllText(Path.Combine(root, "deployment", "systemd", "dn42atlas-run.timer"));
        foreach (var directive in new[] { "Type=oneshot", "User=joyfulreaper", "Group=joyfulreaper", "WorkingDirectory=/opt/DN42Atlas",
            "Environment=HOME=/home/joyfulreaper", "EnvironmentFile=/home/joyfulreaper/.config/dn42atlas/crawler.env",
            "TimeoutStartSec=45min", "NoNewPrivileges=true", "PrivateTmp=true", "--no-launch-profile -- run" })
            Assert.Contains(directive, service);
        Assert.DoesNotContain("oidc.env", service);
        Assert.DoesNotContain("nginx", service);
        Assert.Contains("OnCalendar=*-*-* 01:30:00", timer);
        Assert.Contains("Persistent=true", timer);
        Assert.Contains("RandomizedDelaySec=15m", timer);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "DN42Atlas.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static void InstallConfig(TestFiles files)
    {
        var config = Path.Combine(files.DirectoryPath, "config");
        Directory.CreateDirectory(config);
        File.Copy(files.HostsPath, Path.Combine(config, "excluded-hosts.txt"), true);
        File.Copy(files.PrefixesPath, Path.Combine(config, "excluded-prefixes.txt"), true);
    }

    private static Process StartCli(string cwd, string state, string runtime, string registry, string[] args)
    {
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = cwd,
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(typeof(RunCommand).Assembly.Location);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["DN42ATLAS_PUBLICATION_STATE_PATH"] = state;
        start.Environment["DN42ATLAS_RUNTIME_EXCLUSIONS_PATH"] = runtime;
        start.Environment["DN42ATLAS_REGISTRY_PATH"] = registry;
        return Process.Start(start)!;
    }
}

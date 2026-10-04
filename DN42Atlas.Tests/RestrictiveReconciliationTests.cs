using System.Diagnostics;
using System.Text.Json.Nodes;
using DN42Atlas.Commands;
using DN42Atlas.OptOut.Exclusions;
using DN42Atlas.OptOut.Registry;
using DN42Atlas.Policy;
using DN42Atlas.Publishing;
using Microsoft.Extensions.Logging.Abstractions;

namespace DN42Atlas.Tests;

[TestClass]
public sealed class RestrictiveReconciliationTests
{
    private const string ChildRoot = "DN42ATLAS_TEST_INTERRUPTION_ROOT";
    private const string ChildPhase = "DN42ATLAS_TEST_INTERRUPTION_PHASE";

    // Also runs as a normal successful-exclusion control. The parent test kills its
    // child testhost at a real coordinator boundary, without catch/finally recovery.
    [TestMethod]
    public async Task ExclusionWorkerWithdrawsBeforeCommitAndClearsFenceOnlyAfterPublication()
    {
        var root = Environment.GetEnvironmentVariable(ChildRoot);
        var phase = Environment.GetEnvironmentVariable(ChildPhase);
        using var f = await MutationFixture.CreateAsync(root == null ? null : new TestFiles(directoryPath: root));
        var raw = File.ReadAllBytes(f.RawPath);
        async Task StopAt(string point)
        {
            Assert.IsTrue(RestrictiveReconciliationFence.IsPending(f.Paths));
            Assert.HasCount(1, await f.Store.GetActiveAsync());
            Assert.IsFalse(File.Exists(Path.Combine(f.Paths.Published, "index.html")));
            if (phase != point) return;
            using (var checkpoint = DN42Atlas.IO.PrivateFile.CreateNew(Path.Combine(root!, "checkpoint")))
            {
                checkpoint.Write("ready"u8);
                checkpoint.Flush(flushToDisk: true);
            }
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
        var reconciler = new ExclusionReconciler(f.Store, f.Paths, NullLogger<ExclusionReconciler>.Instance,
            async _ =>
            {
                Assert.IsFalse(File.Exists(f.Paths.Runtime));
                f.AssertListingWithdrawn();
                await StopAt("db-committed");
                await RuntimeExclusionMaterializer.MaterializeAsync(f.Store, f.Paths.Runtime);
                await StopAt("runtime-installed");
            }, async (policy, _) =>
            {
                await StopAt("before-publication");
                if (phase == "during-publication")
                {
                    // Reproduce the publisher's first completed rename, before HTML/state.
                    var artifacts = PublicArtifactGenerator.Generate(File.ReadAllBytes(f.RawPath), policy);
                    var staged = Path.Combine(f.Paths.Published, ".partial-json.tmp");
                    await File.WriteAllTextAsync(staged, artifacts.Json);
                    File.Move(staged, Path.Combine(f.Paths.Published, "latest.json"));
                    await StopAt("during-publication");
                }
                await new RepublishCommand(policy, f.Paths.Published, f.Paths.State).ExecuteAsync();
            });
        using var coordinator = new ExclusionMutationCoordinator(f.Store,
            new RegistryResourceAuthorizer(f.Domains, f.Allocations, () => f.Snapshot), reconciler,
            f.Paths, NullLogger<ExclusionMutationCoordinator>.Instance);
        Assert.AreEqual(MutationStatus.Success, await coordinator.ExcludeAsync(f.Identity, "Domain", "owned.dn42"));
        Assert.IsFalse(RestrictiveReconciliationFence.IsPending(f.Paths));
        Assert.DoesNotContain("owned.dn42", File.ReadAllText(Path.Combine(f.Paths.Published, "index.html")));
        CollectionAssert.AreEqual(raw, File.ReadAllBytes(f.RawPath));
    }

    [TestMethod]
    [DataRow("db-committed")]
    [DataRow("runtime-installed")]
    [DataRow("before-publication")]
    [DataRow("during-publication")]
    public async Task KilledMutationRecoversFromDurableStateWithoutRestoringExcludedData(string phase)
    {
        using var files = new TestFiles();
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(RestrictiveReconciliationTests).Assembly.Location);
        start.ArgumentList.Add("/TestCaseFilter:FullyQualifiedName=DN42Atlas.Tests.RestrictiveReconciliationTests.ExclusionWorkerWithdrawsBeforeCommitAndClearsFenceOnlyAfterPublication");
        start.Environment[ChildRoot] = files.DirectoryPath;
        start.Environment[ChildPhase] = phase;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            var checkpoint = Path.Combine(files.DirectoryPath, "checkpoint");
            var deadline = DateTime.UtcNow.AddSeconds(40);
            while (!File.Exists(checkpoint) && !process.HasExited && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            Assert.IsTrue(File.Exists(checkpoint), process.HasExited ? await output + await error : "Child never reached interruption boundary.");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        var paths = new MutationPaths(files.HostsPath, files.PrefixesPath, files.RuntimePath,
            Path.Combine(files.DirectoryPath, "published"), Path.Combine(files.DirectoryPath, "state.json"));
        var rawPath = Path.Combine(files.DirectoryPath, "raw.json");
        var raw = File.ReadAllBytes(rawPath);
        var store = new ExclusionStore(Path.Combine(files.DirectoryPath, "audit.db"));
        Assert.IsTrue(RestrictiveReconciliationFence.IsPending(paths));
        var active = await store.GetActiveAsync();
        Assert.HasCount(1, active);
        Assert.AreEqual("owned.dn42", active[0].ResourceValue);
        Assert.IsFalse(File.Exists(Path.Combine(paths.Published, "index.html")));
        var jsonPath = Path.Combine(paths.Published, "latest.json");
        if (File.Exists(jsonPath)) Assert.DoesNotContain("owned.dn42", File.ReadAllText(jsonPath));
        Assert.AreEqual(phase != "db-committed", File.Exists(paths.Runtime));

        // A new store/reconciler uses only persisted data, exactly as application startup does.
        await new ExclusionReconciler(store, paths, NullLogger<ExclusionReconciler>.Instance).RecoverPendingAsync();
        Assert.IsFalse(RestrictiveReconciliationFence.IsPending(paths));
        Assert.AreEqual(active[0], (await store.GetActiveAsync()).Single());
        Assert.IsTrue(files.LoadPolicy(paths.Runtime).IsHostExcluded("owned.dn42"));
        var json = File.ReadAllText(jsonPath);
        var html = File.ReadAllText(Path.Combine(paths.Published, "index.html"));
        Assert.DoesNotContain("owned.dn42", json);
        Assert.DoesNotContain("owned.dn42", html);
        Assert.Contains("other.dn42", json);
        Assert.DoesNotContain("ProbeAddresses", json);
        var embeddedStart = html.IndexOf("const scan = ", StringComparison.Ordinal) + "const scan = ".Length;
        Assert.IsTrue(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(html[embeddedStart..html.IndexOf(';', embeddedStart)])));
        foreach (var name in new[] { "about.html", "opt-out.html", "robots.txt" })
            Assert.IsTrue(File.Exists(Path.Combine(paths.Published, name)));
        CollectionAssert.AreEqual(raw, File.ReadAllBytes(rawPath));
    }

    [TestMethod]
    public async Task FailedStartupRecoveryWithdrawsStaleFilesAndKeepsRecordAndFence()
    {
        using var f = await MutationFixture.CreateAsync();
        Assert.AreEqual(MutationStatus.Success, await f.Coordinator.ExcludeAsync(f.Identity, "Domain", "owned.dn42"));
        RestrictiveReconciliationFence.Establish(f.Paths);
        foreach (var name in new[] { "index.html", "latest.json" })
            File.WriteAllText(Path.Combine(f.Paths.Published, name), "stale owned.dn42");
        var raw = File.ReadAllBytes(f.RawPath);
        var reconciler = new ExclusionReconciler(f.Store, f.Paths, NullLogger<ExclusionReconciler>.Instance,
            republish: (_, _) => Task.FromException(new IOException("simulated replacement failure")));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reconciler.RecoverPendingAsync());
        f.AssertListingWithdrawn();
        Assert.IsTrue(RestrictiveReconciliationFence.IsPending(f.Paths));
        Assert.HasCount(1, await f.Store.GetActiveAsync());
        Assert.IsTrue(f.Files.LoadPolicy(f.Paths.Runtime).IsHostExcluded("owned.dn42"));
        CollectionAssert.AreEqual(raw, File.ReadAllBytes(f.RawPath));
        await new ExclusionReconciler(f.Store, f.Paths, NullLogger<ExclusionReconciler>.Instance).RecoverPendingAsync();
        Assert.IsFalse(RestrictiveReconciliationFence.IsPending(f.Paths));
    }

    [TestMethod]
    [DataRow("marker")]
    [DataRow("index.html")]
    [DataRow("latest.json")]
    public async Task FenceOrWithdrawalFailurePreventsDatabaseCommit(string obstruction)
    {
        using var f = await MutationFixture.CreateAsync();
        var path = obstruction == "marker" ? f.Paths.Pending : Path.Combine(f.Paths.Published, obstruction);
        File.Delete(path);
        Directory.CreateDirectory(path);
        Assert.AreEqual(MutationStatus.NotRecorded, await f.Coordinator.ExcludeAsync(f.Identity, "Domain", "owned.dn42"));
        Assert.IsEmpty(await f.Store.GetActiveAsync());
        Assert.IsTrue(File.Exists(f.Paths.Runtime));
    }

    [TestMethod]
    public async Task EmptyPendingMarkerAndMissingRuntimeAreRecoveredBeforePolicyValidation()
    {
        using var f = await MutationFixture.CreateAsync();
        File.WriteAllText(f.Paths.Pending, "");
        File.Delete(f.Paths.Runtime);
        await new ExclusionReconciler(f.Store, f.Paths, NullLogger<ExclusionReconciler>.Instance).RecoverPendingAsync();
        Assert.IsFalse(RestrictiveReconciliationFence.IsPending(f.Paths));
        _ = f.Files.LoadPolicy(f.Paths.Runtime);
        Assert.Contains("owned.dn42", File.ReadAllText(Path.Combine(f.Paths.Published, "latest.json")));
    }

    [TestMethod]
    public async Task ReservedFencePathNeverOverwritesOrDeletesRawScanEvidence()
    {
        using var f = await MutationFixture.CreateAsync();
        var raw = File.ReadAllBytes(f.RawPath);
        await File.WriteAllBytesAsync(f.Paths.Pending, raw);
        await ArtifactPublisher.PublishAsync(f.Paths.Pending, f.Paths.Published, f.Files.LoadPolicy(), f.Paths.State);
        Assert.AreEqual(MutationStatus.NotRecorded, await f.Coordinator.ExcludeAsync(f.Identity, "Domain", "owned.dn42"));
        Assert.IsEmpty(await f.Store.GetActiveAsync());
        f.AssertListingWithdrawn();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            new ExclusionReconciler(f.Store, f.Paths, NullLogger<ExclusionReconciler>.Instance).RecoverPendingAsync());
        CollectionAssert.AreEqual(raw, File.ReadAllBytes(f.Paths.Pending));
    }

    [TestMethod]
    public async Task InclusionRollbackFencesAndWithdrawsBeforeReactivatingAnExclusion()
    {
        using var f = await MutationFixture.CreateAsync();
        Assert.AreEqual(MutationStatus.Success, await f.Coordinator.ExcludeAsync(f.Identity, "Domain", "owned.dn42"));
        var active = (await f.Store.GetActiveAsync()).Single();
        using var coordinator = f.CreateCoordinator(inclusionOperations: new InclusionOperations
        {
            InstallRuntime = (_, _) => throw new IOException("simulated post-publication install failure"),
            Reactivate = async (id, revokedUtc) =>
            {
                Assert.IsTrue(RestrictiveReconciliationFence.IsPending(f.Paths));
                f.AssertListingWithdrawn();
                return await f.Store.ReactivateAsync(id, revokedUtc);
            }
        });
        Assert.AreEqual(MutationStatus.InclusionRestored,
            await coordinator.IncludeAsync(f.Identity, "Domain", "owned.dn42", active.Id));
        Assert.AreEqual(active, (await f.Store.GetActiveAsync()).Single());
        Assert.IsFalse(RestrictiveReconciliationFence.IsPending(f.Paths));
        Assert.DoesNotContain("owned.dn42", File.ReadAllText(Path.Combine(f.Paths.Published, "index.html")));
    }

    [TestMethod]
    public async Task ProductionStartupRecoversPendingStateBeforeValidatingMissingRuntime()
    {
        using var f = await MutationFixture.CreateAsync();
        Assert.AreEqual(MutationStatus.Success, await f.Coordinator.ExcludeAsync(f.Identity, "Domain", "owned.dn42"));
        RestrictiveReconciliationFence.Establish(f.Paths);
        File.Delete(f.Paths.Runtime);
        File.WriteAllText(Path.Combine(f.Paths.Published, "index.html"), "stale owned.dn42");
        var requests = Path.Combine(f.Files.DirectoryPath, "requests.db");
        await DN42Atlas.OptOut.ManualRequests.ManualRequestStore.InitializeAsync(requests);
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(typeof(ExclusionStore).Assembly.Location)!
        };
        start.ArgumentList.Add(typeof(ExclusionStore).Assembly.Location);
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add("http://127.0.0.1:0");
        foreach (var setting in new Dictionary<string, string>
        {
            ["DN42ATLAS_OIDC_CLIENT_ID"] = "test-client", ["DN42ATLAS_OIDC_CLIENT_SECRET"] = "test-secret",
            ["DN42ATLAS_OIDC_AUTHORITY"] = "https://auth42.invalid", ["DN42ATLAS_REGISTRY_PATH"] = Path.Combine(f.Files.DirectoryPath, "registry"),
            ["DN42ATLAS_EXCLUSION_DB_PATH"] = f.DbPath, ["DN42ATLAS_RUNTIME_EXCLUSIONS_PATH"] = f.Paths.Runtime,
            ["DN42ATLAS_EXCLUDED_HOSTS_PATH"] = f.Paths.Hosts, ["DN42ATLAS_EXCLUDED_PREFIXES_PATH"] = f.Paths.Prefixes,
            ["DN42ATLAS_PUBLISHED_PATH"] = f.Paths.Published, ["DN42ATLAS_PUBLICATION_STATE_PATH"] = f.Paths.State,
            ["DN42ATLAS_MANUAL_REQUEST_DB_PATH"] = requests, ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["Ntfy__ServerUrl"] = "http://127.0.0.1:1", ["Ntfy__Topic"] = "startup-test",
            ["Ntfy__AccessToken"] = "",
            ["Logging__LogLevel__Default"] = "Information"
        }) start.Environment[setting.Key] = setting.Value;
        using var process = Process.Start(start)!;
        var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = Task.Run(async () =>
        {
            var lines = new List<string>();
            while (await process.StandardOutput.ReadLineAsync() is string line)
            {
                lines.Add(line);
                if (line.Contains("Now listening on:", StringComparison.Ordinal)) listening.TrySetResult();
            }
            return string.Join('\n', lines);
        });
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            var exit = process.WaitForExitAsync();
            await Task.WhenAny(listening.Task, exit).WaitAsync(TimeSpan.FromSeconds(25));
            Assert.IsTrue(listening.Task.IsCompletedSuccessfully,
                listening.Task.IsCompletedSuccessfully ? "Startup completed." : await output + await error);
            Assert.IsFalse(RestrictiveReconciliationFence.IsPending(f.Paths));
            Assert.IsTrue(f.Files.LoadPolicy(f.Paths.Runtime).IsHostExcluded("owned.dn42"));
            Assert.HasCount(1, await f.Store.GetActiveAsync());
            Assert.DoesNotContain("owned.dn42", File.ReadAllText(Path.Combine(f.Paths.Published, "index.html")));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await output;
            await error;
        }
    }
}

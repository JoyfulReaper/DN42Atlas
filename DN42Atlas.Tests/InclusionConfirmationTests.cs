using System.Security.Claims;
using System.Text.Json.Nodes;
using DN42Atlas.OptOut.Auth;
using DN42Atlas.OptOut.Exclusions;
using DN42Atlas.OptOut.Registry;
using DN42Atlas.OptOut.Web;
using DN42Atlas.Policy;
using DN42Atlas.Registry;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Microsoft.Data.Sqlite;

namespace DN42Atlas.Tests;

[TestClass]
public sealed class InclusionConfirmationTests
{
    [TestMethod]
    [DataRow("exclude")]
    [DataRow("include")]
    public async Task FirstPostConfirmsWithoutChangingAnyArtifacts(string operation)
    {
        using var f = await MutationFixture.CreateAsync();
        if (operation == "include") await Exclude(f);
        var active = (await f.Store.GetActiveAsync()).ToArray();
        var paths = new[] { f.DbPath, f.Paths.Runtime, f.Paths.State, f.RawPath,
            Path.Combine(f.Paths.Published, "latest.json"), Path.Combine(f.Paths.Published, "index.html") };
        var bytes = paths.Select(File.ReadAllBytes).ToArray();
        var context = f.PostContext("Domain", "OWNED.DN42.", "confirm-" + operation);
        var result = (ContentHttpResult)await OperatorEndpoints.PostAsync(context, f.Antiforgery, f.Coordinator);
        Assert.AreEqual(200, result.StatusCode ?? 200);
        Assert.Contains(operation == "include" ? "Confirm inclusion" : "Confirm exclusion", result.ResponseContent!);
        Assert.Contains("name=\"confirmationToken\"", result.ResponseContent!);
        Assert.Contains("href=\"/operator\">Cancel", result.ResponseContent!);
        Assert.Contains("no-store", context.Response.Headers.CacheControl.ToString());
        CollectionAssert.AreEqual(active, (await f.Store.GetActiveAsync()).ToArray());
        for (var i = 0; i < paths.Length; i++) CollectionAssert.AreEqual(bytes[i], File.ReadAllBytes(paths[i]));
    }

    [TestMethod]
    public void ConfirmationHtmlEncodesAllValues()
    {
        var html = OptOutPage.RenderConfirmation("exclude", new(ExclusionResourceType.Domain, "\"><script>x</script>.dn42"),
            "\"><img src=x>", "\"><svg>");
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("<svg", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("tampered")]
    [DataRow("resource")]
    [DataRow("type")]
    [DataRow("operation")]
    [DataRow("subject")]
    [DataRow("maintainer")]
    [DataRow("extra")]
    [DataRow("duplicate")]
    public async Task FinalPostRejectsInvalidConfirmationWithoutMutation(string failure)
    {
        using var f = await MutationFixture.CreateAsync();
        var post = await f.ConfirmedPostAsync("exclude", "Domain", "owned.dn42");
        var form = post.Request.Form.ToDictionary(p => p.Key, p => p.Value);
        switch (failure)
        {
            case "missing": form.Remove("confirmationToken"); break;
            case "tampered": form["confirmationToken"] = "tampered"; break;
            case "resource": form["resourceValue"] = "other.dn42"; break;
            case "type": form["resourceType"] = "IPv4Prefix"; form["resourceValue"] = "172.20.16.0/24"; break;
            case "operation": form["intent"] = "include"; break;
            case "extra": form["id"] = "1"; break;
            case "duplicate": form["confirmationToken"] = new StringValues([form["confirmationToken"].ToString(), "another"]); break;
            case "subject":
            case "maintainer":
                var other = failure == "subject" ? f.Identity with { Subject = "other-subject" }
                    : f.Identity with { ActiveMaintainer = "OTHER-MNT" };
                post = f.PostContext("Domain", "owned.dn42", "exclude", form["confirmationToken"].ToString(), other);
                form = post.Request.Form.ToDictionary(p => p.Key, p => p.Value);
                break;
        }
        post.Request.Form = new FormCollection(form);
        var runtime = File.ReadAllBytes(f.Paths.Runtime);
        Assert.AreEqual(400, ((IStatusCodeHttpResult)await OperatorEndpoints.PostAsync(post, f.Antiforgery, f.Coordinator)).StatusCode);
        Assert.IsEmpty(await f.Store.GetActiveAsync());
        CollectionAssert.AreEqual(runtime, File.ReadAllBytes(f.Paths.Runtime));
    }

    [TestMethod]
    public async Task ExpiredAndFutureConfirmationTokensAreRejected()
    {
        using var f = await MutationFixture.CreateAsync();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var tokens = new ConfirmationTokens(f.Services.GetRequiredService<IDataProtectionProvider>(), clock);
        var resource = new ExactResource(ExclusionResourceType.Domain, "owned.dn42");
        var token = tokens.Create("exclude", resource, f.Identity, null);
        Assert.IsTrue(tokens.TryValidate(token, "exclude", resource, f.Identity, out _));
        clock.Now += TimeSpan.FromMinutes(5) + TimeSpan.FromTicks(1);
        Assert.IsFalse(tokens.TryValidate(token, "exclude", resource, f.Identity, out _));
        var context = f.PostContext("Domain", "owned.dn42", "exclude",
            new ConfirmationTokens(f.Services.GetRequiredService<IDataProtectionProvider>(),
                new TestClock(DateTimeOffset.UtcNow.AddMinutes(-6))).Create("exclude", resource, f.Identity, null));
        Assert.AreEqual(400, ((IStatusCodeHttpResult)await OperatorEndpoints.PostAsync(context, f.Antiforgery, f.Coordinator)).StatusCode);
        clock.Now -= TimeSpan.FromMinutes(6);
        Assert.IsFalse(tokens.TryValidate(token, "exclude", resource, f.Identity, out _));
        Assert.IsEmpty(await f.Store.GetActiveAsync());
    }

    [TestMethod]
    [DataRow("exclude", "ownership")]
    [DataRow("exclude", "stale")]
    [DataRow("exclude", "dirty")]
    [DataRow("exclude", "unknown")]
    [DataRow("include", "ownership")]
    [DataRow("include", "stale")]
    [DataRow("include", "dirty")]
    [DataRow("include", "unknown")]
    public async Task FinalPostRechecksCurrentOwnershipAndFreshness(string operation, string change)
    {
        using var f = await MutationFixture.CreateAsync();
        if (operation == "include") await Exclude(f);
        var post = await f.ConfirmedPostAsync(operation, "Domain", "owned.dn42");
        if (change == "ownership") File.WriteAllText(f.DomainFile, "domain: owned.dn42\nmnt-by: OTHER-MNT\n");
        else f.Snapshot = f.Snapshot with { Status = change switch {
            "stale" => RegistrySnapshotStatus.Stale, "dirty" => RegistrySnapshotStatus.Dirty, _ => RegistrySnapshotStatus.Unknown } };
        var before = (await f.Store.GetActiveAsync()).ToArray();
        Assert.AreEqual(403, ((IStatusCodeHttpResult)await OperatorEndpoints.PostAsync(post, f.Antiforgery, f.Coordinator)).StatusCode);
        CollectionAssert.AreEqual(before, (await f.Store.GetActiveAsync()).ToArray());
    }

    [TestMethod]
    [DataRow("Domain", "owned.dn42", "owned.dn42")]
    [DataRow("IPv4Prefix", "172.20.16.0/24", "172.20.16.7")]
    [DataRow("IPv6Prefix", "fd42:1234::/48", "fd42:1234::1")]
    public async Task ExactOwnerIncludePreservesAuditAndRawAndRepublishes(string type, string value, string restoredReference)
    {
        using var f = await MutationFixture.CreateAsync();
        await Exclude(f, type, value);
        var original = (await f.Store.GetActiveAsync()).Single();
        var raw = File.ReadAllBytes(f.RawPath);
        var post = await f.ConfirmedPostAsync("include", type, value);
        var result = await OperatorEndpoints.PostAsync(post, f.Antiforgery, f.Coordinator);
        Assert.AreEqual("/operator?result=included", ((RedirectHttpResult)result).Url);
        var row = (await f.Store.GetByIdAsync(original.Id))!;
        Assert.IsNotNull(row.RevokedUtc);
        Assert.AreEqual(original, row with { RevokedUtc = null });
        Assert.IsEmpty(await f.Store.GetActiveAsync());
        var policy = ExclusionPolicy.Load(f.Paths.Hosts, f.Paths.Prefixes, f.Paths.Runtime);
        Assert.IsFalse(type == "Domain" ? policy.IsHostExcluded(value) : policy.IsAddressExcluded(restoredReference));
        var bundle = RuntimeExclusionBundle.Load(f.Paths.Runtime);
        Assert.IsEmpty(bundle.HostRules);
        Assert.IsEmpty(bundle.PrefixRules);
        var json = File.ReadAllText(Path.Combine(f.Paths.Published, "latest.json"));
        var html = File.ReadAllText(Path.Combine(f.Paths.Published, "index.html"));
        Assert.Contains(restoredReference, json);
        Assert.Contains(restoredReference, html);
        Assert.AreEqual("2026-10-03T12:50:26Z", JsonNode.Parse(json)!["GeneratedAt"]!.GetValue<string>());
        var start = html.IndexOf("const scan = ", StringComparison.Ordinal) + "const scan = ".Length;
        Assert.IsTrue(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(html[start..html.IndexOf(';', start)])));
        CollectionAssert.AreEqual(raw, File.ReadAllBytes(f.RawPath));
        Assert.AreEqual(409, ((IStatusCodeHttpResult)await OperatorEndpoints.PostAsync(post, f.Antiforgery, f.Coordinator)).StatusCode);
        await Exclude(f, type, value);
        var newRow = (await f.Store.GetActiveAsync()).Single();
        Assert.AreNotEqual(original.Id, newRow.Id);
        Assert.AreEqual(409, ((IStatusCodeHttpResult)await OperatorEndpoints.PostAsync(post, f.Antiforgery, f.Coordinator)).StatusCode);
        Assert.AreEqual(newRow, (await f.Store.GetActiveAsync()).Single());
    }

    [TestMethod]
    public async Task CurrentOwnerCanIncludePreviousOwnersExclusion()
    {
        using var f = await MutationFixture.CreateAsync();
        await Exclude(f);
        var original = (await f.Store.GetActiveAsync()).Single();
        File.WriteAllText(f.DomainFile, "domain: owned.dn42\nmnt-by: NEW-MNT\n");
        var newOwner = new Auth42Identity("new-subject", "NEW-MNT", 4242421000);
        var post = await f.ConfirmedPostAsync("include", "Domain", "owned.dn42", newOwner);
        Assert.IsInstanceOfType<RedirectHttpResult>(await OperatorEndpoints.PostAsync(post, f.Antiforgery, f.Coordinator));
        Assert.AreEqual(original, (await f.Store.GetByIdAsync(original.Id))! with { RevokedUtc = null });
    }

    [TestMethod]
    public async Task ExclusionConfirmationReplayRemainsIdempotent()
    {
        using var f = await MutationFixture.CreateAsync();
        var post = await f.ConfirmedPostAsync("exclude", "Domain", "owned.dn42");
        Assert.IsInstanceOfType<RedirectHttpResult>(await OperatorEndpoints.PostAsync(post, f.Antiforgery, f.Coordinator));
        var row = (await f.Store.GetActiveAsync()).Single();
        Assert.IsInstanceOfType<RedirectHttpResult>(await OperatorEndpoints.PostAsync(post, f.Antiforgery, f.Coordinator));
        Assert.AreEqual(row, (await f.Store.GetActiveAsync()).Single());
    }

    [TestMethod]
    [DataRow("Domain", "other.dn42")]
    [DataRow("Domain", "*.owned.dn42")]
    [DataRow("IPv4Prefix", "172.20.16.0/23")]
    [DataRow("IPv4Prefix", "172.20.16.0/25")]
    [DataRow("IPv6Prefix", "fd42:1234::/47")]
    [DataRow("IPv6Prefix", "fd42:1234::/49")]
    public async Task IncludeRequiresExactLocalOwnershipRegardlessOfAsnOrRouteClaims(string type, string value)
    {
        using var f = await MutationFixture.CreateAsync();
        await Exclude(f);
        var context = f.PostContext(type, value, "confirm-include");
        ((ClaimsIdentity)context.User.Identity!).AddClaim(new Claim("route", value));
        ((ClaimsIdentity)context.User.Identity!).AddClaim(new Claim("route6", value));
        var result = await OperatorEndpoints.PostAsync(context, f.Antiforgery, f.Coordinator);
        Assert.AreEqual(value.StartsWith('*') ? 400 : 403, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.HasCount(1, await f.Store.GetActiveAsync());
    }

    [TestMethod]
    public async Task NoActiveExclusionCannotBeConfirmedOrIncluded()
    {
        using var f = await MutationFixture.CreateAsync();
        Assert.AreEqual(MutationStatus.Conflict, (await f.Coordinator.PrepareAsync(f.Identity, "include", "Domain", "owned.dn42")).Status);
        Assert.AreEqual(MutationStatus.Conflict, await f.Coordinator.IncludeAsync(f.Identity, "Domain", "owned.dn42", 1));
    }

    [TestMethod]
    [DataRow("Domain", "owned.dn42", "owned.dn42")]
    [DataRow("Domain", "owned.dn42", "*.dn42")]
    [DataRow("IPv4Prefix", "172.20.16.0/24", "172.20.0.0/16")]
    [DataRow("IPv6Prefix", "fd42:1234::/48", "fd42::/16")]
    public async Task ManualPolicyWinsInUiConfirmationAndFinalPost(string type, string value, string manual)
    {
        using var f = await MutationFixture.CreateAsync();
        await Exclude(f, type, value);
        var post = await f.ConfirmedPostAsync("include", type, value);
        var path = type == "Domain" ? f.Paths.Hosts : f.Paths.Prefixes;
        File.WriteAllText(path, manual + "\n");
        var originalManual = File.ReadAllBytes(path);
        var active = (await f.Store.GetActiveAsync()).Single();
        Assert.AreEqual(409, ((IStatusCodeHttpResult)await OperatorEndpoints.PostAsync(post, f.Antiforgery, f.Coordinator)).StatusCode);
        Assert.AreEqual(MutationStatus.Conflict, (await f.Coordinator.PrepareAsync(f.Identity, "include", type, value)).Status);
        Assert.AreEqual(active, (await f.Store.GetActiveAsync()).Single());
        var page = (ContentHttpResult)await OperatorEndpoints.GetAsync(f.Context(), f.Domains, f.Allocations,
            f.SnapshotService, f.Store, f.Paths, f.Antiforgery, f.Logs);
        var item = Item(page.ResponseContent!, value);
        Assert.Contains("Excluded by operator policy", item);
        Assert.DoesNotContain("Include again", item);
        CollectionAssert.AreEqual(originalManual, File.ReadAllBytes(path));
    }

    [TestMethod]
    public async Task DashboardClassifiesIncludedManualAndSelfServiceSeparately()
    {
        using var f = await MutationFixture.CreateAsync();
        File.WriteAllText(f.Paths.Hosts, "owned.dn42\n");
        await Exclude(f, "IPv4Prefix", "172.20.16.0/24");
        var page = (ContentHttpResult)await OperatorEndpoints.GetAsync(f.Context(), f.Domains, f.Allocations,
            f.SnapshotService, f.Store, f.Paths, f.Antiforgery, f.Logs);
        var html = page.ResponseContent!;
        Assert.Contains("Excluded by operator policy", Item(html, "owned.dn42"));
        Assert.DoesNotContain("<form", Item(html, "owned.dn42"));
        Assert.Contains("Excluded by self-service", Item(html, "172.20.16.0/24"));
        Assert.Contains("Include again", Item(html, "172.20.16.0/24"));
        Assert.Contains("Included", Item(html, "fd42:1234::/48"));
        Assert.Contains("Exclude from DN42Atlas", Item(html, "fd42:1234::/48"));
    }

    [TestMethod]
    [DataRow("candidate")]
    [DataRow("validation")]
    [DataRow("policy")]
    [DataRow("republish")]
    [DataRow("install")]
    public async Task InclusionFailuresRestoreSameExclusionAndFilteredPublication(string stage)
    {
        using var f = await MutationFixture.CreateAsync();
        await Exclude(f);
        var original = (await f.Store.GetActiveAsync()).Single();
        var manual = File.ReadAllBytes(f.Paths.Hosts);
        var raw = File.ReadAllBytes(f.RawPath);
        var ops = new InclusionOperations
        {
            MaterializeCandidate = stage switch
            {
                "candidate" => _ => throw new IOException("candidate failure"),
                "validation" => path => File.WriteAllTextAsync(path, "invalid"),
                "policy" => async path =>
                {
                    await RuntimeExclusionMaterializer.MaterializeAsync(f.Store, path);
                    File.WriteAllText(f.Paths.Prefixes, "invalid/999\n");
                },
                _ => null
            },
            Republish = stage == "republish" ? _ => throw new IOException("publication failure") : null,
            InstallRuntime = stage == "install" ? (_, _) => throw new IOException("install failure") : null,
            Reactivate = stage == "policy" ? async (id, timestamp) =>
            {
                File.WriteAllText(f.Paths.Prefixes, "");
                return await f.Store.ReactivateAsync(id, timestamp);
            } : null
        };
        using var coordinator = f.CreateCoordinator(inclusionOperations: ops);
        Assert.AreEqual(MutationStatus.InclusionRestored, await coordinator.IncludeAsync(f.Identity, "Domain", "owned.dn42", original.Id));
        Assert.AreEqual(original, (await f.Store.GetActiveAsync()).Single());
        Assert.IsTrue(ExclusionPolicy.Load(f.Paths.Hosts, f.Paths.Prefixes, f.Paths.Runtime).IsHostExcluded("owned.dn42"));
        Assert.DoesNotContain("owned.dn42", File.ReadAllText(Path.Combine(f.Paths.Published, "latest.json")));
        Assert.DoesNotContain("owned.dn42", File.ReadAllText(Path.Combine(f.Paths.Published, "index.html")));
        CollectionAssert.AreEqual(raw, File.ReadAllBytes(f.RawPath));
        CollectionAssert.AreEqual(manual, File.ReadAllBytes(f.Paths.Hosts));
        Assert.IsEmpty(Directory.GetFiles(f.Files.DirectoryPath, "*.candidate"));
    }

    [TestMethod]
    public async Task UnprovenRollbackDisablesRuntimeAndWithdrawsOnlyListingThenReconcileRecovers()
    {
        using var f = await MutationFixture.CreateAsync();
        await Exclude(f);
        var original = (await f.Store.GetActiveAsync()).Single();
        using var coordinator = f.CreateCoordinator(inclusionOperations: new InclusionOperations
        {
            InstallRuntime = (_, _) => throw new IOException("install failure"),
            Reactivate = (_, _) => Task.FromResult(false)
        });
        Assert.AreEqual(MutationStatus.InclusionUnavailable, await coordinator.IncludeAsync(f.Identity, "Domain", "owned.dn42", original.Id));
        Assert.IsFalse(File.Exists(f.Paths.Runtime));
        f.AssertListingWithdrawn();
        var row = (await f.Store.GetByIdAsync(original.Id))!;
        Assert.IsNotNull(row.RevokedUtc);
        // Reconciliation recovers recorded DB truth; it does not invent a revocation or reactivation.
        Assert.AreEqual(ReconciliationStatus.Success, await f.Coordinator.ReconcileAsync());
        Assert.IsTrue(File.Exists(f.Paths.Runtime));
        Assert.IsTrue(File.Exists(Path.Combine(f.Paths.Published, "index.html")));
    }

    [TestMethod]
    public async Task ExpectedTimestampReactivationCannotUndoOtherRevocations()
    {
        using var f = await MutationFixture.CreateAsync();
        await Exclude(f);
        var original = (await f.Store.GetActiveAsync()).Single();
        var time = DateTimeOffset.UtcNow;
        Assert.IsTrue(await f.Store.RevokeAsync(original.Id, time));
        Assert.IsFalse(await f.Store.ReactivateAsync(original.Id, time.AddTicks(1)));
        Assert.IsTrue(await f.Store.ReactivateAsync(original.Id, time));
        Assert.IsFalse(await f.Store.ReactivateAsync(original.Id, time));
        Assert.AreEqual(original, (await f.Store.GetByIdAsync(original.Id))!);
    }

    [TestMethod]
    [DataRow("include")]
    [DataRow("exclude")]
    public async Task IncludeSharesMutationGateWithOtherMutations(string secondOperation)
    {
        using var f = await MutationFixture.CreateAsync();
        await Exclude(f);
        var original = (await f.Store.GetActiveAsync()).Single();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var criticalSections = 0;
        using var coordinator = f.CreateCoordinator(inclusionOperations: new InclusionOperations
        {
            MaterializeCandidate = async path =>
            {
                Assert.AreEqual(1, Interlocked.Increment(ref criticalSections));
                entered.SetResult();
                await release.Task;
                await RuntimeExclusionMaterializer.MaterializeAsync(f.Store, path);
                Interlocked.Decrement(ref criticalSections);
            }
        });
        var first = coordinator.IncludeAsync(f.Identity, "Domain", "owned.dn42", original.Id);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsFalse(File.Exists(f.Paths.Runtime));
        Assert.IsNotNull((await f.Store.GetByIdAsync(original.Id))!.RevokedUtc);
        var second = secondOperation == "include"
            ? coordinator.IncludeAsync(f.Identity, "Domain", "owned.dn42", original.Id)
            : coordinator.ExcludeAsync(f.Identity, "Domain", "owned.dn42", expectedLatestRecordId: original.Id);
        try { Assert.IsFalse(second.IsCompleted); }
        finally { release.SetResult(); }
        Assert.AreEqual(MutationStatus.Success, await first);
        Assert.AreEqual(secondOperation == "include" ? MutationStatus.Conflict : MutationStatus.Success, await second);
        Assert.AreEqual(0, criticalSections);
    }

    [TestMethod]
    [DataRow("exclude", RegistrySnapshotStatus.Stale)]
    [DataRow("exclude", RegistrySnapshotStatus.Dirty)]
    [DataRow("exclude", RegistrySnapshotStatus.Unknown)]
    [DataRow("include", RegistrySnapshotStatus.Stale)]
    [DataRow("include", RegistrySnapshotStatus.Dirty)]
    [DataRow("include", RegistrySnapshotStatus.Unknown)]
    public async Task UnsafeRegistryCannotEvenPrepareConfirmation(string operation, RegistrySnapshotStatus status)
    {
        using var f = await MutationFixture.CreateAsync();
        if (operation == "include") await Exclude(f);
        var before = (await f.Store.GetActiveAsync()).ToArray();
        f.Snapshot = f.Snapshot with { Status = status };
        var context = f.PostContext("Domain", "owned.dn42", "confirm-" + operation);
        Assert.AreEqual(403, ((IStatusCodeHttpResult)await OperatorEndpoints.PostAsync(context, f.Antiforgery, f.Coordinator)).StatusCode);
        CollectionAssert.AreEqual(before, (await f.Store.GetActiveAsync()).ToArray());
        var html = OptOutPage.RenderSignedIn(f.Identity, ["owned.dn42"], [], [], f.Snapshot,
            selfService: new HashSet<ExactResource> { new(ExclusionResourceType.Domain, "owned.dn42") }, requestToken: "token");
        Assert.Contains("Excluded by self-service", html);
        Assert.DoesNotContain("<form", html);
    }

    [TestMethod]
    public async Task DatabaseRevocationFailureRecoversOriginalActiveRecord()
    {
        using var f = await MutationFixture.CreateAsync();
        await Exclude(f);
        var row = (await f.Store.GetActiveAsync()).Single();
        await using (var connection = new SqliteConnection($"Data Source={f.DbPath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER FailRevocation BEFORE UPDATE ON Exclusions WHEN NEW.RevokedUtc IS NOT NULL BEGIN SELECT RAISE(ABORT, 'test failure'); END;";
            await command.ExecuteNonQueryAsync();
        }
        Assert.AreEqual(MutationStatus.InclusionRestored, await f.Coordinator.IncludeAsync(f.Identity, "Domain", "owned.dn42", row.Id));
        Assert.AreEqual(row, (await f.Store.GetActiveAsync()).Single());
        Assert.IsTrue(f.Files.LoadPolicy(f.Paths.Runtime).IsHostExcluded("owned.dn42"));
    }

    [TestMethod]
    public async Task InstallFailureAfterRenameStillRestoresExclusion()
    {
        using var f = await MutationFixture.CreateAsync();
        await Exclude(f);
        var row = (await f.Store.GetActiveAsync()).Single();
        using var coordinator = f.CreateCoordinator(inclusionOperations: new InclusionOperations
        {
            InstallRuntime = (candidate, runtime) =>
            {
                File.Move(candidate, runtime);
                throw new IOException("detected failure after installation");
            }
        });
        Assert.AreEqual(MutationStatus.InclusionRestored, await coordinator.IncludeAsync(f.Identity, "Domain", "owned.dn42", row.Id));
        Assert.AreEqual(row, (await f.Store.GetActiveAsync()).Single());
        Assert.IsTrue(f.Files.LoadPolicy(f.Paths.Runtime).IsHostExcluded("owned.dn42"));
        Assert.DoesNotContain("owned.dn42", File.ReadAllText(Path.Combine(f.Paths.Published, "latest.json")));
    }

    [TestMethod]
    public async Task RecoveryPublicationFailureLeavesRestoredDbButRuntimeAndListingUnavailable()
    {
        using var f = await MutationFixture.CreateAsync();
        await Exclude(f);
        var row = (await f.Store.GetActiveAsync()).Single();
        var originalRaw = File.ReadAllBytes(f.RawPath);
        File.WriteAllText(f.RawPath, "changed raw scan");
        Assert.AreEqual(MutationStatus.InclusionUnavailable, await f.Coordinator.IncludeAsync(f.Identity, "Domain", "owned.dn42", row.Id));
        Assert.AreEqual(row, (await f.Store.GetActiveAsync()).Single());
        Assert.IsFalse(File.Exists(f.Paths.Runtime));
        f.AssertListingWithdrawn();
        File.WriteAllBytes(f.RawPath, originalRaw);
        Assert.AreEqual(ReconciliationStatus.Success, await f.Coordinator.ReconcileAsync());
        Assert.IsTrue(f.Files.LoadPolicy(f.Paths.Runtime).IsHostExcluded("owned.dn42"));
        Assert.DoesNotContain("owned.dn42", File.ReadAllText(Path.Combine(f.Paths.Published, "latest.json")));
    }

    [TestMethod]
    public async Task InclusionCompletesDespiteDisconnectDuringCriticalSequence()
    {
        using var f = await MutationFixture.CreateAsync();
        await Exclude(f);
        var row = (await f.Store.GetActiveAsync()).Single();
        using var disconnected = new CancellationTokenSource();
        using var coordinator = f.CreateCoordinator(inclusionOperations: new InclusionOperations
        {
            MaterializeCandidate = async candidate =>
            {
                disconnected.Cancel();
                Assert.IsFalse(File.Exists(f.Paths.Runtime));
                await RuntimeExclusionMaterializer.MaterializeAsync(f.Store, candidate);
            }
        });
        Assert.AreEqual(MutationStatus.Success, await coordinator.IncludeAsync(f.Identity, "Domain", "owned.dn42", row.Id, disconnected.Token));
        Assert.IsEmpty(await f.Store.GetActiveAsync());
        Assert.IsTrue(File.Exists(f.Paths.Runtime));
    }

    [TestMethod]
    [DataRow("172.20.16.0/24", "172.20.0.0/16", true)]
    [DataRow("172.20.16.0/24", "172.20.16.0/25", false)]
    [DataRow("172.20.16.0/24", "172.20.17.0/24", false)]
    [DataRow("fd42:1234::/48", "fd42::/16", true)]
    [DataRow("fd42:1234::/48", "fd42:1234::/49", false)]
    [DataRow("fd42:1234::/48", "fd42:5678::/48", false)]
    [DataRow("fd42:1234::/48", "172.20.0.0/16", false)]
    public void ManualCidrMustContainEntireExactAllocation(string allocation, string manual, bool expected)
    {
        using var files = new TestFiles();
        File.WriteAllText(files.PrefixesPath, manual);
        Assert.AreEqual(expected, files.LoadPolicy().IsPrefixExcluded(allocation));
    }

    private static async Task<MutationStatus> Exclude(MutationFixture f, string type = "Domain", string value = "owned.dn42")
    {
        var resource = ExactResource.Parse(type, value);
        var latest = await f.Store.GetLatestAsync(resource.Type, resource.Value);
        return await f.Coordinator.ExcludeAsync(f.Identity, type, value, expectedLatestRecordId: latest?.Id);
    }

    private static string Item(string html, string resource)
    {
        var start = html.IndexOf("<li>" + resource, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, start, html);
        return html[start..html.IndexOf("</li>", start, StringComparison.Ordinal)];
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}

using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json.Nodes;
using DN42Atlas.OptOut.Auth;
using DN42Atlas.OptOut.Exclusions;
using DN42Atlas.OptOut.Registry;
using DN42Atlas.OptOut.Web;
using DN42Atlas.Policy;
using DN42Atlas.Publishing;
using DN42Atlas.Registry;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;

namespace DN42Atlas.Tests;

[TestClass]
public sealed class ExclusionMutationTests
{
    [TestMethod]
    public void MutationConfigurationRequiresAbsoluteDistinctPrivatePaths()
    {
        using var files = new TestFiles();
        var config = new Dictionary<string, string?>
        {
            ["DN42ATLAS_EXCLUDED_HOSTS_PATH"] = files.HostsPath,
            ["DN42ATLAS_EXCLUDED_PREFIXES_PATH"] = files.PrefixesPath,
            ["DN42ATLAS_RUNTIME_EXCLUSIONS_PATH"] = files.RuntimePath,
            ["DN42ATLAS_EXCLUSION_DB_PATH"] = Path.Combine(files.DirectoryPath, "db.sqlite"),
            ["DN42ATLAS_PUBLISHED_PATH"] = Path.Combine(files.DirectoryPath, "published"),
            ["DN42ATLAS_PUBLICATION_STATE_PATH"] = Path.Combine(files.DirectoryPath, "state.json")
        };
        MutationPaths Read() => MutationPaths.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(config).Build());
        Assert.AreEqual(files.HostsPath, Read().Hosts);
        config["DN42ATLAS_EXCLUDED_HOSTS_PATH"] = "relative.txt";
        Assert.ThrowsExactly<InvalidOperationException>(() => Read());
        config["DN42ATLAS_EXCLUDED_HOSTS_PATH"] = null;
        Assert.ThrowsExactly<InvalidOperationException>(() => Read());
        config["DN42ATLAS_EXCLUDED_HOSTS_PATH"] = files.HostsPath;
        config["DN42ATLAS_PUBLICATION_STATE_PATH"] = Path.Combine(config["DN42ATLAS_PUBLISHED_PATH"]!, "state.json");
        Assert.ThrowsExactly<InvalidDataException>(() => Read());
        config["DN42ATLAS_PUBLICATION_STATE_PATH"] = files.RuntimePath;
        Assert.ThrowsExactly<InvalidOperationException>(() => Read());
        config["DN42ATLAS_PUBLICATION_STATE_PATH"] = files.RuntimePath + ".reconciliation-pending";
        Assert.ThrowsExactly<InvalidOperationException>(() => Read());
        config["DN42ATLAS_PUBLICATION_STATE_PATH"] = Path.Combine(files.DirectoryPath, "state.json");
        config["DN42ATLAS_MANUAL_REQUEST_DB_PATH"] = files.RuntimePath + ".reconciliation-pending";
        Assert.ThrowsExactly<InvalidOperationException>(() => Read());
        Assert.ThrowsExactly<InvalidOperationException>(() => DN42Atlas.OptOut.ManualRequests.ManualRequestStore.ConfiguredPath(
            new ConfigurationBuilder().AddInMemoryCollection(config).Build()));
    }

    [TestMethod]
    public async Task DashboardListsOnlyExactNormalizedDomainObjects()
    {
        using var fixture = await MutationFixture.CreateAsync();
        var directory = Path.GetDirectoryName(fixture.DomainFile)!;
        File.WriteAllText(Path.Combine(directory, "wildcard"), "domain: *.owned.dn42\nmnt-by: OWNER-MNT\n");
        File.WriteAllText(Path.Combine(directory, "malformed"), "domain: bad@owned.dn42\nmnt-by: OWNER-MNT\n");
        File.WriteAllText(fixture.DomainFile, "domain: OWNED.DN42\nmnt-by: OWNER-MNT\n");
        CollectionAssert.AreEqual(new[] { "owned.dn42" }, fixture.Domains.FindDomains("OWNER-MNT").ToArray());
    }

    [TestMethod]
    public async Task OperatorRoutesAndSingletonServicesWorkWithRealHttpForms()
    {
        using var fixture = await MutationFixture.CreateAsync();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        builder.Services.AddSingleton(fixture.Domains);
        builder.Services.AddSingleton(fixture.Allocations);
        builder.Services.AddSingleton(fixture.SnapshotService);
        builder.Services.AddSingleton(fixture.Store);
        builder.Services.AddSingleton(fixture.Paths);
        builder.Services.AddSingleton(new RegistryResourceAuthorizer(fixture.Domains, fixture.Allocations, () => fixture.Snapshot));
        builder.Services.AddSingleton<ExclusionReconciler>();
        builder.Services.AddSingleton<ExclusionMutationCoordinator>();
        builder.Services.AddSingleton<ConfirmationTokens>();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddAntiforgery(options =>
        {
            options.Cookie.Name = "__Host-DN42Atlas.Antiforgery";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            // Test authentication only; production uses its reduced, authenticated cookie principal.
            context.Request.Scheme = "https";
            if (context.Request.Headers["X-Test-Identity"] == "yes") context.User = fixture.Identity.ToPrincipal("test");
            await next(context);
        });
        OperatorEndpoints.Map(app);
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = new Uri(address) };
            using var anonymous = await client.PostAsync("/operator", new FormUrlEncodedContent(new Dictionary<string, string>()));
            Assert.AreEqual(System.Net.HttpStatusCode.Unauthorized, anonymous.StatusCode);
            client.DefaultRequestHeaders.Add("X-Test-Identity", "yes");
            using var missingToken = await client.PostAsync("/operator", new FormUrlEncodedContent(new Dictionary<string, string>
                { ["resourceType"] = "Domain", ["resourceValue"] = "owned.dn42" }));
            Assert.AreEqual(System.Net.HttpStatusCode.BadRequest, missingToken.StatusCode);
            using var page = await client.GetAsync("/operator");
            var html = await page.Content.ReadAsStringAsync();
            var match = System.Text.RegularExpressions.Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"");
            Assert.IsTrue(match.Success);
            Assert.Contains("no-store", page.Headers.CacheControl!.ToString());
            var cookie = page.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
            client.DefaultRequestHeaders.Add("Cookie", cookie);
            using var confirmation = await client.PostAsync("/operator", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["intent"] = "confirm-exclude",
                ["resourceType"] = "Domain", ["resourceValue"] = "owned.dn42",
                ["__RequestVerificationToken"] = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value)
            }));
            Assert.AreEqual(System.Net.HttpStatusCode.OK, confirmation.StatusCode);
            Assert.IsEmpty(await fixture.Store.GetActiveAsync());
            html = await confirmation.Content.ReadAsStringAsync();
            var protectedToken = System.Text.RegularExpressions.Regex.Match(html, "name=\"confirmationToken\" value=\"([^\"]+)\"");
            using var result = await client.PostAsync("/operator", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["intent"] = "exclude", ["resourceType"] = "Domain", ["resourceValue"] = "owned.dn42",
                ["__RequestVerificationToken"] = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value),
                ["confirmationToken"] = System.Net.WebUtility.HtmlDecode(protectedToken.Groups[1].Value)
            }));
            Assert.AreEqual(System.Net.HttpStatusCode.Redirect, result.StatusCode);
            Assert.AreEqual("/operator?result=excluded", result.Headers.Location!.OriginalString);
            Assert.HasCount(1, await fixture.Store.GetActiveAsync());
        }
        finally { await app.StopAsync(); }
    }

    [TestMethod]
    [DataRow("Domain", " OWNED.DN42. ", "owned.dn42")]
    [DataRow("IPv4Prefix", "172.20.16.0/24", "172.20.16.0/24")]
    [DataRow("IPv6Prefix", "fd42:1234::/48", "fd42:1234::/48")]
    public async Task ExactOwnedResourcePostStoresEvidenceAndReconcilesWithoutChangingRaw(string type, string value, string normalized)
    {
        using var fixture = await MutationFixture.CreateAsync();
        var original = await File.ReadAllBytesAsync(fixture.RawPath);
        var context = await fixture.ConfirmedPostAsync("exclude", type, value);
        var result = await OperatorEndpoints.PostAsync(context, fixture.Antiforgery, fixture.Coordinator);
        Assert.AreEqual("/operator?result=excluded", ((RedirectHttpResult)result).Url);
        Assert.Contains("no-store", context.Response.Headers.CacheControl.ToString());
        var active = await fixture.Store.GetActiveAsync();
        Assert.HasCount(1, active);
        var record = active.Single();
        Assert.AreEqual(normalized, record.ResourceValue);
        Assert.AreEqual(fixture.Identity.Subject, record.Subject);
        Assert.AreEqual(fixture.Identity.ActiveMaintainer, record.Maintainer);
        Assert.AreEqual(fixture.Identity.Asn, record.Asn);
        Assert.AreEqual(fixture.Snapshot.CommitSha, record.RegistryCommitSha);
        Assert.AreEqual(fixture.Snapshot.ObservedAt, record.RegistryObservedAtUtc);
        var rules = RuntimeExclusionBundle.Load(fixture.Paths.Runtime);
        Assert.IsTrue(type == "Domain" ? rules.HostRules.Contains(normalized) : rules.PrefixRules.Contains(normalized));
        var json = await File.ReadAllTextAsync(Path.Combine(fixture.Paths.Published, "latest.json"));
        var html = await File.ReadAllTextAsync(Path.Combine(fixture.Paths.Published, "index.html"));
        var forbidden = type switch { "Domain" => "owned.dn42", "IPv4Prefix" => "172.20.16.7", _ => "fd42:1234::1" };
        Assert.DoesNotContain(forbidden, json);
        Assert.DoesNotContain(forbidden, html);
        Assert.IsTrue(JsonNode.DeepEquals(JsonNode.Parse(json), Embedded(html)));
        Assert.AreEqual("2026-10-03T12:50:26Z", JsonNode.Parse(json)!["GeneratedAt"]!.GetValue<string>());
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(fixture.RawPath));
        Assert.AreEqual(fixture.RawPath, (await PublicationState.LoadAsync(fixture.Paths.State, fixture.Paths.Published)).RawScanPath);
    }

    [TestMethod]
    [DataRow("anonymous", 401)]
    [DataRow("missing-token", 400)]
    [DataRow("invalid-token", 400)]
    [DataRow("wrong-cookie", 400)]
    [DataRow("identity-changed", 400)]
    [DataRow("extra-field", 400)]
    public async Task AuthenticationAndAntiforgeryRejectBeforeMutation(string failure, int expectedCode)
    {
        using var fixture = await MutationFixture.CreateAsync();
        var context = fixture.PostContext("Domain", "owned.dn42");
        var form = context.Request.Form.ToDictionary(p => p.Key, p => p.Value);
        switch (failure)
        {
            case "anonymous": context.User = new ClaimsPrincipal(new ClaimsIdentity()); break;
            case "missing-token": form.Remove("__RequestVerificationToken"); break;
            case "invalid-token": form["__RequestVerificationToken"] = "invalid"; break;
            case "wrong-cookie": context.Request.Headers.Cookie = ""; break;
            case "identity-changed": context.User = new Auth42Identity("different-subject", "DIFFERENT-MNT", 42).ToPrincipal("test"); break;
            case "extra-field": form["maintainer"] = "OWNER-MNT"; break;
        }
        context.Request.Form = new FormCollection(form);
        var before = await File.ReadAllBytesAsync(fixture.Paths.Runtime);
        var result = await OperatorEndpoints.PostAsync(context, fixture.Antiforgery, fixture.Coordinator);
        Assert.AreEqual(expectedCode, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.IsEmpty(await fixture.Store.GetActiveAsync());
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fixture.Paths.Runtime));
    }

    [TestMethod]
    [DataRow("Domain", "*.owned.dn42")]
    [DataRow("Domain", "not.example")]
    [DataRow("Domain", "bad@owned.dn42")]
    [DataRow("IPv4Prefix", "fd42:1234::/48")]
    [DataRow("IPv6Prefix", "172.20.16.0/24")]
    [DataRow("IPv4Prefix", "broken/999")]
    [DataRow("0", "owned.dn42")]
    [DataRow("Unknown", "owned.dn42")]
    public async Task MalformedResourcesDoNotChangeState(string type, string value)
    {
        using var fixture = await MutationFixture.CreateAsync();
        Assert.AreEqual(MutationStatus.InvalidRequest, await fixture.Coordinator.ExcludeAsync(fixture.Identity, type, value));
        Assert.IsEmpty(await fixture.Store.GetActiveAsync());
        Assert.AreEqual(MutationStatus.InvalidRequest, await fixture.Coordinator.ExcludeAsync(fixture.Identity, "Domain", new string('x', 254)));
    }

    [TestMethod]
    [DataRow("Domain", "other.dn42")]
    [DataRow("IPv4Prefix", "172.20.16.0/23")]
    [DataRow("IPv4Prefix", "172.20.16.0/25")]
    [DataRow("IPv6Prefix", "fd42:1234::/47")]
    [DataRow("IPv6Prefix", "fd42:1234::/49")]
    public async Task OnlyExactLocalMaintainerOwnershipAuthorizes(string type, string value)
    {
        using var fixture = await MutationFixture.CreateAsync();
        // The ASN matches the owner fixture, and browser claims pretend to own routes. Neither grants authority.
        var context = fixture.PostContext(type, value);
        ((ClaimsIdentity)context.User.Identity!).AddClaim(new Claim("route", "172.20.16.0/23"));
        ((ClaimsIdentity)context.User.Identity!).AddClaim(new Claim("route6", "fd42:1234::/47"));
        var result = await OperatorEndpoints.PostAsync(context, fixture.Antiforgery, fixture.Coordinator);
        Assert.AreEqual(403, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.IsEmpty(await fixture.Store.GetActiveAsync());
    }

    [TestMethod]
    [DataRow(RegistrySnapshotStatus.Stale)]
    [DataRow(RegistrySnapshotStatus.Dirty)]
    [DataRow(RegistrySnapshotStatus.Unknown)]
    public async Task UnsafeSnapshotCannotCreateExclusion(RegistrySnapshotStatus status)
    {
        using var fixture = await MutationFixture.CreateAsync();
        fixture.Snapshot = fixture.Snapshot with { Status = status };
        Assert.AreEqual(MutationStatus.NotAuthorized,
            await fixture.Coordinator.ExcludeAsync(fixture.Identity, "Domain", "owned.dn42"));
        Assert.IsEmpty(await fixture.Store.GetActiveAsync());
    }

    [TestMethod]
    public async Task ChangedOwnershipAndUnreadableCatalogAreRecheckedOnPost()
    {
        using var fixture = await MutationFixture.CreateAsync();
        var context = fixture.PostContext("Domain", "owned.dn42");
        Assert.Contains("owned.dn42", fixture.Domains.FindDomains("OWNER-MNT"));
        File.WriteAllText(fixture.DomainFile, "domain: owned.dn42\nmnt-by: OTHER-MNT\n");
        var result = await OperatorEndpoints.PostAsync(context, fixture.Antiforgery, fixture.Coordinator);
        Assert.AreEqual(403, ((IStatusCodeHttpResult)result).StatusCode);
        Directory.Move(Path.GetDirectoryName(fixture.DomainFile)!, Path.GetDirectoryName(fixture.DomainFile)! + "-unavailable");
        Assert.AreEqual(MutationStatus.NotRecorded, await fixture.Coordinator.ExcludeAsync(fixture.Identity, "Domain", "owned.dn42"));
        Assert.IsEmpty(await fixture.Store.GetActiveAsync());
    }

    [TestMethod]
    public async Task RegistryChangeDuringOwnershipReadRejectsAuthorization()
    {
        using var fixture = await MutationFixture.CreateAsync();
        var reads = 0;
        var authorizer = new RegistryResourceAuthorizer(fixture.Domains, fixture.Allocations, () =>
            ++reads == 1 ? fixture.Snapshot : fixture.Snapshot with { CommitSha = new string('b', 40) });
        Assert.IsNull(authorizer.Authorize("OWNER-MNT", ExactResource.Parse("Domain", "owned.dn42")));
    }

    [TestMethod]
    public async Task DuplicateRequestReconcilesButDoesNotCreateAnotherActiveRecord()
    {
        using var fixture = await MutationFixture.CreateAsync();
        Assert.AreEqual(MutationStatus.Success, await fixture.Coordinator.ExcludeAsync(fixture.Identity, "Domain", "owned.dn42"));
        File.Delete(fixture.Paths.Runtime);
        File.Delete(Path.Combine(fixture.Paths.Published, "latest.json"));
        Assert.AreEqual(MutationStatus.Success, await fixture.Coordinator.ExcludeAsync(fixture.Identity, "Domain", "owned.dn42"));
        Assert.HasCount(1, await fixture.Store.GetActiveAsync());
        Assert.Contains("owned.dn42", RuntimeExclusionBundle.Load(fixture.Paths.Runtime).HostRules);
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Paths.Published, "latest.json")));
    }

    [TestMethod]
    public async Task DatabaseFailureRestoresPolicyOnlyAfterConfirmingNoRecordWasAdded()
    {
        using var fixture = await MutationFixture.CreateAsync();
        using (var db = new SqliteConnection($"Data Source={fixture.DbPath};Pooling=False"))
        {
            db.Open();
            using var trigger = db.CreateCommand();
            trigger.CommandText = "CREATE TRIGGER reject_insert BEFORE INSERT ON Exclusions BEGIN SELECT RAISE(ABORT, 'simulated failure'); END;";
            trigger.ExecuteNonQuery();
        }
        var runtime = await File.ReadAllBytesAsync(fixture.Paths.Runtime);
        var html = await File.ReadAllBytesAsync(Path.Combine(fixture.Paths.Published, "index.html"));
        Assert.AreEqual(MutationStatus.NotRecorded, await fixture.Coordinator.ExcludeAsync(fixture.Identity, "Domain", "owned.dn42"));
        Assert.IsEmpty(await fixture.Store.GetActiveAsync());
        CollectionAssert.AreEqual(runtime, await File.ReadAllBytesAsync(fixture.Paths.Runtime));
        CollectionAssert.AreEqual(html, await File.ReadAllBytesAsync(Path.Combine(fixture.Paths.Published, "index.html")));
    }

    [TestMethod]
    public async Task MaterializationFailureKeepsRecordAndPolicyUnavailableUntilReconciliation()
    {
        using var fixture = await MutationFixture.CreateAsync();
        using var coordinator = fixture.CreateCoordinator(_ => Task.FromException(new IOException("simulated materialization failure")));
        Assert.AreEqual(MutationStatus.RecordedRuntimeUnavailable, await coordinator.ExcludeAsync(fixture.Identity, "Domain", "owned.dn42"));
        Assert.HasCount(1, await fixture.Store.GetActiveAsync());
        Assert.IsFalse(File.Exists(fixture.Paths.Runtime));
        Assert.ThrowsExactly<FileNotFoundException>(() => fixture.Files.LoadPolicy(fixture.Paths.Runtime));
        fixture.AssertListingWithdrawn();
        Assert.AreEqual(ReconciliationStatus.Success, await fixture.Coordinator.ReconcileAsync());
        Assert.Contains("owned.dn42", RuntimeExclusionBundle.Load(fixture.Paths.Runtime).HostRules);
        Assert.DoesNotContain("owned.dn42", await File.ReadAllTextAsync(Path.Combine(fixture.Paths.Published, "latest.json")));
    }

    [TestMethod]
    public async Task InvalidMaterializedBundleIsWithdrawnRatherThanLeavingOldPolicyUsable()
    {
        using var fixture = await MutationFixture.CreateAsync();
        using var coordinator = fixture.CreateCoordinator(_ => File.WriteAllTextAsync(fixture.Paths.Runtime, "malformed bundle"));
        Assert.AreEqual(MutationStatus.RecordedRuntimeUnavailable,
            await coordinator.ExcludeAsync(fixture.Identity, "Domain", "owned.dn42"));
        Assert.HasCount(1, await fixture.Store.GetActiveAsync());
        Assert.IsFalse(File.Exists(fixture.Paths.Runtime));
        fixture.AssertListingWithdrawn();
    }

    [TestMethod]
    public async Task ReconciliationFencesAnExistingPolicyBeforeMaterialization()
    {
        using var fixture = await MutationFixture.CreateAsync();
        using var coordinator = fixture.CreateCoordinator(_ =>
        {
            Assert.IsFalse(File.Exists(fixture.Paths.Runtime));
            return Task.FromException(new IOException("simulated recovery failure"));
        });
        Assert.AreEqual(ReconciliationStatus.RuntimeUnavailable, await coordinator.ReconcileAsync());
        Assert.IsFalse(File.Exists(fixture.Paths.Runtime));
        fixture.AssertListingWithdrawn();
    }

    [TestMethod]
    public async Task RecordedExclusionsRemainEffectiveAfterRegistryBecomesStale()
    {
        using var fixture = await MutationFixture.CreateAsync();
        Assert.AreEqual(MutationStatus.Success, await fixture.Coordinator.ExcludeAsync(fixture.Identity, "Domain", "owned.dn42"));
        fixture.Snapshot = fixture.Snapshot with { Status = RegistrySnapshotStatus.Stale };
        Assert.AreEqual(MutationStatus.NotAuthorized,
            await fixture.Coordinator.ExcludeAsync(fixture.Identity, "IPv4Prefix", "172.20.16.0/24"));
        Assert.IsTrue(fixture.Files.LoadPolicy(fixture.Paths.Runtime).IsHostExcluded("owned.dn42"));
        Assert.DoesNotContain("owned.dn42", await File.ReadAllTextAsync(Path.Combine(fixture.Paths.Published, "latest.json")));
    }

    [TestMethod]
    public async Task PublicationFailureKeepsEffectiveExclusionAndWithdrawsOnlyListing()
    {
        using var fixture = await MutationFixture.CreateAsync();
        var original = await File.ReadAllBytesAsync(fixture.RawPath);
        File.WriteAllText(fixture.RawPath, "replaced source");
        Assert.AreEqual(MutationStatus.RecordedPublicationWithdrawn,
            await fixture.Coordinator.ExcludeAsync(fixture.Identity, "Domain", "owned.dn42"));
        Assert.HasCount(1, await fixture.Store.GetActiveAsync());
        Assert.IsTrue(fixture.Files.LoadPolicy(fixture.Paths.Runtime).IsHostExcluded("owned.dn42"));
        fixture.AssertListingWithdrawn();
        await File.WriteAllBytesAsync(fixture.RawPath, original);
        Assert.AreEqual(ReconciliationStatus.Success, await fixture.Coordinator.ReconcileAsync());
        Assert.DoesNotContain("owned.dn42", await File.ReadAllTextAsync(Path.Combine(fixture.Paths.Published, "index.html")));
    }

    [TestMethod]
    public async Task ConcurrentMutationsSerializeDatabaseRuntimeAndPublication()
    {
        using var fixture = await MutationFixture.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var coordinator = fixture.CreateCoordinator(async _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.SetResult();
                await release.Task;
            }
            await RuntimeExclusionMaterializer.MaterializeAsync(fixture.Store, fixture.Paths.Runtime);
        });
        var first = coordinator.ExcludeAsync(fixture.Identity, "Domain", "owned.dn42");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = coordinator.ExcludeAsync(fixture.Identity, "IPv4Prefix", "172.20.16.0/24");
        Assert.IsFalse(second.IsCompleted);
        Assert.HasCount(1, await fixture.Store.GetActiveAsync());
        release.SetResult();
        CollectionAssert.AreEqual(new[] { MutationStatus.Success, MutationStatus.Success }, await Task.WhenAll(first, second));
        Assert.HasCount(2, await fixture.Store.GetActiveAsync());
        var policy = fixture.Files.LoadPolicy(fixture.Paths.Runtime);
        Assert.IsTrue(policy.IsHostExcluded("owned.dn42"));
        Assert.IsTrue(policy.IsAddressExcluded("172.20.16.7"));
    }

    [TestMethod]
    public async Task DashboardFormsUseRealTokensHideAuditAndShowExcludedResources()
    {
        using var fixture = await MutationFixture.CreateAsync();
        await fixture.Store.AddAsync(ExclusionStoreTests.NewRecord(ExclusionResourceType.Domain, "other.dn42") with
            { Subject = "UNRELATED-SUBJECT", Maintainer = "OTHER-MNT" });
        var context = fixture.Context();
        var page = await OperatorEndpoints.GetAsync(context, fixture.Domains, fixture.Allocations,
            fixture.SnapshotService, fixture.Store, fixture.Paths, fixture.Antiforgery, fixture.Logs);
        var html = ((ContentHttpResult)page).ResponseContent!;
        Assert.Contains("method=\"post\" action=\"/operator\"", html);
        Assert.Contains("name=\"__RequestVerificationToken\"", html);
        Assert.DoesNotContain("other.dn42", html);
        Assert.DoesNotContain("UNRELATED-SUBJECT", html);
        Assert.DoesNotContain("OTHER-MNT", html);
        Assert.DoesNotContain(fixture.Snapshot.CommitSha!, html);
        Assert.Contains("no-store", context.Response.Headers.CacheControl.ToString());
        var cookie = context.Response.Headers.SetCookie.ToString();
        Assert.Contains("secure", cookie);
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=strict", cookie);
        Assert.AreEqual(MutationStatus.Success, await fixture.Coordinator.ExcludeAsync(fixture.Identity, "Domain", "owned.dn42"));
        page = await OperatorEndpoints.GetAsync(fixture.Context(), fixture.Domains, fixture.Allocations,
            fixture.SnapshotService, fixture.Store, fixture.Paths, fixture.Antiforgery, fixture.Logs);
        html = ((ContentHttpResult)page).ResponseContent!;
        Assert.Contains("owned.dn42 <strong>Excluded by self-service</strong>", html);
        Assert.Contains("Include again", html);
    }

    [TestMethod]
    [DataRow(RegistrySnapshotStatus.Stale)]
    [DataRow(RegistrySnapshotStatus.Dirty)]
    [DataRow(RegistrySnapshotStatus.Unknown)]
    public async Task UnsafeDashboardHasNoEnabledControls(RegistrySnapshotStatus status)
    {
        var snapshot = new RegistrySnapshot("/registry", "private-commit", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, TimeSpan.Zero, status);
        var html = OptOutPage.RenderSignedIn(new("subject", "OWNER-MNT", 42), ["owned.dn42"], [], [], snapshot,
            requestToken: "<token>");
        Assert.DoesNotContain("<form", html);
        Assert.DoesNotContain("<button", html);
        Assert.DoesNotContain("private-commit", html);
    }

    [TestMethod]
    public void FormValuesAndTokenRemainHtmlEncoded()
    {
        var snapshot = new RegistrySnapshot("/registry", "private", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, TimeSpan.Zero, RegistrySnapshotStatus.Fresh);
        var html = OptOutPage.RenderSignedIn(new("subject", "OWNER-MNT", 42), ["\"><script>alert(1)</script>.dn42"], [], [], snapshot,
            requestToken: "\"><img src=x>");
        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.DoesNotContain("<img src=x>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [TestMethod]
    public async Task MaintenanceCliRepairsRecordedStateWithoutOidcOrNewExclusions()
    {
        using var fixture = await MutationFixture.CreateAsync();
        await fixture.Store.AddAsync(ExclusionStoreTests.NewRecord(ExclusionResourceType.Domain, "owned.dn42"));
        File.Delete(fixture.Paths.Runtime);
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetTempPath(), RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        start.ArgumentList.Add(typeof(ExclusionStore).Assembly.Location);
        start.ArgumentList.Add("exclusions-reconcile");
        foreach (var (name, path) in new[]
        {
            ("DN42ATLAS_EXCLUSION_DB_PATH", fixture.DbPath), ("DN42ATLAS_RUNTIME_EXCLUSIONS_PATH", fixture.Paths.Runtime),
            ("DN42ATLAS_EXCLUDED_HOSTS_PATH", fixture.Paths.Hosts), ("DN42ATLAS_EXCLUDED_PREFIXES_PATH", fixture.Paths.Prefixes),
            ("DN42ATLAS_PUBLISHED_PATH", fixture.Paths.Published), ("DN42ATLAS_PUBLICATION_STATE_PATH", fixture.Paths.State)
        }) start.Environment[name] = path;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        Assert.AreEqual(0, process.ExitCode, await error);
        Assert.Contains("reconciled", await output);
        Assert.HasCount(1, await fixture.Store.GetActiveAsync());
        Assert.IsTrue(fixture.Files.LoadPolicy(fixture.Paths.Runtime).IsHostExcluded("owned.dn42"));
        Assert.DoesNotContain("owned.dn42", await File.ReadAllTextAsync(Path.Combine(fixture.Paths.Published, "latest.json")));
    }

    [TestMethod]
    public async Task UnixPrivateFilesAreOwnerOnly()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = await MutationFixture.CreateAsync();
        foreach (var path in new[] { fixture.DbPath, fixture.Paths.Runtime, fixture.Paths.State })
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        await fixture.Coordinator.ExcludeAsync(fixture.Identity, "Domain", "owned.dn42");
        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(fixture.Paths.Runtime));
    }

    private static JsonNode Embedded(string html)
    {
        var start = html.IndexOf("const scan = ", StringComparison.Ordinal) + "const scan = ".Length;
        return JsonNode.Parse(html[start..html.IndexOf(';', start)])!;
    }
}

internal sealed class MutationFixture : IDisposable
{
    public TestFiles Files { get; }
    private MutationFixture(TestFiles? files) { Files = files ?? new(); }
    public Auth42Identity Identity { get; } = new("operator-subject", "OWNER-MNT", 4242420425);
    public MutationPaths Paths { get; private set; } = null!;
    public string DbPath => Path.Combine(Files.DirectoryPath, "audit.db");
    public string RawPath => Path.Combine(Files.DirectoryPath, "raw.json");
    public string DomainFile => Path.Combine(Files.DirectoryPath, "registry", "data", "dns", "owned");
    public RegistryDomainCatalog Domains { get; private set; } = null!;
    public RegistryAllocationCatalog Allocations { get; private set; } = null!;
    public ExclusionStore Store { get; private set; } = null!;
    public ExclusionMutationCoordinator Coordinator { get; private set; } = null!;
    public RegistrySnapshot Snapshot { get; set; } = null!;
    public RegistrySnapshotService SnapshotService { get; private set; } = null!;
    public ServiceProvider Services { get; private set; } = null!;
    public IAntiforgery Antiforgery => Services.GetRequiredService<IAntiforgery>();
    public ILoggerFactory Logs => Services.GetRequiredService<ILoggerFactory>();

    public static async Task<MutationFixture> CreateAsync(TestFiles? files = null)
    {
        var f = new MutationFixture(files);
        f.Paths = new(f.Files.HostsPath, f.Files.PrefixesPath, f.Files.RuntimePath,
            Path.Combine(f.Files.DirectoryPath, "published"), Path.Combine(f.Files.DirectoryPath, "state.json"));
        await ExclusionMaintenance.InitializeAsync(f.DbPath, f.Paths.Runtime);
        f.Store = new(f.DbPath);
        var registry = Path.Combine(f.Files.DirectoryPath, "registry");
        foreach (var name in new[] { "dns", "inetnum", "inet6num" }) Directory.CreateDirectory(Path.Combine(registry, "data", name));
        File.WriteAllText(f.DomainFile, "domain: owned.dn42\nmnt-by: OWNER-MNT\n");
        File.WriteAllText(Path.Combine(registry, "data", "dns", "other"), "domain: other.dn42\nmnt-by: OTHER-MNT\n");
        File.WriteAllText(Path.Combine(registry, "data", "inetnum", "owned"), "inetnum: 172.20.16.0 - 172.20.16.255\ncidr: 172.20.16.0/24\nmnt-by: OWNER-MNT\n");
        File.WriteAllText(Path.Combine(registry, "data", "inet6num", "owned"), "inet6num: fd42:1234:: - fd42:1234:0:ffff:ffff:ffff:ffff:ffff\ncidr: fd42:1234::/48\nmnt-by: OWNER-MNT\n");
        f.Domains = new(Path.Combine(registry, "data", "dns"));
        f.Allocations = new(Path.Combine(registry, "data", "inetnum"), Path.Combine(registry, "data", "inet6num"));
        var git = Path.Combine(registry, ".git");
        Directory.CreateDirectory(git);
        var observed = DateTimeOffset.UtcNow;
        f.Snapshot = new(registry, new string('a', 40), observed, observed, TimeSpan.Zero, RegistrySnapshotStatus.Fresh);
        await RegistrySnapshotObservationStore.WriteAsync(git, new(f.Snapshot.CommitSha!, observed));
        f.SnapshotService = new(registry, TimeSpan.FromHours(72), new FixtureInspector(f, git));
        f.Files.Write("raw.json", """
            {"GeneratedAt":"2026-10-03T12:50:26Z","ExcludedByHostname":0,"Results":[
              {"Domain":"owned.dn42","Scheme":"http","Port":80,"ProbeAddresses":["172.20.16.7","fd42:1234::1"]},
              {"Domain":"other.dn42","Scheme":"http","Port":80,"ProbeAddresses":["fd42:5678::1"],"Title":"references owned.dn42 172.20.16.7 fd42:1234::1"}]}
            """);
        await ArtifactPublisher.PublishAsync(f.RawPath, f.Paths.Published, f.Files.LoadPolicy(), f.Paths.State);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ConfirmationTokens>();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddAntiforgery(options =>
        {
            options.Cookie.Name = "__Host-DN42Atlas.Antiforgery";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });
        f.Services = services.BuildServiceProvider();
        f.Coordinator = f.CreateCoordinator();
        return f;
    }

    public ExclusionMutationCoordinator CreateCoordinator(Func<CancellationToken, Task>? materialize = null,
        InclusionOperations? inclusionOperations = null)
    {
        var authorizer = new RegistryResourceAuthorizer(Domains, Allocations, () => Snapshot);
        var reconciler = new ExclusionReconciler(Store, Paths, NullLogger<ExclusionReconciler>.Instance, materialize);
        return new(Store, authorizer, reconciler, Paths, NullLogger<ExclusionMutationCoordinator>.Instance, inclusionOperations);
    }

    public DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext { RequestServices = Services, User = Identity.ToPrincipal("test") };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("dn42atlas.dn42");
        return context;
    }

    public DefaultHttpContext PostContext(string type, string value, string intent = "confirm-exclude",
        string? confirmationToken = null, Auth42Identity? identity = null)
    {
        var get = Context();
        if (identity != null) get.User = identity.ToPrincipal("test");
        var tokens = Antiforgery.GetAndStoreTokens(get);
        var post = Context();
        if (identity != null) post.User = identity.ToPrincipal("test");
        post.Request.Method = "POST";
        post.Request.ContentType = "application/x-www-form-urlencoded";
        post.Request.Headers.Cookie = get.Response.Headers.SetCookie.ToString().Split(';')[0];
        post.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["resourceType"] = type, ["resourceValue"] = value, ["__RequestVerificationToken"] = tokens.RequestToken!,
            ["intent"] = intent
        });
        if (confirmationToken != null)
            post.Request.Form = new FormCollection(post.Request.Form.ToDictionary(p => p.Key, p => p.Value)
                .Append(new KeyValuePair<string, StringValues>("confirmationToken", confirmationToken)).ToDictionary(p => p.Key, p => p.Value));
        return post;
    }

    public async Task<DefaultHttpContext> ConfirmedPostAsync(string operation, string type, string value,
        Auth42Identity? identity = null)
    {
        var first = PostContext(type, value, "confirm-" + operation, identity: identity);
        var response = await OperatorEndpoints.PostAsync(first, Antiforgery, Coordinator);
        var html = ((ContentHttpResult)response).ResponseContent!;
        Assert.AreEqual(200, ((ContentHttpResult)response).StatusCode ?? 200);
        var match = System.Text.RegularExpressions.Regex.Match(html, "name=\"confirmationToken\" value=\"([^\"]+)\"");
        Assert.IsTrue(match.Success, html);
        return PostContext(type, value, operation, System.Net.WebUtility.HtmlDecode(match.Groups[1].Value), identity);
    }

    public void AssertListingWithdrawn()
    {
        Assert.IsFalse(File.Exists(Path.Combine(Paths.Published, "index.html")));
        Assert.IsFalse(File.Exists(Path.Combine(Paths.Published, "latest.json")));
        foreach (var name in new[] { "about.html", "opt-out.html", "robots.txt" })
            Assert.IsTrue(File.Exists(Path.Combine(Paths.Published, name)));
    }

    public void Dispose() { Coordinator.Dispose(); Services.Dispose(); Files.Dispose(); }

    private sealed class FixtureInspector(MutationFixture f, string git) : IRegistryRepositoryInspector
    {
        public bool TryInspect(string root, out RegistryRepositoryState? state)
        {
            state = new(f.Snapshot.CommitSha!, f.Snapshot.CommitTimestamp, git, f.Snapshot.Status != RegistrySnapshotStatus.Dirty);
            return f.Snapshot.Status != RegistrySnapshotStatus.Unknown;
        }
    }
}

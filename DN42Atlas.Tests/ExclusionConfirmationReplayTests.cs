using System.Text.Json;
using DN42Atlas.OptOut.Exclusions;
using DN42Atlas.OptOut.Registry;
using DN42Atlas.OptOut.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace DN42Atlas.Tests;

[TestClass]
public sealed class ExclusionConfirmationReplayTests
{
    [TestMethod]
    [DataRow("Domain", "owned.dn42")]
    [DataRow("IPv4Prefix", "172.20.16.0/24")]
    [DataRow("IPv6Prefix", "fd42:1234::/48")]
    public async Task ReplayAfterIncludeConflictsWithoutChangingFilesAndFreshConfirmationWorks(string type, string value)
    {
        using var f = await MutationFixture.CreateAsync();
        var resource = ExactResource.Parse(type, value);
        var oldPost = await f.ConfirmedPostAsync("exclude", type, value);
        AssertBinding(f, oldPost, resource, null);
        await AssertSuccess(f, oldPost, "excluded");
        var first = (await f.Store.GetActiveAsync()).Single();

        // Replay remains idempotent while the exclusion is active, even with a null historical binding.
        await AssertSuccess(f, oldPost, "excluded");
        Assert.AreEqual(first, (await f.Store.GetActiveAsync()).Single());
        Assert.AreEqual(1L, await CountRows(f));
        Assert.AreEqual(first, await f.Store.GetLatestAsync(resource.Type, resource.Value));

        await AssertSuccess(f, await f.ConfirmedPostAsync("include", type, value), "included");
        var revoked = (await f.Store.GetByIdAsync(first.Id))!;
        Assert.IsNotNull(revoked.RevokedUtc);
        Assert.IsEmpty(await f.Store.GetActiveAsync());
        await AssertConflictWithoutWrites(f, oldPost);
        Assert.AreEqual(revoked, await f.Store.GetByIdAsync(first.Id));
        Assert.AreEqual(1L, await CountRows(f));

        var freshPost = await f.ConfirmedPostAsync("exclude", type, value);
        AssertBinding(f, freshPost, resource, first.Id);
        await AssertSuccess(f, freshPost, "excluded");
        var second = (await f.Store.GetActiveAsync()).Single();
        Assert.IsGreaterThan(first.Id, second.Id);
        Assert.AreEqual(revoked, await f.Store.GetByIdAsync(first.Id));
        Assert.AreEqual(2L, await CountRows(f));
    }

    [TestMethod]
    public async Task NonNullHistoryBindingCannotSurviveAnotherExclusionGeneration()
    {
        using var f = await MutationFixture.CreateAsync();
        await AssertSuccess(f, await f.ConfirmedPostAsync("exclude", "Domain", "owned.dn42"), "excluded");
        var first = (await f.Store.GetActiveAsync()).Single();
        await AssertSuccess(f, await f.ConfirmedPostAsync("include", "Domain", "owned.dn42"), "included");

        // Two confirmations start from the same revoked generation; only one is used initially.
        var oldPost = await f.ConfirmedPostAsync("exclude", "Domain", "owned.dn42");
        AssertBinding(f, oldPost, new(ExclusionResourceType.Domain, "owned.dn42"), first.Id);
        await AssertSuccess(f, await f.ConfirmedPostAsync("exclude", "Domain", "owned.dn42"), "excluded");
        var second = (await f.Store.GetActiveAsync()).Single();
        Assert.IsGreaterThan(first.Id, second.Id);
        await AssertSuccess(f, await f.ConfirmedPostAsync("include", "Domain", "owned.dn42"), "included");
        await AssertConflictWithoutWrites(f, oldPost);
        Assert.AreEqual(2L, await CountRows(f));
        Assert.IsEmpty(await f.Store.GetActiveAsync());
        Assert.IsNotNull((await f.Store.GetByIdAsync(first.Id))!.RevokedUtc);
        Assert.IsNotNull((await f.Store.GetByIdAsync(second.Id))!.RevokedUtc);
    }

    [TestMethod]
    public async Task ActiveResourceDoesNotIssueNewExclusionConfirmation()
    {
        using var f = await MutationFixture.CreateAsync();
        await AssertSuccess(f, await f.ConfirmedPostAsync("exclude", "Domain", "owned.dn42"), "excluded");
        var oldPost = f.PostContext("Domain", "owned.dn42", "confirm-exclude");
        var active = (await f.Store.GetActiveAsync()).Single();
        await AssertConflictWithoutWrites(f, oldPost);
        Assert.AreEqual(active, (await f.Store.GetActiveAsync()).Single());
        var response = (ContentHttpResult)await OperatorEndpoints.PostAsync(oldPost, f.Antiforgery, f.Coordinator);
        Assert.DoesNotContain("name=\"confirmationToken\"", response.ResponseContent!);
    }

    [TestMethod]
    public async Task InclusionTokensKeepTheirExistingActiveRecordBinding()
    {
        using var f = await MutationFixture.CreateAsync();
        await AssertSuccess(f, await f.ConfirmedPostAsync("exclude", "Domain", "owned.dn42"), "excluded");
        var active = (await f.Store.GetActiveAsync()).Single();
        var resource = new ExactResource(ExclusionResourceType.Domain, "owned.dn42");
        // Existing include tokens issued before this change have no LatestRecordId field.
        var protector = f.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("DN42Atlas.OperatorConfirmation.v1");
        var oldFormatToken = protector.Protect(JsonSerializer.Serialize(new
        {
            Operation = "include", Resource = resource, f.Identity.Subject,
            Maintainer = f.Identity.ActiveMaintainer, IssuedAt = DateTimeOffset.UtcNow, ActiveRecordId = active.Id
        }));
        var tokens = f.Services.GetRequiredService<ConfirmationTokens>();
        Assert.IsTrue(tokens.TryValidate(oldFormatToken, "include", resource, f.Identity, out var id));
        Assert.AreEqual(active.Id, id);
        await AssertSuccess(f, f.PostContext("Domain", "owned.dn42", "include", oldFormatToken), "included");
    }

    private static void AssertBinding(MutationFixture f, DefaultHttpContext post, ExactResource resource, long? expected)
    {
        var tokens = f.Services.GetRequiredService<ConfirmationTokens>();
        Assert.IsTrue(tokens.TryValidate(post.Request.Form["confirmationToken"].ToString(), "exclude", resource, f.Identity, out var id));
        Assert.AreEqual(expected, id);
        CollectionAssert.AreEquivalent(new[] { "intent", "resourceType", "resourceValue", "confirmationToken", "__RequestVerificationToken" },
            post.Request.Form.Keys.ToArray());
    }

    private static async Task AssertSuccess(MutationFixture f, DefaultHttpContext post, string result)
    {
        var response = await OperatorEndpoints.PostAsync(post, f.Antiforgery, f.Coordinator);
        Assert.IsInstanceOfType<RedirectHttpResult>(response);
        Assert.AreEqual("/operator?result=" + result, ((RedirectHttpResult)response).Url);
    }

    private static async Task AssertConflictWithoutWrites(MutationFixture f, DefaultHttpContext post)
    {
        var paths = Directory.GetFiles(f.Files.DirectoryPath, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).ToArray();
        var before = paths.Select(File.ReadAllBytes).ToArray();
        var response = await OperatorEndpoints.PostAsync(post, f.Antiforgery, f.Coordinator);
        Assert.AreEqual(409, ((IStatusCodeHttpResult)response).StatusCode);
        Assert.Contains("no-store", post.Response.Headers.CacheControl.ToString());
        CollectionAssert.AreEqual(paths, Directory.GetFiles(f.Files.DirectoryPath, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).ToArray());
        for (var i = 0; i < paths.Length; i++) CollectionAssert.AreEqual(before[i], File.ReadAllBytes(paths[i]), paths[i]);
    }

    private static async Task<long> CountRows(MutationFixture f)
    {
        await using var connection = new SqliteConnection($"Data Source={f.DbPath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Exclusions;";
        return (long)(await command.ExecuteScalarAsync())!;
    }
}

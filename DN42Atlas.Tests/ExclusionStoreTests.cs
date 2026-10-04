using DN42Atlas.OptOut.Exclusions;
using DN42Atlas.Policy;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DN42Atlas.Tests;

[TestClass]
public sealed class ExclusionStoreTests
{
    [TestMethod]
    public async Task ExplicitInitializationCreatesSchemaAndEmptyBundle()
    {
        using var files = new TestFiles();
        var databasePath = Path.Combine(files.DirectoryPath, "exclusions.db");

        await ExclusionMaintenance.InitializeAsync(databasePath, files.RuntimePath);

        var store = new ExclusionStore(databasePath);
        store.ValidateExisting();
        Assert.IsEmpty(await store.GetActiveAsync());
        Assert.IsEmpty(RuntimeExclusionBundle.Load(files.RuntimePath).HostRules);
        Assert.IsEmpty(RuntimeExclusionBundle.Load(files.RuntimePath).PrefixRules);
    }

    [TestMethod]
    public async Task MissingDatabaseIsNotSilentlyCreated()
    {
        using var files = new TestFiles();
        var databasePath = Path.Combine(files.DirectoryPath, "missing.db");
        var store = new ExclusionStore(databasePath);

        await Assert.ThrowsExactlyAsync<FileNotFoundException>(
            () => store.GetActiveAsync());

        Assert.IsFalse(File.Exists(databasePath));
    }

    [TestMethod]
    public async Task ActiveExclusionPersistsAndAuditFieldsRoundTrip()
    {
        using var files = new TestFiles();
        var databasePath = await InitializeStoreAsync(files);
        var store = new ExclusionStore(databasePath);
        var input = NewRecord(
            ExclusionResourceType.Domain,
            " Example.DN42. ");

        var inserted = await store.AddAsync(input);
        var loaded = await new ExclusionStore(databasePath).GetByIdAsync(inserted.Id);

        Assert.IsNotNull(loaded);
        Assert.AreEqual("example.dn42", loaded.ResourceValue);
        Assert.AreEqual(input.Subject, loaded.Subject);
        Assert.AreEqual(input.Maintainer, loaded.Maintainer);
        Assert.AreEqual(input.Asn, loaded.Asn);
        Assert.AreEqual(input.RegistryCommitSha, loaded.RegistryCommitSha);
        Assert.AreEqual(input.RegistryObservedAtUtc, loaded.RegistryObservedAtUtc);
        Assert.AreEqual(input.CreatedUtc, loaded.CreatedUtc);
        Assert.IsNull(loaded.RevokedUtc);
    }

    [TestMethod]
    public async Task DuplicateActiveResourceIsHandledIdempotently()
    {
        using var files = new TestFiles();
        var databasePath = await InitializeStoreAsync(files);
        var store = new ExclusionStore(databasePath);

        var first = await store.AddAsync(NewRecord(
            ExclusionResourceType.Domain,
            "Example.DN42."));
        var duplicate = await store.AddAsync(NewRecord(
            ExclusionResourceType.Domain,
            "example.dn42"));

        Assert.AreEqual(first.Id, duplicate.Id);
        Assert.HasCount(1, await store.GetActiveAsync());
    }

    [TestMethod]
    public async Task RevokedRecordDoesNotMaterialize()
    {
        using var files = new TestFiles();
        var databasePath = await InitializeStoreAsync(files);
        var store = new ExclusionStore(databasePath);
        var record = await store.AddAsync(NewRecord(
            ExclusionResourceType.Domain,
            "example.dn42"));

        Assert.IsTrue(await store.RevokeAsync(
            record.Id,
            new DateTimeOffset(2026, 10, 3, 15, 0, 0, TimeSpan.Zero)));
        await ExclusionMaintenance.MaterializeAsync(databasePath, files.RuntimePath);

        Assert.IsEmpty(RuntimeExclusionBundle.Load(files.RuntimePath).HostRules);
        Assert.IsNotNull(await store.GetByIdAsync(record.Id));
        Assert.IsNotNull((await store.GetByIdAsync(record.Id))!.RevokedUtc);
    }

    [TestMethod]
    [DataRow(ExclusionResourceType.Domain, " Example.DN42. ", "example.dn42")]
    [DataRow(ExclusionResourceType.IPv4Prefix, "172.20.16.7/24", "172.20.16.0/24")]
    [DataRow(ExclusionResourceType.IPv6Prefix, "FD42:1234::7/48", "fd42:1234::/48")]
    public async Task LatestResourceHistoryIncludesRevokedRowsAndUsesMonotonicIds(
        ExclusionResourceType type, string input, string normalized)
    {
        using var files = new TestFiles();
        var store = new ExclusionStore(await InitializeStoreAsync(files));
        Assert.IsNull(await store.GetLatestAsync(type, input));
        var first = await store.AddAsync(NewRecord(type, input));
        Assert.AreEqual(first, await store.GetLatestAsync(type, normalized));
        Assert.IsTrue(await store.RevokeAsync(first.Id, DateTimeOffset.UtcNow));
        Assert.AreEqual(await store.GetByIdAsync(first.Id), await store.GetLatestAsync(type, input));
        var second = await store.AddAsync(NewRecord(type, normalized) with { CreatedUtc = first.CreatedUtc.AddDays(-1) });
        Assert.IsGreaterThan(first.Id, second.Id);
        Assert.AreEqual(second, await store.GetLatestAsync(type, input));
        await store.AddAsync(NewRecord(ExclusionResourceType.Domain, "unrelated.dn42"));
        Assert.AreEqual(second, await store.GetLatestAsync(type, input));
        Assert.IsTrue(await store.RevokeAsync(second.Id, DateTimeOffset.UtcNow));
        Assert.AreEqual(await store.GetByIdAsync(second.Id), await store.GetLatestAsync(type, input));
    }

    private static async Task<string> InitializeStoreAsync(TestFiles files)
    {
        var databasePath = Path.Combine(files.DirectoryPath, "exclusions.db");
        await ExclusionStore.InitializeAsync(databasePath);
        return databasePath;
    }

    internal static NewExclusionRecord NewRecord(
        ExclusionResourceType resourceType,
        string value) =>
        new(
            resourceType,
            value,
            "auth42-subject",
            "JOYFULREAPER-MNT",
            4242420425,
            "0123456789abcdef",
            new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 3, 12, 5, 0, TimeSpan.Zero));
}

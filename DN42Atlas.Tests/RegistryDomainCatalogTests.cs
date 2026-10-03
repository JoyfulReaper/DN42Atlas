using DN42Atlas.OptOut.Registry;

namespace DN42Atlas.Tests;

[TestClass]
public sealed class RegistryDomainCatalogTests
{
    [TestMethod]
    public void MaintainerMatchingIsCaseInsensitive()
    {
        using var directory = new RegistryDirectory();
        directory.Add(
            "dn42atlas",
            """
            domain: dn42atlas.dn42
            mnt-by: joyfulreaper-mnt
            """);

        var domains = directory.Catalog.FindDomains("JOYFULREAPER-MNT");

        CollectionAssert.AreEqual(
            new[] { "dn42atlas.dn42" },
            domains.ToArray());
    }

    [TestMethod]
    public void UnrelatedMaintainersAreNotReturned()
    {
        using var directory = new RegistryDirectory();
        directory.Add(
            "owned",
            """
            domain: owned.dn42
            mnt-by: JOYFULREAPER-MNT
            """);
        directory.Add(
            "unrelated",
            """
            domain: unrelated.dn42
            mnt-by: OTHER-MNT
            """);
        directory.Add(
            "outside",
            """
            domain: outside.example
            mnt-by: JOYFULREAPER-MNT
            """);

        var domains = directory.Catalog.FindDomains("JOYFULREAPER-MNT");

        CollectionAssert.AreEqual(
            new[] { "owned.dn42" },
            domains.ToArray());
    }

    [TestMethod]
    public void AnyMatchingMaintainerEntryAuthorizesTheExactDomain()
    {
        using var directory = new RegistryDirectory();
        directory.Add(
            "shared",
            """
            domain: shared.dn42
            mnt-by: FIRST-MNT
            mnt-by: JOYFULREAPER-MNT
            mnt-by: THIRD-MNT
            """);

        var domains = directory.Catalog.FindDomains("JOYFULREAPER-MNT");

        CollectionAssert.AreEqual(
            new[] { "shared.dn42" },
            domains.ToArray());
    }

    private sealed class RegistryDirectory : IDisposable
    {
        private readonly string path = Path.Combine(
            Path.GetTempPath(),
            $"dn42atlas-optout-tests-{Guid.NewGuid():N}");

        public RegistryDirectory()
        {
            Directory.CreateDirectory(path);
            Catalog = new RegistryDomainCatalog(path);
        }

        public RegistryDomainCatalog Catalog { get; }

        public void Add(string fileName, string contents)
        {
            File.WriteAllText(Path.Combine(path, fileName), contents);
        }

        public void Dispose()
        {
            Directory.Delete(path, recursive: true);
        }
    }
}

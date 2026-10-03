using DN42Atlas.OptOut.Registry;

namespace DN42Atlas.Tests;

[TestClass]
public sealed class RegistryAllocationCatalogTests
{
    [TestMethod]
    public void MatchingIpv4AllocationIsReturned()
    {
        using var registry = new AllocationRegistry();
        registry.AddIpv4("owned-v4", Ipv4Object(
            "172.20.220.48 - 172.20.220.63",
            "172.20.220.48/28",
            "JOYFULREAPER-MNT"));

        var allocations = registry.Catalog.FindAllocations("JOYFULREAPER-MNT");

        CollectionAssert.AreEqual(
            new[] { "172.20.220.48/28" },
            allocations.Ipv4Prefixes.ToArray());
        Assert.IsEmpty(allocations.Ipv6Prefixes);
    }

    [TestMethod]
    public void MatchingIpv6AllocationIsReturned()
    {
        using var registry = new AllocationRegistry();
        registry.AddIpv6("owned-v6", Ipv6Object(
            "fdf0:e12c:5528:0000:0000:0000:0000:0000 - fdf0:e12c:5528:ffff:ffff:ffff:ffff:ffff",
            "fdf0:e12c:5528::/48",
            "JOYFULREAPER-MNT"));

        var allocations = registry.Catalog.FindAllocations("JOYFULREAPER-MNT");

        CollectionAssert.AreEqual(
            new[] { "fdf0:e12c:5528::/48" },
            allocations.Ipv6Prefixes.ToArray());
        Assert.IsEmpty(allocations.Ipv4Prefixes);
    }

    [TestMethod]
    public void MaintainerMatchingIsCaseInsensitive()
    {
        using var registry = new AllocationRegistry();
        registry.AddIpv4("owned", Ipv4Object(
            "172.20.220.48 - 172.20.220.63",
            "172.20.220.48/28",
            "joyfulreaper-mnt"));

        var allocations = registry.Catalog.FindAllocations("JOYFULREAPER-MNT");

        Assert.HasCount(1, allocations.Ipv4Prefixes);
    }

    [TestMethod]
    public void UnrelatedMaintainerIsNotReturned()
    {
        using var registry = new AllocationRegistry();
        registry.AddIpv4("unrelated", Ipv4Object(
            "172.20.220.48 - 172.20.220.63",
            "172.20.220.48/28",
            "OTHER-MNT"));

        var allocations = registry.Catalog.FindAllocations("JOYFULREAPER-MNT");

        Assert.IsEmpty(allocations.Ipv4Prefixes);
    }

    [TestMethod]
    public void AnyMatchingMaintainerEntryReturnsAllocation()
    {
        using var registry = new AllocationRegistry();
        registry.AddIpv4("shared", Ipv4Object(
            "172.20.220.48 - 172.20.220.63",
            "172.20.220.48/28",
            "FIRST-MNT",
            "JOYFULREAPER-MNT",
            "THIRD-MNT"));

        var allocations = registry.Catalog.FindAllocations("JOYFULREAPER-MNT");

        Assert.HasCount(1, allocations.Ipv4Prefixes);
    }

    [TestMethod]
    public void MalformedPrefixesAreRejected()
    {
        using var registry = new AllocationRegistry();
        registry.AddIpv4("bad-v4", Ipv4Object(
            "172.20.220.48 - 172.20.220.63",
            "172.20.220.48/33",
            "JOYFULREAPER-MNT"));
        registry.AddIpv6("bad-v6", Ipv6Object(
            "fdf0:e12c:5528:: - fdf0:e12c:5528:ffff:ffff:ffff:ffff:ffff",
            "fdf0:e12c:5528::/129",
            "JOYFULREAPER-MNT"));

        var allocations = registry.Catalog.FindAllocations("JOYFULREAPER-MNT");

        Assert.IsEmpty(allocations.Ipv4Prefixes);
        Assert.IsEmpty(allocations.Ipv6Prefixes);
    }

    [TestMethod]
    public void CrossFamilyValuesAreRejected()
    {
        using var registry = new AllocationRegistry();
        registry.AddIpv4("v6-in-v4", Ipv4Object(
            "fdf0:e12c:5528:: - fdf0:e12c:5528:ffff:ffff:ffff:ffff:ffff",
            "fdf0:e12c:5528::/48",
            "JOYFULREAPER-MNT"));
        registry.AddIpv6("v4-in-v6", Ipv6Object(
            "172.20.220.48 - 172.20.220.63",
            "172.20.220.48/28",
            "JOYFULREAPER-MNT"));

        var allocations = registry.Catalog.FindAllocations("JOYFULREAPER-MNT");

        Assert.IsEmpty(allocations.Ipv4Prefixes);
        Assert.IsEmpty(allocations.Ipv6Prefixes);
    }

    [TestMethod]
    public void DuplicatePrefixesAreDeduplicatedAndSorted()
    {
        using var registry = new AllocationRegistry();
        registry.AddIpv4("second", Ipv4Object(
            "172.20.220.64 - 172.20.220.79",
            "172.20.220.64/28",
            "JOYFULREAPER-MNT"));
        registry.AddIpv4("first", Ipv4Object(
            "172.20.220.48 - 172.20.220.63",
            "172.20.220.48/28",
            "JOYFULREAPER-MNT"));
        registry.AddIpv4("first-duplicate", Ipv4Object(
            "172.20.220.48 - 172.20.220.63",
            "172.20.220.48/28",
            "JOYFULREAPER-MNT"));

        var allocations = registry.Catalog.FindAllocations("JOYFULREAPER-MNT");

        CollectionAssert.AreEqual(
            new[] { "172.20.220.48/28", "172.20.220.64/28" },
            allocations.Ipv4Prefixes.ToArray());
    }

    [TestMethod]
    public void CidrMustMatchTheDeclaredAllocationRange()
    {
        using var registry = new AllocationRegistry();
        registry.AddIpv4("mismatch", Ipv4Object(
            "172.20.220.64 - 172.20.220.79",
            "172.20.220.48/28",
            "JOYFULREAPER-MNT"));

        var allocations = registry.Catalog.FindAllocations("JOYFULREAPER-MNT");

        Assert.IsEmpty(allocations.Ipv4Prefixes);
    }

    private static string Ipv4Object(
        string range,
        string prefix,
        params string[] maintainers) =>
        Object("inetnum", range, prefix, maintainers);

    private static string Ipv6Object(
        string range,
        string prefix,
        params string[] maintainers) =>
        Object("inet6num", range, prefix, maintainers);

    private static string Object(
        string rangeAttribute,
        string range,
        string prefix,
        IReadOnlyList<string> maintainers) =>
        string.Join(
            Environment.NewLine,
            new[]
            {
                $"{rangeAttribute}: {range}",
                $"cidr: {prefix}",
                "netname: TEST-NETWORK"
            }
            .Concat(maintainers.Select(value => $"mnt-by: {value}"))
            .Append("source: DN42"));

    private sealed class AllocationRegistry : IDisposable
    {
        private readonly string root = Path.Combine(
            Path.GetTempPath(),
            $"dn42atlas-allocation-tests-{Guid.NewGuid():N}");
        private readonly string ipv4Directory;
        private readonly string ipv6Directory;

        public AllocationRegistry()
        {
            ipv4Directory = Path.Combine(root, "inetnum");
            ipv6Directory = Path.Combine(root, "inet6num");
            Directory.CreateDirectory(ipv4Directory);
            Directory.CreateDirectory(ipv6Directory);
            Catalog = new RegistryAllocationCatalog(
                ipv4Directory,
                ipv6Directory);
        }

        public RegistryAllocationCatalog Catalog { get; }

        public void AddIpv4(string fileName, string contents) =>
            File.WriteAllText(Path.Combine(ipv4Directory, fileName), contents);

        public void AddIpv6(string fileName, string contents) =>
            File.WriteAllText(Path.Combine(ipv6Directory, fileName), contents);

        public void Dispose() =>
            Directory.Delete(root, recursive: true);
    }
}

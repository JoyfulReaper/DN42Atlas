using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DN42Atlas.Commands;
using DN42Atlas.Networking;
using DN42Atlas.Probing;
using DN42Atlas.Registry;
using DN42Atlas.Scanning;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DN42Atlas.Tests;

[TestClass]
public sealed class ScanningTests
{
    [TestMethod]
    public async Task RegistryExcludesHostsBeforeDnsAndAddressesBeforeResults()
    {
        using var files = new TestFiles("blocked.dn42\n*.private.dn42", "172.20.1.0/24");
        var registryPath = Path.Combine(files.DirectoryPath, "dns");
        Directory.CreateDirectory(registryPath);
        string[] domains = ["blocked.dn42", "child.private.dn42", "prefix.dn42", "good.dn42", "missing.dn42", "temporary.dn42", "error.dn42", "other.example"];
        foreach (var domain in domains)
            File.WriteAllText(Path.Combine(registryPath, domain), $"domain: {domain}\n");
        File.WriteAllText(Path.Combine(registryPath, "malformed"), "no domain attribute");
        var queried = new List<string>();
        var resolver = new RegistryResolver(files.LoadPolicy(), hostname =>
        {
            queried.Add(hostname);
            return hostname switch
            {
                "prefix.dn42" => Task.FromResult(new[] { IPAddress.Parse("172.20.2.1"), IPAddress.Parse("172.20.1.1") }),
                "missing.dn42" => Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound)),
                "temporary.dn42" => Task.FromException<IPAddress[]>(new SocketException((int)SocketError.TryAgain)),
                "error.dn42" => Task.FromException<IPAddress[]>(new InvalidOperationException("DNS failure")),
                _ => Task.FromResult(new[] { IPAddress.Parse("fd42::1") })
            };
        });
        var scan = await resolver.ResolveAsync(registryPath);
        CollectionAssert.AreEqual(new[] { "error.dn42", "good.dn42", "missing.dn42", "prefix.dn42", "temporary.dn42" }, queried);
        Assert.AreEqual(5, scan.RegisteredDomainCount);
        CollectionAssert.AreEqual(new[] { "error.dn42", "good.dn42", "missing.dn42", "temporary.dn42" }, scan.Resolutions.Select(x => x.Domain).ToArray());
        CollectionAssert.AreEqual(new[] { ResolutionStatus.Error, ResolutionStatus.Resolved, ResolutionStatus.NotFound, ResolutionStatus.TemporaryFailure }, scan.Resolutions.Select(x => x.Status).ToArray());
    }

    [TestMethod]
    public async Task SavedResolutionFilteringOrderingAndPublicOutputStayCompatible()
    {
        using var files = new TestFiles("blocked.dn42\n*.private.dn42", "172.20.1.0/24");
        var resolutionPath = files.Write("resolution.json", """
            [
              {"Domain":"blocked.dn42","Status":"Resolved"},
              {"Domain":"child.private.dn42","Status":"Error"},
              {"Domain":"prefix.dn42","Status":"Resolved","Addresses":["172.20.2.1","172.20.1.1"]},
              {"Domain":"external.dn42","Status":"Resolved","Addresses":["192.0.2.1"]},
              {"Domain":"mixed.dn42","Status":"Resolved","Addresses":["fd42::1","192.0.2.1"]},
              {"Domain":"invalid.dn42","Status":"Resolved","Addresses":["invalid"]},
              {"Domain":"not-dn42.example","Status":"Resolved","Addresses":["fd42::1"]},
              {"Domain":"empty.dn42","Status":"Resolved","Addresses":[]},
              {"Domain":"failed.dn42","Status":"NotFound"},
              {"Domain":"","Status":"Resolved"},
              {"Domain":"z.DN42","Status":"Resolved","Addresses":["172.20.2.1"]},
              {"Domain":"a.dn42","Status":"Resolved","Addresses":["fd42::1"]},
              {"Domain":"a.dn42","Status":"Resolved","Addresses":["fd42::1"]}
            ]
            """);
        var calls = new ConcurrentBag<string>();
        var scanner = new WebScanner(files.LoadPolicy(), HttpProbeTargets.All, (domain, scheme, port, _) =>
        {
            calls.Add(domain);
            return Task.FromResult(new HttpProbeResult { Domain = domain, Scheme = scheme, Port = port, Reachable = true });
        });
        var previousDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = files.DirectoryPath;
            await new WebScanCommand(scanner, HttpProbeTargets.All).ExecuteAsync(["web-scan", resolutionPath]);
        }
        finally
        {
            Environment.CurrentDirectory = previousDirectory;
        }
        Assert.HasCount(46, calls);
        Assert.IsTrue(calls.All(x => x is "a.dn42" or "z.DN42"));
        Assert.IsFalse(calls.Contains("not-dn42.example"));
        var jsonPath = Directory.GetFiles(Path.Combine(files.DirectoryPath, "results"), "web-probe-*.json").Single();
        Assert.IsTrue(File.Exists(Path.ChangeExtension(jsonPath, ".html")));
        var json = await File.ReadAllTextAsync(jsonPath);
        foreach (var excluded in new[] { "blocked.dn42", "private.dn42", "172.20.1.0/24", "prefix.dn42", "not-dn42.example" })
            Assert.IsFalse(json.Contains(excluded, StringComparison.Ordinal));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        CollectionAssert.AreEqual(new[] { "GeneratedAt", "StartedAt", "FinishedAt", "DurationSeconds", "ResolutionSource", "ResolvedDomainsProbed", "SkippedExternalOrMixedDomains", "ExcludedByHostname", "ExcludedByPrefix", "ProbeTargetCount", "ReachableOrigins", "ProbeTargets", "Results" }, root.EnumerateObject().Select(x => x.Name).ToArray());
        Assert.AreEqual(2, root.GetProperty("ExcludedByHostname").GetInt32());
        Assert.AreEqual(1, root.GetProperty("ExcludedByPrefix").GetInt32());
        Assert.AreEqual(2, root.GetProperty("ResolvedDomainsProbed").GetInt32());
        Assert.AreEqual(46, root.GetProperty("ProbeTargetCount").GetInt32());
        Assert.AreEqual(46, root.GetProperty("ReachableOrigins").GetInt32());
        CollectionAssert.AreEqual(new[] { "external.dn42", "mixed.dn42", "invalid.dn42" }, root.GetProperty("SkippedExternalOrMixedDomains").EnumerateArray().Select(x => x.GetString()).ToArray());
        var results = root.GetProperty("Results").EnumerateArray().ToArray();
        var actual = results.Select(x => (x.GetProperty("Domain").GetString(), x.GetProperty("Scheme").GetString(), x.GetProperty("Port").GetInt32())).ToArray();
        var expected = new[] { "a.dn42", "z.DN42" }.SelectMany(domain => HttpProbeTargets.All.Select(target => (domain, target.Scheme, target.Port))).OrderBy(x => x.domain).ThenBy(x => x.Scheme).ThenBy(x => x.Port).ToArray();
        CollectionAssert.AreEqual(expected, actual);
        Assert.AreEqual(JsonSerializer.SerializeToElement(new HttpProbeResult { Domain = "a.dn42", Scheme = "http", Port = 80, Reachable = true }).GetRawText(), JsonSerializer.Serialize(results[0]));
    }

    [TestMethod]
    public async Task ScanConcurrencyRemains32()
    {
        using var files = new TestFiles();
        var path = files.Write("resolution.json", JsonSerializer.Serialize(Enumerable.Range(0, 40).Select(i => new { Domain = $"host{i}.dn42", Status = "Resolved", Addresses = new[] { "fd42::1" } })));
        var reachedLimit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximum = 0;
        var scanner = new WebScanner(files.LoadPolicy(), [("http", 80)], async (domain, scheme, port, _) =>
        {
            var current = Interlocked.Increment(ref active);
            int previous;
            do { previous = Volatile.Read(ref maximum); }
            while (current > previous && Interlocked.CompareExchange(ref maximum, current, previous) != previous);
            if (current == 32) reachedLimit.TrySetResult();
            await release.Task;
            Interlocked.Decrement(ref active);
            return new HttpProbeResult { Domain = domain, Scheme = scheme, Port = port };
        });
        var scanTask = scanner.ScanAsync(path);
        try { await reachedLimit.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { release.TrySetResult(); }
        var scan = await scanTask;
        Assert.AreEqual(32, maximum);
        Assert.HasCount(40, scan.Results);
    }

    [TestMethod]
    public void HttpTargetsRemainUnchanged()
    {
        CollectionAssert.AreEqual(new[] { ("http", 80), ("https", 443), ("http", 81), ("http", 3000), ("http", 3001), ("http", 4000), ("http", 5000), ("http", 5001), ("http", 7000), ("http", 8000), ("http", 8001), ("http", 8008), ("http", 8080), ("http", 8081), ("http", 8088), ("http", 8880), ("http", 8888), ("http", 9000), ("http", 9090), ("https", 4443), ("https", 8443), ("https", 9443), ("https", 10443) }, HttpProbeTargets.All.ToArray());
    }

    [TestMethod]
    [DataRow("172.20.0.0", true)]
    [DataRow("172.23.255.255", true)]
    [DataRow("172.19.255.255", false)]
    [DataRow("172.24.0.0", false)]
    [DataRow("172.31.255.255", true)]
    [DataRow("172.30.255.255", false)]
    [DataRow("10.100.0.0", true)]
    [DataRow("10.103.255.255", true)]
    [DataRow("10.99.255.255", false)]
    [DataRow("10.104.0.0", false)]
    [DataRow("10.127.255.255", true)]
    [DataRow("10.126.255.255", false)]
    [DataRow("fdff::1", true)]
    [DataRow("fc00::1", false)]
    [DataRow("2001:db8::1", false)]
    [DataRow("::ffff:172.20.0.1", false)]
    [DataRow("invalid", false)]
    public void Dn42AddressBoundariesRemainUnchanged(string address, bool expected)
        => Assert.AreEqual(expected, Dn42AddressSpace.Contains(address));
}

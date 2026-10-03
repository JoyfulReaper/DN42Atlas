using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DN42Atlas.Tests;

[TestClass]
public sealed class ExclusionPolicyTests
{
    [TestMethod]
    public void ExactHostnameMatchingNormalizesCaseWhitespaceAndTrailingDot()
    {
        using var files = new TestFiles("# comment\nExample.DN42 # opt out\nexample.dn42\n");
        var policy = files.LoadPolicy();
        Assert.IsTrue(policy.IsHostExcluded(" EXAMPLE.dn42. ", out var rule));
        Assert.AreEqual("Example.DN42", rule);
        Assert.IsFalse(policy.IsHostExcluded("child.example.dn42"));
        Assert.IsFalse(policy.IsHostExcluded("other.dn42", out rule));
        Assert.IsNull(rule);
        Assert.HasCount(1, policy.HostRules);
    }

    [TestMethod]
    public void WildcardMatchesDescendantsButNotBareParentOrSimilarSuffix()
    {
        using var files = new TestFiles("*.example.dn42");
        var policy = files.LoadPolicy();
        Assert.IsTrue(policy.IsHostExcluded("FOO.example.dn42.", out var rule));
        Assert.AreEqual("*.example.dn42", rule);
        Assert.IsTrue(policy.IsHostExcluded("bar.foo.example.dn42"));
        Assert.IsFalse(policy.IsHostExcluded("example.dn42"));
        Assert.IsFalse(policy.IsHostExcluded("foo.notexample.dn42"));
    }

    [TestMethod]
    [DataRow("172.20.16.7/20", "172.20.16.0", true)]
    [DataRow("172.20.16.7/20", "172.20.31.255", true)]
    [DataRow("172.20.16.7/20", "172.20.32.0", false)]
    [DataRow("172.20.16.7/20", "172.20.15.255", false)]
    [DataRow("172.20.0.0/14", "::ffff:172.23.255.255", true)]
    [DataRow("172.20.0.0/14", "fd00::1", false)]
    [DataRow("fd42:1234:5678:8000::/49", "fd42:1234:5678:ffff::1", true)]
    [DataRow("fd42:1234:5678:8000::/49", "fd42:1234:5678:7fff::1", false)]
    [DataRow("fd42:1234:5678:8000::/49", "172.20.0.1", false)]
    [DataRow("172.20.0.1/32", "172.20.0.1", true)]
    [DataRow("172.20.0.1/32", "172.20.0.2", false)]
    [DataRow("fd42::1/128", "fd42::1", true)]
    [DataRow("fd42::1/128", "fd42::2", false)]
    [DataRow("0.0.0.0/0", "192.0.2.1", true)]
    [DataRow("::/0", "2001:db8::1", true)]
    public void CidrMatching(string prefix, string address, bool expected)
    {
        using var files = new TestFiles(prefixes: prefix);
        Assert.AreEqual(expected, files.LoadPolicy().IsAddressExcluded(address, out var rule));
        Assert.AreEqual(expected ? prefix : null, rule);
    }

    [TestMethod]
    [DataRow("172.20.0.0")]
    [DataRow("not-an-address/24")]
    [DataRow("172.20.0.0/nope")]
    [DataRow("172.20.0.0/-1")]
    [DataRow("172.20.0.0/33")]
    [DataRow("fd42::/129")]
    [DataRow("172.20.0.0/24/1")]
    public void MalformedCidrIsRejected(string prefix)
    {
        using var files = new TestFiles(prefixes: prefix);
        Assert.ThrowsExactly<FormatException>(() => files.LoadPolicy());
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void EitherMissingFileFailsClosed(bool missingHosts)
    {
        using var files = new TestFiles();
        var missingPath = missingHosts ? files.HostsPath : files.PrefixesPath;
        File.Delete(missingPath);
        var exception = Assert.ThrowsExactly<FileNotFoundException>(() => files.LoadPolicy());
        Assert.AreEqual(missingPath, exception.FileName);
    }
}

using DN42Atlas.Commands;
using DN42Atlas.Scanning;

namespace DN42Atlas.Tests;

[TestClass]
public sealed class RegistryPathTests
{
    [TestMethod]
    public async Task OverrideThenConfiguredCheckoutThenDeveloperFallback()
    {
        using var files = new TestFiles("blocked.dn42");
        var previousRoot = Environment.GetEnvironmentVariable("DN42ATLAS_REGISTRY_PATH");
        var previousCwd = Environment.CurrentDirectory;
        try
        {
            Environment.SetEnvironmentVariable("DN42ATLAS_REGISTRY_PATH", files.DirectoryPath);
            Assert.AreEqual("explicit-dns", ResolveCommand.GetRegistryDirectory("explicit-dns"));
            var dns = Path.Combine(files.DirectoryPath, "data", "dns");
            Directory.CreateDirectory(dns);
            File.WriteAllText(Path.Combine(dns, "blocked"), "domain: blocked.dn42\n");
            Assert.AreEqual(dns, ResolveCommand.GetRegistryDirectory());
            Environment.CurrentDirectory = files.DirectoryPath;
            var resolver = new RegistryResolver(files.LoadPolicy(), _ => throw new AssertFailedException("Excluded configured registry host must not resolve."));
            Assert.IsTrue(File.Exists(await new ResolveCommand(resolver).ExecuteAsync()));
            Environment.SetEnvironmentVariable("DN42ATLAS_REGISTRY_PATH", "relative");
            Assert.ThrowsExactly<InvalidOperationException>(() => ResolveCommand.GetRegistryDirectory());
            Environment.SetEnvironmentVariable("DN42ATLAS_REGISTRY_PATH", " ");
            Assert.ThrowsExactly<InvalidOperationException>(() => ResolveCommand.GetRegistryDirectory());
            Assert.AreEqual("override", ResolveCommand.GetRegistryDirectory("override"));
            Assert.ThrowsExactly<ArgumentException>(() => ResolveCommand.GetRegistryDirectory(" "));
            Environment.SetEnvironmentVariable("DN42ATLAS_REGISTRY_PATH", null);
            Assert.AreEqual(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "dn42-registry", "data", "dns"), ResolveCommand.GetRegistryDirectory());
        }
        finally
        {
            Environment.SetEnvironmentVariable("DN42ATLAS_REGISTRY_PATH", previousRoot);
            Environment.CurrentDirectory = previousCwd;
        }
    }
}

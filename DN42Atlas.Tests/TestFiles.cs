using DN42Atlas.Policy;

namespace DN42Atlas.Tests;

internal sealed class TestFiles : IDisposable
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "DN42Atlas-tests", Guid.NewGuid().ToString("N"));
    public string HostsPath => Path.Combine(DirectoryPath, "excluded-hosts.txt");
    public string PrefixesPath => Path.Combine(DirectoryPath, "excluded-prefixes.txt");
    public string RuntimePath => Path.Combine(DirectoryPath, "runtime-exclusions.json");

    public TestFiles(string hosts = "", string prefixes = "")
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(HostsPath, hosts);
        File.WriteAllText(PrefixesPath, prefixes);
    }

    public ExclusionPolicy LoadPolicy(string? runtimePath = null) =>
        ExclusionPolicy.Load(HostsPath, PrefixesPath, runtimePath);

    public string Write(string filename, string content)
    {
        var path = Path.Combine(DirectoryPath, filename);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
}

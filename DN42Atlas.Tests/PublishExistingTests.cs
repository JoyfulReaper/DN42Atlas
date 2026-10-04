using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using DN42Atlas.Commands;
using DN42Atlas.Policy;
using DN42Atlas.Publishing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DN42Atlas.Tests;

[TestClass]
public sealed class PublishExistingTests
{
    [TestMethod]
    public async Task SelectedScanBootstrapsStateAndPublishesSameFilteredModelWithoutChangingRawBytes()
    {
        using var files = new TestFiles("manual.dn42", "172.20.1.0/24");
        InstallManualPolicy(files);
        var raw = files.Write("explicit old scan.json", """
            {"GeneratedAt":"2026-10-03T12:50:26Z","ExcludedByHostname":2,"Results":[
              {"Domain":"manual.dn42","Scheme":"http","Port":80},
              {"Domain":"runtime.dn42","Scheme":"http","Port":80},
              {"Domain":"good.dn42","Scheme":"http","Port":80,"ProbeAddresses":["fd42:5678::1"],
               "Title":"manual.dn42 runtime.dn42 172.20.1.4 fd42:1234::1",
               "DiscoveredLinks":["http://runtime.dn42/","https://good.dn42/"],
               "Dn42Mentions":["manual.dn42","runtime.dn42","good.dn42"]}]}
            """);
        var original = await File.ReadAllBytesAsync(raw);
        // This newer unrelated scan must never be auto-selected.
        files.Write("web-probe-20990101-000000.json", "not a valid scan");
        var runtime = Path.Combine(files.DirectoryPath, "runtime.json");
        await RuntimeExclusionBundle.WriteAtomicAsync(runtime, ["runtime.dn42"], ["fd42:1234::/48"]);
        var result = await RunCli(files, runtime, "publish-existing", Path.GetFileName(raw));
        Assert.AreEqual(0, result.ExitCode, result.Error);
        Assert.Contains($"Published existing scan: {raw}", result.Output);
        var published = Path.Combine(files.DirectoryPath, "published");
        var json = await File.ReadAllTextAsync(Path.Combine(published, "latest.json"));
        var html = await File.ReadAllTextAsync(Path.Combine(published, "index.html"));
        var model = JsonNode.Parse(json)!;
        Assert.HasCount(1, model["Results"]!.AsArray());
        Assert.AreEqual("good.dn42", model["Results"]![0]!["Domain"]!.GetValue<string>());
        Assert.AreEqual(4, model["ExcludedByHostname"]!.GetValue<int>());
        Assert.AreEqual("2026-10-03T12:50:26Z", model["GeneratedAt"]!.GetValue<string>());
        foreach (var excluded in new[] { "manual.dn42", "runtime.dn42", "172.20.1.4", "fd42:1234" })
        {
            Assert.IsFalse(json.Contains(excluded, StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(html.Contains(excluded, StringComparison.OrdinalIgnoreCase));
        }
        var begin = html.IndexOf("const scan = ", StringComparison.Ordinal) + "const scan = ".Length;
        var end = html.IndexOf(";", begin);
        Assert.IsTrue(JsonNode.DeepEquals(model, JsonNode.Parse(html[begin..end])));
        var state = await PublicationState.LoadAsync(Path.Combine(files.DirectoryPath, "private", "state.json"), published);
        Assert.AreEqual(raw, state.RawScanPath);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(original)), state.RawScanSha256);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(raw));
        Assert.IsFalse(File.Exists(Path.Combine(published, Path.GetFileName(raw))));
        Assert.IsFalse(File.Exists(Path.Combine(files.DirectoryPath, "domain-resolution.json")));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExactlyOnePathIsRequiredEvenWithoutPolicy(bool extraArgument)
    {
        using var files = new TestFiles();
        string[] arguments = extraArgument ? ["publish-existing", "one.json", "two.json"] : ["publish-existing"];
        var result = await RunCli(files, null, arguments);
        Assert.AreEqual(2, result.ExitCode);
        Assert.AreEqual("Usage: dn42atlas publish-existing <web-probe.json>" + Environment.NewLine, result.Output);
        Assert.AreEqual("", result.Error);
        Assert.IsFalse(Directory.Exists(Path.Combine(files.DirectoryPath, "published")));
    }

    [TestMethod]
    [DataRow("missing-file")]
    [DataRow("missing-runtime")]
    [DataRow("malformed-runtime")]
    [DataRow("missing-manual")]
    public async Task InvalidInputsPreserveCurrentPublicArtifactsAndState(string failure)
    {
        using var files = new TestFiles();
        InstallManualPolicy(files);
        var raw = files.Write("selected.json", "{\"Results\":[]}");
        var published = Path.Combine(files.DirectoryPath, "published");
        var statePath = Path.Combine(files.DirectoryPath, "private", "state.json");
        await ArtifactPublisher.PublishAsync(raw, published, files.LoadPolicy(), statePath);
        var previousJson = await File.ReadAllBytesAsync(Path.Combine(published, "latest.json"));
        var previousHtml = await File.ReadAllBytesAsync(Path.Combine(published, "index.html"));
        var previousState = await File.ReadAllBytesAsync(statePath);
        files.Write("web-probe-20990101-000000.json", "{\"Results\":[],\"newer\":true}");
        string? runtime = null;
        if (failure == "missing-file") raw = Path.Combine(files.DirectoryPath, "missing.json");
        if (failure is "missing-runtime" or "malformed-runtime") runtime = Path.Combine(files.DirectoryPath, "runtime.json");
        if (failure == "malformed-runtime") await File.WriteAllTextAsync(runtime!, "not JSON");
        if (failure == "missing-manual") File.Delete(Path.Combine(files.DirectoryPath, "config", "excluded-hosts.txt"));
        var result = await RunCli(files, runtime, "publish-existing", raw);
        Assert.AreEqual(1, result.ExitCode);
        CollectionAssert.AreEqual(previousJson, await File.ReadAllBytesAsync(Path.Combine(published, "latest.json")));
        CollectionAssert.AreEqual(previousHtml, await File.ReadAllBytesAsync(Path.Combine(published, "index.html")));
        CollectionAssert.AreEqual(previousState, await File.ReadAllBytesAsync(statePath));
        Assert.IsFalse(File.Exists(Path.Combine(files.DirectoryPath, "domain-resolution.json")));
    }

    private static void InstallManualPolicy(TestFiles files)
    {
        var config = Path.Combine(files.DirectoryPath, "config");
        Directory.CreateDirectory(config);
        File.Copy(files.HostsPath, Path.Combine(config, "excluded-hosts.txt"));
        File.Copy(files.PrefixesPath, Path.Combine(config, "excluded-prefixes.txt"));
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunCli(TestFiles files, string? runtime, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = files.DirectoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(typeof(PublishExistingCommand).Assembly.Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["DN42ATLAS_PUBLICATION_STATE_PATH"] = Path.Combine(files.DirectoryPath, "private", "state.json");
        start.Environment.Remove("DN42ATLAS_RUNTIME_EXCLUSIONS_PATH");
        if (runtime != null) start.Environment["DN42ATLAS_RUNTIME_EXCLUSIONS_PATH"] = runtime;
        start.Environment["DN42ATLAS_REGISTRY_PATH"] = Path.Combine(files.DirectoryPath, "nonexistent-registry");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await output, await error);
    }
}

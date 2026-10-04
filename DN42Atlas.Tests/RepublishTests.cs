using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DN42Atlas.Commands;
using DN42Atlas.Policy;
using DN42Atlas.Publishing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DN42Atlas.Tests;

[TestClass]
public sealed class RepublishTests
{
    private const string Raw = """
        {"GeneratedAt":"2026-10-03T12:50:26Z","ExcludedByHostname":2,"Results":[
          {"Domain":"blocked.dn42","Scheme":"http","Port":80},
          {"Domain":"good.dn42","Scheme":"http","Port":80,
           "Title":"blocked.dn42 172.20.16.7 [fd42:1234::1] </script><script>alert(1)</script>",
           "DiscoveredLinks":["http://blocked.dn42/","http://172.20.16.7/","https://good.dn42/"],
           "Dn42Mentions":["blocked.dn42","good.dn42"]}]}
        """;

    [TestMethod]
    public async Task BothPublicArtifactsShareOneFilteredModelAndRawBytesRemainUnchanged()
    {
        using var files = new TestFiles("blocked.dn42", "172.20.16.0/20\nfd42:1234::/48");
        var raw = files.Write("raw.json", Raw);
        var original = await File.ReadAllBytesAsync(raw);
        var published = Path.Combine(files.DirectoryPath, "published");
        var statePath = Path.Combine(files.DirectoryPath, "private", "state.json");
        await ArtifactPublisher.PublishAsync(raw, published, files.LoadPolicy(), statePath);
        var json = await File.ReadAllTextAsync(Path.Combine(published, "latest.json"));
        var html = await File.ReadAllTextAsync(Path.Combine(published, "index.html"));
        foreach (var excluded in new[] { "blocked.dn42", "172.20.16.7", "fd42:1234" })
        {
            Assert.IsFalse(json.Contains(excluded, StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(html.Contains(excluded, StringComparison.OrdinalIgnoreCase));
        }
        var model = JsonNode.Parse(json)!;
        Assert.AreEqual(3, model["ExcludedByHostname"]!.GetValue<int>());
        Assert.HasCount(1, model["Results"]!.AsArray());
        Assert.Contains("[excluded]", model["Results"]![0]!["Title"]!.GetValue<string>());
        Assert.IsFalse(html.Contains("<script>alert(1)</script>", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(JsonNode.DeepEquals(model, EmbeddedScan(html)));
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(raw));
        var state = await PublicationState.LoadAsync(statePath, published);
        Assert.AreEqual(1, state.Version);
        Assert.AreEqual(Path.GetFullPath(raw), state.RawScanPath);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(original)), state.RawScanSha256);
        Assert.AreEqual(TimeSpan.Zero, state.PublishedAtUtc.Offset);
        Assert.IsFalse((await File.ReadAllTextAsync(statePath)).Contains("Results", StringComparison.Ordinal));
        Assert.IsFalse(File.Exists(Path.Combine(published, "raw.json")));
    }

    [TestMethod]
    public async Task RepublishUsesRecordedScanAndNewRuntimeExclusionsWithoutChangingScanTime()
    {
        using var files = new TestFiles();
        var raw = files.Write("raw.json", Raw);
        var original = await File.ReadAllBytesAsync(raw);
        var published = Path.Combine(files.DirectoryPath, "published");
        var statePath = Path.Combine(files.DirectoryPath, "state.json");
        await ArtifactPublisher.PublishAsync(raw, published, files.LoadPolicy(), statePath);
        // A newer file must never be selected instead of the state-recorded source.
        files.Write("web-probe-20990101-000000.json", "invalid decoy");
        var runtime = Path.Combine(files.DirectoryPath, "runtime.json");
        await RuntimeExclusionBundle.WriteAtomicAsync(runtime, ["blocked.dn42"], ["172.20.16.0/20", "fd42:1234::/48"]);
        var policy = ExclusionPolicy.Load(files.HostsPath, files.PrefixesPath, runtime);
        Assert.AreEqual(0, await new RepublishCommand(policy, published, statePath).ExecuteAsync());
        var model = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(published, "latest.json")))!;
        var html = await File.ReadAllTextAsync(Path.Combine(published, "index.html"));
        Assert.IsTrue(JsonNode.DeepEquals(model, EmbeddedScan(html)));
        Assert.AreEqual("2026-10-03T12:50:26Z", model["GeneratedAt"]!.GetValue<string>());
        Assert.Contains("2026-10-03 12:50:26 UTC", html);
        Assert.AreEqual(3, model["ExcludedByHostname"]!.GetValue<int>());
        Assert.IsFalse(html.Contains("blocked.dn42", StringComparison.OrdinalIgnoreCase));
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(raw));
        Assert.AreEqual(raw, (await PublicationState.LoadAsync(statePath, published)).RawScanPath);
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("malformed")]
    [DataRow("unsupported")]
    [DataRow("missing-raw")]
    [DataRow("hash-mismatch")]
    [DataRow("relative-raw")]
    [DataRow("missing-hash")]
    [DataRow("bad-generation")]
    public async Task InvalidStateOrFailedRegenerationPreservesPublicFiles(string failure)
    {
        using var files = new TestFiles();
        var raw = files.Write("raw.json", Raw);
        var published = Path.Combine(files.DirectoryPath, "published");
        var statePath = Path.Combine(files.DirectoryPath, "state.json");
        await ArtifactPublisher.PublishAsync(raw, published, files.LoadPolicy(), statePath);
        var json = await File.ReadAllBytesAsync(Path.Combine(published, "latest.json"));
        var html = await File.ReadAllBytesAsync(Path.Combine(published, "index.html"));
        var state = JsonNode.Parse(await File.ReadAllTextAsync(statePath))!;
        switch (failure)
        {
            case "missing": File.Delete(statePath); break;
            case "malformed": await File.WriteAllTextAsync(statePath, "not JSON"); break;
            case "unsupported": state["version"] = 99; break;
            case "missing-raw": File.Delete(raw); break;
            case "hash-mismatch": await File.WriteAllTextAsync(raw, "replaced raw scan"); break;
            case "relative-raw": state["rawScanPath"] = "raw.json"; break;
            case "missing-hash": state.AsObject().Remove("rawScanSha256"); break;
            case "bad-generation":
                await File.WriteAllTextAsync(raw, "{\"Results\":null}");
                state["rawScanSha256"] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(raw)));
                break;
        }
        if (failure is not ("missing" or "malformed"))
            await File.WriteAllTextAsync(statePath, state.ToJsonString());
        var previousState = File.Exists(statePath) ? await File.ReadAllBytesAsync(statePath) : null;
        try
        {
            await new RepublishCommand(files.LoadPolicy(), published, statePath).ExecuteAsync();
            Assert.Fail("Republish must fail closed.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException) { }
        CollectionAssert.AreEqual(json, await File.ReadAllBytesAsync(Path.Combine(published, "latest.json")));
        CollectionAssert.AreEqual(html, await File.ReadAllBytesAsync(Path.Combine(published, "index.html")));
        if (previousState != null) CollectionAssert.AreEqual(previousState, await File.ReadAllBytesAsync(statePath));
        else Assert.IsFalse(File.Exists(statePath));
        Assert.HasCount(5, Directory.GetFiles(published));
    }

    [TestMethod]
    public async Task PublicReplacementFailureDoesNotAdvancePrivateState()
    {
        using var files = new TestFiles();
        var raw = files.Write("raw.json", Raw);
        var published = Path.Combine(files.DirectoryPath, "published");
        var statePath = Path.Combine(files.DirectoryPath, "state.json");
        await ArtifactPublisher.PublishAsync(raw, published, files.LoadPolicy(), statePath);
        var state = await File.ReadAllBytesAsync(statePath);
        File.Delete(Path.Combine(published, "index.html"));
        Directory.CreateDirectory(Path.Combine(published, "index.html"));
        try
        {
            await ArtifactPublisher.PublishAsync(raw, published, files.LoadPolicy(), statePath);
            Assert.Fail("Replacing a directory with a file must fail.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        CollectionAssert.AreEqual(state, await File.ReadAllBytesAsync(statePath));
    }

    [TestMethod]
    public async Task StateInsideWebRootIsRejectedBeforePublication()
    {
        using var files = new TestFiles();
        var raw = files.Write("raw.json", Raw);
        var published = Path.Combine(files.DirectoryPath, "published");
        await Assert.ThrowsAsync<InvalidDataException>(() => ArtifactPublisher.PublishAsync(raw, published, files.LoadPolicy(), Path.Combine(published, "state.json")));
        Assert.IsFalse(Directory.Exists(published));
    }

    [TestMethod]
    [DataRow("valid")]
    [DataRow("missing")]
    [DataRow("malformed")]
    public async Task CliRepublishLoadsRuntimePolicyAndRequiresNoRegistryOrProbes(string bundle)
    {
        using var files = new TestFiles();
        var raw = files.Write("raw.json", Raw);
        var published = Path.Combine(files.DirectoryPath, "published");
        var statePath = Path.Combine(files.DirectoryPath, "state.json");
        await ArtifactPublisher.PublishAsync(raw, published, files.LoadPolicy(), statePath);
        var beforeJson = await File.ReadAllBytesAsync(Path.Combine(published, "latest.json"));
        var beforeHtml = await File.ReadAllBytesAsync(Path.Combine(published, "index.html"));
        var config = Path.Combine(files.DirectoryPath, "config");
        Directory.CreateDirectory(config);
        File.Copy(files.HostsPath, Path.Combine(config, "excluded-hosts.txt"));
        File.Copy(files.PrefixesPath, Path.Combine(config, "excluded-prefixes.txt"));
        var runtime = Path.Combine(files.DirectoryPath, "runtime.json");
        if (bundle == "valid") await RuntimeExclusionBundle.WriteAtomicAsync(runtime, ["blocked.dn42"], []);
        if (bundle == "malformed") await File.WriteAllTextAsync(runtime, "malformed");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = files.DirectoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(typeof(RepublishCommand).Assembly.Location);
        start.ArgumentList.Add("republish");
        start.Environment["DN42ATLAS_PUBLICATION_STATE_PATH"] = statePath;
        start.Environment["DN42ATLAS_RUNTIME_EXCLUSIONS_PATH"] = runtime;
        start.Environment["DN42ATLAS_REGISTRY_PATH"] = Path.Combine(files.DirectoryPath, "nonexistent-registry");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        if (bundle == "valid")
        {
            Assert.AreEqual(0, process.ExitCode, await error);
            Assert.Contains("Republished current scan:", await output);
            Assert.IsFalse((await File.ReadAllTextAsync(Path.Combine(published, "latest.json"))).Contains("blocked.dn42", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse((await File.ReadAllTextAsync(Path.Combine(published, "index.html"))).Contains("blocked.dn42", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            Assert.AreNotEqual(0, process.ExitCode);
            CollectionAssert.AreEqual(beforeJson, await File.ReadAllBytesAsync(Path.Combine(published, "latest.json")));
            CollectionAssert.AreEqual(beforeHtml, await File.ReadAllBytesAsync(Path.Combine(published, "index.html")));
        }
    }

    private static JsonNode EmbeddedScan(string html)
    {
        var begin = html.IndexOf("const scan = ", StringComparison.Ordinal) + "const scan = ".Length;
        var end = html.IndexOf(";", begin);
        return JsonNode.Parse(html[begin..end])!;
    }
}

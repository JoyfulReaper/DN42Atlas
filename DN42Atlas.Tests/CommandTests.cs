using System.Diagnostics;
using System.Net;
using System.Text.Json;
using DN42Atlas.Commands;
using DN42Atlas.Probing;
using DN42Atlas.Registry;
using DN42Atlas.Scanning;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DN42Atlas.Tests;

[TestClass]
public sealed class CommandTests
{
    [TestMethod]
    [DataRow("report", "Usage: report <web-probe.json>")]
    [DataRow("report missing.json", "File not found: missing.json")]
    [DataRow("web-scan", "Resolution file not found: domain-resolution.json.bk2")]
    [DataRow("web-scan missing.json", "Resolution file not found: missing.json")]
    public async Task CliMissingArgumentsAndFilesKeepMessagesAndFail(string arguments, string expected)
    {
        using var files = new TestFiles();
        Directory.CreateDirectory(Path.Combine(files.DirectoryPath, "config"));
        File.Copy(files.HostsPath, Path.Combine(files.DirectoryPath, "config", "excluded-hosts.txt"));
        File.Copy(files.PrefixesPath, Path.Combine(files.DirectoryPath, "config", "excluded-prefixes.txt"));
        var result = await RunCliAsync(files.DirectoryPath, arguments);
        Assert.AreNotEqual(0, result.ExitCode);
        Assert.AreEqual(expected + Environment.NewLine, result.Output);
        Assert.AreEqual("", result.Error);
    }

    [TestMethod]
    public async Task CliWebScanAndReportGenerateCompatibleFilesWithoutNetworkAccess()
    {
        using var files = new TestFiles();
        var config = Path.Combine(files.DirectoryPath, "config");
        Directory.CreateDirectory(config);
        File.Copy(files.HostsPath, Path.Combine(config, "excluded-hosts.txt"));
        File.Copy(files.PrefixesPath, Path.Combine(config, "excluded-prefixes.txt"));
        files.Write("domain-resolution.json.bk2", "[]");
        var scan = await RunCliAsync(files.DirectoryPath, "web-scan");
        Assert.AreEqual(0, scan.ExitCode);
        Assert.Contains("Scan complete.", scan.Output);
        var jsonPath = Directory.GetFiles(Path.Combine(files.DirectoryPath, "results"), "web-probe-*.json").Single();
        var report = await RunCliAsync(files.DirectoryPath, $"report results/{Path.GetFileName(jsonPath)}");
        Assert.AreEqual(0, report.ExitCode);
        Assert.Contains("Atlas viewer written to:", report.Output);
        Assert.IsTrue(File.Exists(Path.ChangeExtension(jsonPath, ".html")));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CliFailsClosedBeforeDispatchIfEitherPolicyFileIsMissing(bool missingHosts)
    {
        using var files = new TestFiles();
        Directory.CreateDirectory(Path.Combine(files.DirectoryPath, "config"));
        var present = missingHosts ? "excluded-prefixes.txt" : "excluded-hosts.txt";
        File.WriteAllText(Path.Combine(files.DirectoryPath, "config", present), "");
        var result = await RunCliAsync(files.DirectoryPath, "report");
        Assert.AreNotEqual(0, result.ExitCode);
        Assert.AreEqual("", result.Output);
        Assert.Contains("Required exclusion file not found:", result.Error);
    }

    [TestMethod]
    [DataRow("--help", 0)]
    [DataRow("-h", 0)]
    [DataRow("help", 0)]
    [DataRow("unknown", 2)]
    [DataRow("--unknown", 2)]
    public async Task HelpAndUnknownCommandsDoNotLoadPolicyOrScan(string arguments, int exitCode)
    {
        using var files = new TestFiles();
        var result = await RunCliAsync(files.DirectoryPath, arguments);
        Assert.AreEqual(exitCode, result.ExitCode);
        Assert.Contains("Usage: dn42atlas [command]", result.Output);
        foreach (var command in new[] { "resolve", "web-scan", "report", "probe-test", "run" })
            Assert.Contains(command, result.Output);
        Assert.AreEqual("", result.Error);
        Assert.IsFalse(File.Exists(Path.Combine(files.DirectoryPath, "domain-resolution.json")));
        Assert.IsFalse(Directory.Exists(Path.Combine(files.DirectoryPath, "results")));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("resolve")]
    [DataRow("run")]
    public async Task ResolutionAndRunCommandsRequireExclusionPolicy(string arguments)
    {
        using var files = new TestFiles();
        var result = await RunCliAsync(files.DirectoryPath, arguments);
        Assert.AreEqual(1, result.ExitCode);
        Assert.AreEqual("", result.Output);
        Assert.Contains("Required exclusion file not found:", result.Error);
    }

    [TestMethod]
    public async Task RunUsesFreshResolutionAndGeneratesJsonAndHtml()
    {
        using var files = new TestFiles("blocked.dn42");
        var registry = Path.Combine(files.DirectoryPath, "dns");
        Directory.CreateDirectory(registry);
        File.WriteAllText(Path.Combine(registry, "good"), "domain: fresh.dn42\n");
        File.WriteAllText(Path.Combine(registry, "blocked"), "domain: blocked.dn42\n");
        files.Write("domain-resolution.json.bk2", "invalid stale input");
        files.Write("domain-resolution.json", "invalid old output");
        var policy = files.LoadPolicy();
        var queries = new List<string>();
        var probes = new List<string>();
        var resolver = new RegistryResolver(policy, hostname =>
        {
            queries.Add(hostname);
            return Task.FromResult(new[] { IPAddress.Parse("fd42::1") });
        });
        (string, int)[] targets = [("http", 80)];
        var scanner = new WebScanner(policy, targets, (domain, scheme, port, _) =>
        {
            probes.Add(domain);
            return Task.FromResult(new HttpProbeResult { Domain = domain, Scheme = scheme, Port = port });
        });
        var previousDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = files.DirectoryPath;
            var run = new RunCommand(new ResolveCommand(resolver, registry), new WebScanCommand(scanner, targets, policy), policy);
            Assert.AreEqual(0, await run.ExecuteAsync());
        }
        finally { Environment.CurrentDirectory = previousDirectory; }
        CollectionAssert.AreEqual(new[] { "fresh.dn42" }, queries);
        CollectionAssert.AreEqual(new[] { "fresh.dn42" }, probes);
        var json = Directory.GetFiles(Path.Combine(files.DirectoryPath, "results"), "*.json").Single();
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(json));
        Assert.AreEqual(Path.Combine(files.DirectoryPath, "domain-resolution.json"),
            document.RootElement.GetProperty("ResolutionSource").GetString());
        Assert.IsTrue(File.Exists(Path.ChangeExtension(json, ".html")));
        var published = Path.Combine(files.DirectoryPath, "published");
        Assert.AreEqual(await File.ReadAllTextAsync(json), await File.ReadAllTextAsync(Path.Combine(published, "latest.json")));
        Assert.AreEqual(await File.ReadAllTextAsync(Path.ChangeExtension(json, ".html")),
            await File.ReadAllTextAsync(Path.Combine(published, "index.html")));
        Assert.IsTrue(File.Exists(Path.Combine(published, "about.html")));
        Assert.IsTrue(File.Exists(Path.Combine(published, "opt-out.html")));
        Assert.IsTrue(File.Exists(Path.Combine(published, "robots.txt")));
    }

    [TestMethod]
    public async Task RunStopsWhenResolutionFailsInsteadOfScanningStaleOutput()
    {
        using var files = new TestFiles();
        files.Write("domain-resolution.json", "[]");
        var policy = files.LoadPolicy();
        var scanner = new WebScanner(policy, HttpProbeTargets.All, (_, _, _, _) =>
            throw new AssertFailedException("Must not probe after resolution failure."));
        var previousDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = files.DirectoryPath;
            var run = new RunCommand(new ResolveCommand(new RegistryResolver(policy), Path.Combine(files.DirectoryPath, "missing")),
                new WebScanCommand(scanner, HttpProbeTargets.All, policy), policy);
            await Assert.ThrowsAsync<DirectoryNotFoundException>(() => run.ExecuteAsync());
            Assert.IsFalse(Directory.Exists(Path.Combine(files.DirectoryPath, "results")));
        }
        finally { Environment.CurrentDirectory = previousDirectory; }
    }

    [TestMethod]
    public async Task RunLeavesPreviousPublicationIntactWhenScanGenerationFails()
    {
        using var files = new TestFiles();
        var registry = Path.Combine(files.DirectoryPath, "dns");
        Directory.CreateDirectory(registry);
        File.WriteAllText(Path.Combine(registry, "good"), "domain: good.dn42\n");
        var published = Path.Combine(files.DirectoryPath, "published");
        Directory.CreateDirectory(published);
        File.WriteAllText(Path.Combine(published, "index.html"), "previous HTML");
        File.WriteAllText(Path.Combine(published, "latest.json"), "previous JSON");
        var policy = files.LoadPolicy();
        var resolver = new RegistryResolver(policy, _ => Task.FromResult(new[] { IPAddress.Parse("fd42::1") }));
        (string, int)[] targets = [("http", 80)];
        var scanner = new WebScanner(policy, targets, (_, _, _, _) =>
            Task.FromException<HttpProbeResult>(new InvalidDataException("Simulated generation failure.")));
        var previousDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = files.DirectoryPath;
            var run = new RunCommand(new ResolveCommand(resolver, registry), new WebScanCommand(scanner, targets, policy), policy);
            await Assert.ThrowsAsync<InvalidDataException>(() => run.ExecuteAsync());
        }
        finally { Environment.CurrentDirectory = previousDirectory; }
        Assert.AreEqual("previous HTML", await File.ReadAllTextAsync(Path.Combine(published, "index.html")));
        Assert.AreEqual("previous JSON", await File.ReadAllTextAsync(Path.Combine(published, "latest.json")));
        Assert.HasCount(2, Directory.GetFiles(published));
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunCliAsync(string directory, string arguments)
    {
        var application = typeof(ReportCommand).Assembly.Location;
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(application);
        foreach (var argument in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await output, await error);
    }
}

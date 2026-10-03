using System.Diagnostics;
using DN42Atlas.Commands;
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
    public async Task CliMissingArgumentsAndFilesKeepMessagesAndSuccessExit(string arguments, string expected)
    {
        using var files = new TestFiles();
        Directory.CreateDirectory(Path.Combine(files.DirectoryPath, "config"));
        File.Copy(files.HostsPath, Path.Combine(files.DirectoryPath, "config", "excluded-hosts.txt"));
        File.Copy(files.PrefixesPath, Path.Combine(files.DirectoryPath, "config", "excluded-prefixes.txt"));
        var result = await RunCliAsync(files.DirectoryPath, arguments);
        Assert.AreEqual(0, result.ExitCode);
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
        foreach (var argument in arguments.Split(' '))
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

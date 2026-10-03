using System.Text.Json;
using DN42Atlas.Reporting;
using DN42Atlas.Scanning;
using DN42Atlas.Policy;

namespace DN42Atlas.Commands;

public sealed class WebScanCommand(WebScanner scanner, IReadOnlyList<(string, int)> probeTargets, ExclusionPolicy exclusionPolicy)
{
    public async Task ExecuteAsync(string[] args)
    {
        var resolutionPath =
            args.Length > 1
                ? args[1]
                : "domain-resolution.json.bk2";

        if (!File.Exists(resolutionPath))
        {
            Console.WriteLine(
                $"Resolution file not found: {resolutionPath}");

            return;
        }

        Console.WriteLine(
            $"Loading DNS results from: {resolutionPath}");

        var scan = await scanner.ScanAsync(resolutionPath);
        var startedAt = scan.StartedAt;
        var finishedAt = scan.FinishedAt;
        var skippedExternal = scan.SkippedExternal;
        var excludedByHostname = scan.ExcludedByHostname;
        var excludedByPrefix = scan.ExcludedByPrefix;
        var reachable = scan.Reachable;
        var orderedResults = scan.Results;
        var timestamp =
            startedAt.ToString(
                "yyyyMMdd-HHmmss");

        var resultsDirectory =
            Path.Combine(
                Environment.CurrentDirectory,
                "results");

        Directory.CreateDirectory(
            resultsDirectory);

        var webOutputPath =
            Path.Combine(
                resultsDirectory,
                $"web-probe-{timestamp}.json");

        var publicScan =
            JsonSerializer.SerializeToNode(
                new
                {
                    GeneratedAt =
                        finishedAt,

                    StartedAt =
                        startedAt,

                    FinishedAt =
                        finishedAt,

                    DurationSeconds =
                        (finishedAt - startedAt)
                        .TotalSeconds,

                    ResolutionSource =
                        resolutionPath,

                    ResolvedDomainsProbed =
                        scan.DomainCount,

                    SkippedExternalOrMixedDomains =
                        skippedExternal,

                    ExcludedByHostname =
                        excludedByHostname,

                    ExcludedByPrefix =
                        excludedByPrefix,

                    ProbeTargetCount =
                        scan.ProbeTargetCount,

                    ReachableOrigins =
                        reachable,

                    ProbeTargets =
                        probeTargets.Select(
                            x => new
                            {
                                Scheme =
                                    x.Item1,

                                Port =
                                    x.Item2
                            }),

                    //
                    // Serialize the actual result objects
                    // directly so new probe properties
                    // automatically appear in JSON.
                    //
                    Results =
                        orderedResults
                })!;
        new PublicScanPolicy(exclusionPolicy).Apply(publicScan);
        var webJson = publicScan.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        await File.WriteAllTextAsync(
            webOutputPath,
            webJson);

        //
        // Automatically make a browseable
        // self-contained HTML Atlas too.
        //
        var viewerOutputPath =
            Path.ChangeExtension(
                webOutputPath,
                ".html");

        await AtlasReportGenerator.GenerateAsync(
            webOutputPath,
            viewerOutputPath,
            exclusionPolicy);

        Console.WriteLine();
        Console.WriteLine(
            "==============================");

        Console.WriteLine(
            "Scan complete.");

        Console.WriteLine(
            "==============================");

        Console.WriteLine(
            $"Domains probed:    {scan.DomainCount}");

        Console.WriteLine(
            $"Origins reachable: {reachable}");

        Console.WriteLine(
            $"Total probes:      {scan.ProbeTargetCount}");

        Console.WriteLine(
            $"Duration:          " +
            $"{finishedAt - startedAt}");

        Console.WriteLine(
            $"Results:           {webOutputPath}");

        Console.WriteLine(
            $"Viewer:            {viewerOutputPath}");

        Console.WriteLine(
            $"Excluded by host: {excludedByHostname}");

        Console.WriteLine(
            $"Excluded by IP:   {excludedByPrefix}");

        return;
    }
}

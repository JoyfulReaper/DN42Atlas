using System.Collections.Concurrent;
using System.Text.Json;
using DN42Atlas.Policy;
using DN42Atlas.Probing;
using DN42Atlas.Registry;
using DN42Atlas.Networking;

namespace DN42Atlas.Scanning;

public sealed class WebScanner
{
    private readonly Func<string, string, int, CancellationToken, Task<HttpProbeResult>>? probeAsync;

    public WebScanner(ExclusionPolicy exclusionPolicy, IReadOnlyList<(string, int)> probeTargets,
        Func<string, string, int, CancellationToken, Task<HttpProbeResult>>? probeAsync = null)
    {
        this.exclusionPolicy = exclusionPolicy;
        this.probeTargets = probeTargets;
        this.probeAsync = probeAsync;
    }

    private readonly ExclusionPolicy exclusionPolicy;
    private readonly IReadOnlyList<(string, int)> probeTargets;

    public async Task<WebScanResult> ScanAsync(string resolutionPath)
    {
        using var resolutionDocument =
            JsonDocument.Parse(
                await File.ReadAllTextAsync(
                    resolutionPath));

        var domains =
            new List<string>();

        var skippedExternal =
            new List<string>();

        var approvedAddresses = new Dictionary<string, IReadOnlyList<System.Net.IPAddress>>(StringComparer.OrdinalIgnoreCase);
        var rejectedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var excludedPrefixDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var excludedByHostname = 0;
        var excludedByPrefix = 0;

        foreach (var item in
            resolutionDocument
                .RootElement
                .EnumerateArray())
        {
            var domain =
                item.GetProperty(
                        "Domain")
                    .GetString();

            var status =
                item.GetProperty(
                        "Status")
                    .GetString();

            if (!ProbeDestination.IsDn42Hostname(domain))
            {
                continue;
            }

            if (exclusionPolicy.IsHostExcluded(
                domain,
                out var hostExclusionRule))
            {
                excludedByHostname++;

                Console.WriteLine(
                    $"[exclude] {domain} " +
                    $"matched host rule " +
                    $"{hostExclusionRule}");

                continue;
            }

            if (status != "Resolved")
                continue;

            var addresses =
                item.GetProperty(
                        "Addresses")
                    .EnumerateArray()
                    .Select(
                        x => x.GetString())
                    .Where(
                        x =>
                            !string.IsNullOrWhiteSpace(
                                x))
                    .Cast<string>()
                    .ToList();

            if (addresses.Count == 0)
            {
                rejectedDomains.Add(domain);
                continue;
            }

            string? matchedPrefixRule = null;
            string? matchedExcludedAddress = null;

            foreach (var address in addresses)
            {
                if (!exclusionPolicy.IsAddressExcluded(
                    address,
                    out var rule))
                {
                    continue;
                }

                matchedExcludedAddress =
                    address;

                matchedPrefixRule =
                    rule;

                break;
            }

            if (matchedExcludedAddress != null)
            {
                excludedByPrefix++;
                rejectedDomains.Add(domain);
                excludedPrefixDomains.Add(domain);

                Console.WriteLine(
                    $"[exclude] {domain} " +
                    $"resolved to {matchedExcludedAddress}, " +
                    $"matched prefix rule " +
                    $"{matchedPrefixRule}");

                continue;
            }

            //
            // Safety guard:
            //
            // Don't let a .dn42 record point Atlas
            // onto arbitrary clearnet address space.
            //
            if (addresses.Any(
                address =>
                    !Dn42AddressSpace.Contains(
                        address)))
            {
                skippedExternal.Add(
                    domain);
                rejectedDomains.Add(domain);

                continue;
            }

            domains.Add(domain);
            // Benign duplicate rows contribute only already validated addresses.
            approvedAddresses[domain] = approvedAddresses.TryGetValue(domain, out var previous)
                ? previous.Concat(addresses.Select(System.Net.IPAddress.Parse)).Distinct().ToArray()
                : addresses.Select(System.Net.IPAddress.Parse).ToArray();
        }

        // A conflicting external row must not publish the name of a prefix-excluded domain.
        skippedExternal.RemoveAll(excludedPrefixDomains.Contains);

        domains =
            domains
                .Where(domain => !rejectedDomains.Contains(domain))
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x)
                .ToList();

        Console.WriteLine();

        Console.WriteLine(
            $"Resolved DN42 domains to probe: {domains.Count}");

        Console.WriteLine(
            $"Skipped external/mixed domains:  {skippedExternal.Count}");

        var work =
            domains
                .SelectMany(
                    domain =>
                        probeTargets.Select(
                            target => new
                            {
                                Domain =
                                    domain,

                                Scheme =
                                    target.Item1,

                                Port =
                                    target.Item2
                            }))
                .ToList();

        Console.WriteLine(
            $"Total HTTP probe targets:        {work.Count}");

        Console.WriteLine();
        Console.WriteLine(
            "Starting scan...");
        Console.WriteLine();

        var startedAt =
            DateTimeOffset.Now;

        var results =
            new ConcurrentBag<HttpProbeResult>();

        var completed = 0;
        var reachable = 0;

        await Parallel.ForEachAsync(
            work,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = 32
            },
            async (
                item,
                cancellationToken) =>
            {
                var result =
                    await (probeAsync != null
                        ? probeAsync(item.Domain, item.Scheme, item.Port, cancellationToken)
                        : HttpProber.ProbeAsync(item.Domain, item.Scheme, item.Port,
                            exclusionPolicy, approvedAddresses[item.Domain], cancellationToken));

                results.Add(result);

                var currentCompleted =
                    Interlocked.Increment(
                        ref completed);

                if (result.Reachable)
                {
                    var currentReachable =
                        Interlocked.Increment(
                            ref reachable);

                    Console.WriteLine(
                        $"[+] {result.Scheme}://" +
                        $"{result.Domain}:{result.Port} " +
                        $"robotsHttp={result.RobotsStatusCode} " +
                        $"robots={result.Robots} " +
                        $"allowed={result.RobotsAllowed} " +
                        $"redirect={result.RedirectLocation}");

                    if (result.StatusCode != null)
                    {
                        Console.WriteLine(
                            $"    pageHttp={result.StatusCode} " +
                            $"title=\"{result.Title}\" " +
                            $"contentType={result.ContentType} " +
                            $"links={result.DiscoveredLinks.Count}" +
                            $"{(result.LinksTruncated ? "+" : "")} " +
                            $"dn42Mentions={result.Dn42Mentions.Count}" +
                            $"{(result.Dn42MentionsTruncated ? "+" : "")} " +
                            $"pageRedirect={result.HomepageRedirectLocation}");
                    }

                    Console.WriteLine(
                        $"    reachable={currentReachable} " +
                        $"progress={currentCompleted}/{work.Count}");
                }
                else if (
                    currentCompleted % 100 == 0)
                {
                    Console.WriteLine(
                        $"[...] progress " +
                        $"{currentCompleted}/{work.Count} " +
                        $"reachable=" +
                        $"{Volatile.Read(ref reachable)}");
                }
            });

        var finishedAt =
            DateTimeOffset.Now;

        var orderedResults =
            results
                .OrderBy(
                    x => x.Domain)
                .ThenBy(
                    x => x.Scheme)
                .ThenBy(
                    x => x.Port)
                .ToList();

        return new WebScanResult(startedAt, finishedAt, domains.Count, skippedExternal,
            excludedByHostname, excludedByPrefix, work.Count, reachable, orderedResults);
    }
}

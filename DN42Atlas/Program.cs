using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

using DN42Atlas.Probing;
using DN42Atlas.Registry;
using DN42Atlas.Reporting;

var probeTargets = new[]
{
    ("http", 80),
    ("https", 443),

    ("http", 81),
    ("http", 3000),
    ("http", 3001),
    ("http", 4000),
    ("http", 5000),
    ("http", 5001),
    ("http", 7000),
    ("http", 8000),
    ("http", 8001),
    ("http", 8008),
    ("http", 8080),
    ("http", 8081),
    ("http", 8088),
    ("http", 8880),
    ("http", 8888),
    ("http", 9000),
    ("http", 9090),

    ("https", 4443),
    ("https", 8443),
    ("https", 9443),
    ("https", 10443)
};


//
// Generate an HTML Atlas viewer from an existing
// web-probe JSON file.
//
if (args.Length > 0 &&
    args[0] == "report")
{
    if (args.Length < 2)
    {
        Console.WriteLine(
            "Usage: report <web-probe.json>");

        return;
    }

    var inputPath =
        args[1];

    if (!File.Exists(inputPath))
    {
        Console.WriteLine(
            $"File not found: {inputPath}");

        return;
    }

    var htmlPath =
        Path.ChangeExtension(
            inputPath,
            ".html");

    await AtlasReportGenerator.GenerateAsync(
        inputPath,
        htmlPath);

    Console.WriteLine(
        $"Atlas viewer written to: {htmlPath}");

    return;
}


//
// Single-host probe test
//
if (args.Length > 0 &&
    args[0] == "probe-test")
{
    var tasks =
        probeTargets.Select(target =>
            HttpProber.ProbeAsync(
                "burble.dn42",
                target.Item1,
                target.Item2));

    var results =
        await Task.WhenAll(tasks);

    foreach (var result in results)
    {
        Console.WriteLine(
            $"{result.Scheme}://" +
            $"{result.Domain}:{result.Port} " +
            $"reachable={result.Reachable} " +
            $"robotsHttp={result.RobotsStatusCode} " +
            $"robots={result.Robots} " +
            $"redirect={result.RedirectLocation} " +
            $"allowed={result.RobotsAllowed} " +
            $"error={result.Error}");

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
    }

    return;
}


//
// HTTP/HTTPS scan using an existing
// DNS-resolution JSON file.
//
if (args.Length > 0 &&
    args[0] == "web-scan")
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

    using var resolutionDocument =
        JsonDocument.Parse(
            await File.ReadAllTextAsync(
                resolutionPath));

    var domains =
        new List<string>();

    var skippedExternal =
        new List<string>();

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

        if (string.IsNullOrWhiteSpace(
            domain))
        {
            continue;
        }

        if (!domain.EndsWith(
            ".dn42",
            StringComparison.OrdinalIgnoreCase))
        {
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
            continue;

        //
        // Safety guard:
        //
        // Don't let a .dn42 record point Atlas
        // onto arbitrary clearnet address space.
        //
        if (addresses.Any(
            address =>
                !IsDn42Address(
                    address)))
        {
            skippedExternal.Add(
                domain);

            continue;
        }

        domains.Add(domain);
    }

    domains =
        domains
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
                await HttpProber.ProbeAsync(
                    item.Domain,
                    item.Scheme,
                    item.Port,
                    cancellationToken);

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

    var webJson =
        JsonSerializer.Serialize(
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
                    domains.Count,

                SkippedExternalOrMixedDomains =
                    skippedExternal,

                ProbeTargetCount =
                    work.Count,

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
            },
            new JsonSerializerOptions
            {
                WriteIndented = true
            });

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
        viewerOutputPath);

    Console.WriteLine();
    Console.WriteLine(
        "==============================");

    Console.WriteLine(
        "Scan complete.");

    Console.WriteLine(
        "==============================");

    Console.WriteLine(
        $"Domains probed:    {domains.Count}");

    Console.WriteLine(
        $"Origins reachable: {reachable}");

    Console.WriteLine(
        $"Total probes:      {work.Count}");

    Console.WriteLine(
        $"Duration:          " +
        $"{finishedAt - startedAt}");

    Console.WriteLine(
        $"Results:           {webOutputPath}");

    Console.WriteLine(
        $"Viewer:            {viewerOutputPath}");

    return;
}


//
// Normal registry + DNS-resolution scan
//
var registryPath =
    Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder
                .UserProfile),
        "dn42-registry",
        "data",
        "dns");

var registryDomains =
    new List<DomainObject>();

foreach (var file in
    Directory.EnumerateFiles(
        registryPath))
{
    try
    {
        registryDomains.Add(
            DomainParser.Parse(
                file));
    }
    catch (Exception ex)
    {
        Console.WriteLine(
            $"Registry parse failed: " +
            $"{file}: {ex.Message}");
    }
}

registryDomains =
    registryDomains
        .Where(
            x =>
                x.Domain.EndsWith(
                    ".dn42",
                    StringComparison.OrdinalIgnoreCase))
        .ToList();

Console.WriteLine(
    $"Registered domains: " +
    $"{registryDomains.Count}");

Console.WriteLine();

var resolutions =
    new List<DomainResolution>();

foreach (var domain in
    registryDomains.OrderBy(
        x => x.Domain))
{
    try
    {
        var addresses =
            await Dns.GetHostAddressesAsync(
                domain.Domain);

        var result =
            new DomainResolution
            {
                Domain =
                    domain.Domain,

                Status =
                    addresses.Length > 0
                        ? ResolutionStatus.Resolved
                        : ResolutionStatus.NotFound,

                Addresses =
                    addresses.ToList()
            };

        resolutions.Add(
            result);

        if (result.Status ==
            ResolutionStatus.Resolved)
        {
            Console.WriteLine(
                $"[+] {domain.Domain}");

            foreach (
                var address in addresses)
            {
                Console.WriteLine(
                    $"    {address}");
            }
        }
        else
        {
            Console.WriteLine(
                $"[-] {domain.Domain}");
        }
    }
    catch (SocketException ex)
    {
        var status =
            ex.SocketErrorCode switch
            {
                SocketError.HostNotFound =>
                    ResolutionStatus.NotFound,

                SocketError.TryAgain =>
                    ResolutionStatus.TemporaryFailure,

                _ =>
                    ResolutionStatus.Error
            };

        resolutions.Add(
            new DomainResolution
            {
                Domain =
                    domain.Domain,

                Status =
                    status,

                Error =
                    ex.SocketErrorCode
                        .ToString()
            });

        Console.WriteLine(
            $"[-] {domain.Domain} " +
            $"({status})");
    }
    catch (Exception ex)
    {
        resolutions.Add(
            new DomainResolution
            {
                Domain =
                    domain.Domain,

                Status =
                    ResolutionStatus.Error,

                Error =
                    ex.Message
            });

        Console.WriteLine(
            $"[!] {domain.Domain}: " +
            $"{ex.Message}");
    }
}

var resolved =
    resolutions.Count(
        x =>
            x.Status ==
            ResolutionStatus.Resolved);

var notFound =
    resolutions.Count(
        x =>
            x.Status ==
            ResolutionStatus.NotFound);

var temporaryFailures =
    resolutions.Count(
        x =>
            x.Status ==
            ResolutionStatus.TemporaryFailure);

var errors =
    resolutions.Count(
        x =>
            x.Status ==
            ResolutionStatus.Error);

Console.WriteLine();

Console.WriteLine(
    $"Registered:        " +
    $"{registryDomains.Count}");

Console.WriteLine(
    $"Resolved:          " +
    $"{resolved}");

Console.WriteLine(
    $"Not found:         " +
    $"{notFound}");

Console.WriteLine(
    $"Temporary failure: " +
    $"{temporaryFailures}");

Console.WriteLine(
    $"Errors:            " +
    $"{errors}");

var resolutionOutputPath =
    Path.Combine(
        Environment.CurrentDirectory,
        "domain-resolution.json");

var resolutionJson =
    JsonSerializer.Serialize(
        resolutions.Select(
            x => new
            {
                x.Domain,

                Status =
                    x.Status.ToString(),

                Addresses =
                    x.Addresses.Select(
                        a => a.ToString()),

                x.Error
            }),
        new JsonSerializerOptions
        {
            WriteIndented = true
        });

await File.WriteAllTextAsync(
    resolutionOutputPath,
    resolutionJson);

Console.WriteLine();

Console.WriteLine(
    $"Results written to: " +
    $"{resolutionOutputPath}");


//
// DN42 address-space guard
//
static bool IsDn42Address(
    string value)
{
    if (!IPAddress.TryParse(
        value,
        out var address))
    {
        return false;
    }

    var bytes =
        address.GetAddressBytes();

    if (address.AddressFamily ==
        AddressFamily.InterNetworkV6)
    {
        //
        // DN42 uses ULA IPv6 space.
        //
        return bytes[0] == 0xfd;
    }

    if (address.AddressFamily !=
        AddressFamily.InterNetwork)
    {
        return false;
    }

    //
    // 172.20.0.0/14
    //
    if (bytes[0] == 172 &&
        bytes[1] >= 20 &&
        bytes[1] <= 23)
    {
        return true;
    }

    //
    // 172.31.0.0/16
    //
    if (bytes[0] == 172 &&
        bytes[1] == 31)
    {
        return true;
    }

    //
    // 10.100.0.0/14
    //
    if (bytes[0] == 10 &&
        bytes[1] >= 100 &&
        bytes[1] <= 103)
    {
        return true;
    }

    //
    // 10.127.0.0/16
    //
    if (bytes[0] == 10 &&
        bytes[1] == 127)
    {
        return true;
    }

    return false;
}
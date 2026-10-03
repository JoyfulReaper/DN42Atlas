using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DN42Atlas.Probing;
using DN42Atlas.Registry;

if (args.Length > 0 && args[0] == "probe-test")
{
    var probeTargets = new[]
    {
        ("http", 80),
        ("https", 443),
        ("http", 8080),
        ("https", 8443),
        ("http", 8000),
        ("http", 8888),
        ("http", 3000),
        ("http", 5000)
    };

    var tasks = probeTargets.Select(x =>
        HttpProber.ProbeAsync("burble.dn42", x.Item1, x.Item2));

    var results = await Task.WhenAll(tasks);

    foreach (var result in results)
    {
        Console.WriteLine(
            $"{result.Scheme}://{result.Domain}:{result.Port} " +
            $"reachable={result.Reachable} " +
            $"robotsHttp={result.RobotsStatusCode} " +
            $"robots={result.Robots} " +
            $"redirect={result.RedirectLocation} " +
            $"allowed={result.RobotsAllowed} " +
            $"error={result.Error}");
    }

    return;
}

var registryPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    "dn42-registry",
    "data",
    "dns");

var domains = new List<DomainObject>();

foreach (var file in Directory.EnumerateFiles(registryPath))
{
    try
    {
        domains.Add(DomainParser.Parse(file));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Registry parse failed: {file}: {ex.Message}");
    }
}

domains = domains
    .Where(x => x.Domain.EndsWith(".dn42", StringComparison.OrdinalIgnoreCase))
    .ToList();

Console.WriteLine($"Registered domains: {domains.Count}");
Console.WriteLine();

var resolutions = new List<DomainResolution>();

foreach (var domain in domains.OrderBy(x => x.Domain))
{
    try
    {
        var addresses = await Dns.GetHostAddressesAsync(domain.Domain);

        var result = new DomainResolution
        {
            Domain = domain.Domain,
            Status = addresses.Length > 0
                ? ResolutionStatus.Resolved
                : ResolutionStatus.NotFound,
            Addresses = addresses.ToList()
        };

        resolutions.Add(result);

        if (result.Status == ResolutionStatus.Resolved)
        {
            Console.WriteLine($"[+] {domain.Domain}");

            foreach (var address in addresses)
                Console.WriteLine($"    {address}");
        }
        else
        {
            Console.WriteLine($"[-] {domain.Domain}");
        }
    }
    catch (SocketException ex)
    {
        var status = ex.SocketErrorCode switch
        {
            SocketError.HostNotFound => ResolutionStatus.NotFound,
            SocketError.TryAgain => ResolutionStatus.TemporaryFailure,
            _ => ResolutionStatus.Error
        };

        resolutions.Add(new DomainResolution
        {
            Domain = domain.Domain,
            Status = status,
            Error = ex.SocketErrorCode.ToString()
        });

        Console.WriteLine($"[-] {domain.Domain} ({status})");
    }
    catch (Exception ex)
    {
        resolutions.Add(new DomainResolution
        {
            Domain = domain.Domain,
            Status = ResolutionStatus.Error,
            Error = ex.Message
        });

        Console.WriteLine($"[!] {domain.Domain}: {ex.Message}");
    }
}

var resolved = resolutions.Count(x => x.Status == ResolutionStatus.Resolved);
var notFound = resolutions.Count(x => x.Status == ResolutionStatus.NotFound);
var temporaryFailures = resolutions.Count(x => x.Status == ResolutionStatus.TemporaryFailure);
var errors = resolutions.Count(x => x.Status == ResolutionStatus.Error);

Console.WriteLine();
Console.WriteLine($"Registered:        {domains.Count}");
Console.WriteLine($"Resolved:          {resolved}");
Console.WriteLine($"Not found:         {notFound}");
Console.WriteLine($"Temporary failure: {temporaryFailures}");
Console.WriteLine($"Errors:            {errors}");

var outputPath = Path.Combine(
    Environment.CurrentDirectory,
    "domain-resolution.json");

var json = JsonSerializer.Serialize(
    resolutions.Select(x => new
    {
        x.Domain,
        Status = x.Status.ToString(),
        Addresses = x.Addresses.Select(a => a.ToString()),
        x.Error
    }),
    new JsonSerializerOptions
    {
        WriteIndented = true
    });

await File.WriteAllTextAsync(outputPath, json);

Console.WriteLine();
Console.WriteLine($"Results written to: {outputPath}");
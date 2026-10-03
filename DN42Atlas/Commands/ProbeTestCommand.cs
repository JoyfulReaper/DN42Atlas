using DN42Atlas.Probing;
using DN42Atlas.Policy;
using DN42Atlas.Networking;
using DN42Atlas.Registry;
using System.Net;

namespace DN42Atlas.Commands;

public sealed class ProbeTestCommand(
    IReadOnlyList<(string, int)> probeTargets,
    ExclusionPolicy exclusionPolicy,
    Func<string, Task<IPAddress[]>>? resolveAsync = null,
    Func<string, string, int, CancellationToken, Task<HttpProbeResult>>? probeAsync = null)
{
    public async Task ExecuteAsync()
    {
        const string hostname = "burble.dn42";
        if (exclusionPolicy.IsHostExcluded(hostname))
        {
            Console.WriteLine("Probe-test skipped: host is excluded.");
            return;
        }

        var addresses = await (resolveAsync?.Invoke(hostname) ?? Dns.GetHostAddressesAsync(hostname));
        if (!ProbeDestination.IsAllowed(exclusionPolicy, hostname, addresses))
        {
            Console.WriteLine("Probe-test skipped: addresses are excluded or outside DN42.");
            return;
        }

        var tasks =
            probeTargets.Select(target =>
                probeAsync != null
                    ? probeAsync(hostname, target.Item1, target.Item2, CancellationToken.None)
                    : HttpProber.ProbeAsync(hostname, target.Item1, target.Item2, exclusionPolicy, addresses));

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
}

using DN42Atlas.Probing;

namespace DN42Atlas.Commands;

public sealed class ProbeTestCommand(IReadOnlyList<(string, int)> probeTargets)
{
    public async Task ExecuteAsync()
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
}

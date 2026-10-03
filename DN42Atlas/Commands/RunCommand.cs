using DN42Atlas.Publishing;

namespace DN42Atlas.Commands;

public sealed class RunCommand(ResolveCommand resolve, WebScanCommand webScan)
{
    public async Task<int> ExecuteAsync()
    {
        var resolutionPath = await resolve.ExecuteAsync();
        // WebScanCommand already generates the report from its own JSON output.
        var scanPath = await webScan.ExecuteAsync(["web-scan", resolutionPath]);
        if (scanPath == null)
            return 1;

        var publishedDirectory = Path.Combine(Environment.CurrentDirectory, "published");
        await ArtifactPublisher.PublishAsync(scanPath, publishedDirectory);
        Console.WriteLine($"Published viewer: {Path.Combine(publishedDirectory, "index.html")}");
        Console.WriteLine($"Published results: {Path.Combine(publishedDirectory, "latest.json")}");
        return 0;
    }
}

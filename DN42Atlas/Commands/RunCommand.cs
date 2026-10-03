namespace DN42Atlas.Commands;

public sealed class RunCommand(ResolveCommand resolve, WebScanCommand webScan)
{
    public async Task<int> ExecuteAsync()
    {
        var resolutionPath = await resolve.ExecuteAsync();
        // WebScanCommand already generates the report from its own JSON output.
        var scanPath = await webScan.ExecuteAsync(["web-scan", resolutionPath]);
        return scanPath != null ? 0 : 1;
    }
}

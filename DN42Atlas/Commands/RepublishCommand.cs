using DN42Atlas.Policy;
using DN42Atlas.Publishing;

namespace DN42Atlas.Commands;

public sealed class RepublishCommand(ExclusionPolicy policy, string publishedDirectory, string statePath)
{
    public async Task<int> ExecuteAsync()
    {
        var state = await PublicationState.LoadAsync(statePath, publishedDirectory);
        await ArtifactPublisher.PublishAsync(state.RawScanPath, publishedDirectory, policy, statePath,
            expectedSha256: state.RawScanSha256);
        Console.WriteLine($"Republished current scan: {state.RawScanPath}");
        return 0;
    }
}

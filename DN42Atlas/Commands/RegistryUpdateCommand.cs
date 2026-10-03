using DN42Atlas.Registry;

namespace DN42Atlas.Commands;

public static class RegistryUpdateCommand
{
    public static async Task<int> ExecuteAsync()
    {
        var registryPath =
            Environment.GetEnvironmentVariable(
                "DN42ATLAS_REGISTRY_PATH");

        if (string.IsNullOrWhiteSpace(registryPath))
            throw new InvalidOperationException(
                "DN42ATLAS_REGISTRY_PATH is required for registry-update.");

        var upstream =
            Environment.GetEnvironmentVariable(
                "DN42ATLAS_REGISTRY_UPSTREAM");

        var result = await new RegistryUpdater().UpdateAsync(
            registryPath,
            upstream);

        Console.WriteLine(
            $"Registry updated to {result.CommitSha} " +
            $"from {result.Upstream} at " +
            $"{result.ObservedAt.ToUniversalTime():yyyy-MM-dd HH:mm:ss 'UTC'}.");

        return 0;
    }
}

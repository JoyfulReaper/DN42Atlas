using DN42Atlas.Policy;

namespace DN42Atlas.OptOut.Exclusions;

public static class RuntimeExclusionMaterializer
{
    public static async Task MaterializeAsync(
        ExclusionStore store,
        string runtimeBundlePath,
        CancellationToken cancellationToken = default)
    {
        var active = await store.GetActiveAsync(cancellationToken);

        await RuntimeExclusionBundle.WriteAtomicAsync(
            runtimeBundlePath,
            active
                .Where(record =>
                    record.ResourceType == ExclusionResourceType.Domain)
                .Select(record => record.ResourceValue),
            active
                .Where(record =>
                    record.ResourceType is
                        ExclusionResourceType.IPv4Prefix or
                        ExclusionResourceType.IPv6Prefix)
                .Select(record => record.ResourceValue),
            cancellationToken);
    }
}

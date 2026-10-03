namespace DN42Atlas.OptOut.Exclusions;

public static class ExclusionMaintenance
{
    public static async Task InitializeAsync(
        string databasePath,
        string runtimeBundlePath,
        CancellationToken cancellationToken = default)
    {
        if (File.Exists(runtimeBundlePath))
            throw new InvalidOperationException(
                "The runtime exclusion bundle already exists.");

        await ExclusionStore.InitializeAsync(
            databasePath,
            cancellationToken);
        await RuntimeExclusionMaterializer.MaterializeAsync(
            new ExclusionStore(databasePath),
            runtimeBundlePath,
            cancellationToken);
    }

    public static Task MaterializeAsync(
        string databasePath,
        string runtimeBundlePath,
        CancellationToken cancellationToken = default) =>
        RuntimeExclusionMaterializer.MaterializeAsync(
            new ExclusionStore(databasePath),
            runtimeBundlePath,
            cancellationToken);
}

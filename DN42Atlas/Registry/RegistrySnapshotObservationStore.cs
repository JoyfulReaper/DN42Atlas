using System.Text.Json;

namespace DN42Atlas.Registry;

public sealed record RegistrySnapshotObservation(
    string CommitSha,
    DateTimeOffset ObservedAt);

public static class RegistrySnapshotObservationStore
{
    public const string FileName = "dn42atlas-registry-update.json";

    public static bool TryRead(
        string gitDirectory,
        out RegistrySnapshotObservation? observation)
    {
        observation = null;

        try
        {
            var path = Path.Combine(gitDirectory, FileName);

            if (!File.Exists(path))
                return false;

            observation = JsonSerializer.Deserialize<RegistrySnapshotObservation>(
                File.ReadAllText(path));

            return observation is not null &&
                !string.IsNullOrWhiteSpace(observation.CommitSha) &&
                observation.ObservedAt != default;
        }
        catch (Exception ex) when (
            ex is IOException or
            UnauthorizedAccessException or
            JsonException)
        {
            observation = null;
            return false;
        }
    }

    public static async Task WriteAsync(
        string gitDirectory,
        RegistrySnapshotObservation observation,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(gitDirectory, FileName);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(observation),
                cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}

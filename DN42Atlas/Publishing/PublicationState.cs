using System.Text.Json;

namespace DN42Atlas.Publishing;

public sealed record PublicationState(int Version, string RawScanPath, DateTimeOffset PublishedAtUtc, string RawScanSha256)
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string ConfiguredPath => Path.GetFullPath(
        Environment.GetEnvironmentVariable("DN42ATLAS_PUBLICATION_STATE_PATH")
        ?? Path.Combine(Environment.CurrentDirectory, ".dn42atlas", "publication-state.json"));

    public static string ValidateLocation(string statePath, string publishedDirectory)
    {
        var fullPath = Path.GetFullPath(statePath);
        EnsureOutsidePublicRoot(fullPath, publishedDirectory);
        return fullPath;
    }

    public static void EnsureOutsidePublicRoot(string path, string publishedDirectory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(publishedDirectory));
        var fullPath = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (fullPath.Equals(root, comparison) || fullPath.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            throw new InvalidDataException("Raw scans and publication state must be outside the public web root.");
    }

    public static async Task<PublicationState> LoadAsync(string path, string publishedDirectory, CancellationToken token = default)
    {
        var fullPath = ValidateLocation(path, publishedDirectory);
        var state = JsonSerializer.Deserialize<PublicationState>(await File.ReadAllTextAsync(fullPath, token), JsonOptions)
            ?? throw new InvalidDataException("Publication state is empty.");
        if (state.Version != 1)
            throw new InvalidDataException($"Unsupported publication state version: {state.Version}.");
        if (string.IsNullOrWhiteSpace(state.RawScanPath) || !Path.IsPathFullyQualified(state.RawScanPath))
            throw new InvalidDataException("Publication state must contain an absolute raw scan path.");
        EnsureOutsidePublicRoot(state.RawScanPath, publishedDirectory);
        if (state.PublishedAtUtc == default || string.IsNullOrWhiteSpace(state.RawScanSha256)
            || state.RawScanSha256.Length != 64 || !state.RawScanSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Publication state has an invalid timestamp or SHA-256 hash.");
        return state;
    }
}

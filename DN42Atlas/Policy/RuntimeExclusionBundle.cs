using System.Text.Json;
using System.Text.Json.Serialization;

namespace DN42Atlas.Policy;

public sealed record RuntimeExclusionRules(
    IReadOnlyList<string> HostRules,
    IReadOnlyList<string> PrefixRules);

public static class RuntimeExclusionBundle
{
    public const int CurrentVersion = 1;

    public static RuntimeExclusionRules Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Required runtime exclusion bundle not found: {path}",
                path);

        BundleDocument document;

        try
        {
            document = JsonSerializer.Deserialize<BundleDocument>(
                    File.ReadAllText(path)) ??
                throw new InvalidDataException(
                    "Runtime exclusion bundle is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                "Runtime exclusion bundle is malformed.",
                ex);
        }

        if (document.Version != CurrentVersion)
            throw new InvalidDataException(
                $"Unsupported runtime exclusion bundle version: {document.Version}.");

        if (document.HostRules is null || document.PrefixRules is null)
            throw new InvalidDataException(
                "Runtime exclusion bundle rule lists must not be null.");

        try
        {
            var hosts = document.HostRules
                .Select(ExclusionResourceNormalizer.NormalizeDomain)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            var prefixes = document.PrefixRules
                .Select(value => ExclusionResourceNormalizer.NormalizePrefix(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();

            return new RuntimeExclusionRules(hosts, prefixes);
        }
        catch (Exception ex) when (
            ex is ArgumentException or FormatException)
        {
            throw new InvalidDataException(
                "Runtime exclusion bundle contains an invalid rule.",
                ex);
        }
    }

    public static async Task WriteAtomicAsync(
        string path,
        IEnumerable<string> hostRules,
        IEnumerable<string> prefixRules,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);

        var document = new BundleDocument
        {
            Version = CurrentVersion,
            HostRules = hostRules
                .Select(ExclusionResourceNormalizer.NormalizeDomain)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray(),
            PrefixRules = prefixRules
                .Select(value => ExclusionResourceNormalizer.NormalizePrefix(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray()
        };
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    new JsonSerializerOptions { WriteIndented = true },
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private sealed class BundleDocument
    {
        [JsonPropertyName("version")]
        [JsonRequired]
        public int Version { get; init; }

        [JsonPropertyName("hostRules")]
        [JsonRequired]
        public IReadOnlyList<string> HostRules { get; init; } = [];

        [JsonPropertyName("prefixRules")]
        [JsonRequired]
        public IReadOnlyList<string> PrefixRules { get; init; } = [];
    }
}

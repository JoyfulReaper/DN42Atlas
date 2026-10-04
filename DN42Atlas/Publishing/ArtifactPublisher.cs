using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DN42Atlas.Policy;

namespace DN42Atlas.Publishing;

public static class ArtifactPublisher
{
    private static readonly (string FileName, string ResourceName)[] StaticFiles =
    [
        ("about.html", "DN42Atlas.site.about.html"),
        ("opt-out.html", "DN42Atlas.site.opt-out.html"),
        ("robots.txt", "DN42Atlas.site.robots.txt")
    ];

    public static async Task PublishAsync(string scanPath, string publishedDirectory, ExclusionPolicy policy,
        string statePath, CancellationToken cancellationToken = default, string? expectedSha256 = null)
    {
        scanPath = Path.GetFullPath(scanPath);
        statePath = PublicationState.ValidateLocation(statePath, publishedDirectory);
        PublicationState.EnsureOutsidePublicRoot(scanPath, publishedDirectory);
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (scanPath.Equals(statePath, pathComparison))
            throw new InvalidDataException("Publication state must not overwrite the raw scan.");
        var rawScan = await File.ReadAllBytesAsync(scanPath, cancellationToken);
        var hash = Convert.ToHexString(SHA256.HashData(rawScan));
        if (expectedSha256 != null && !hash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Raw scan SHA-256 does not match publication state.");
        var artifacts = PublicArtifactGenerator.Generate(rawScan, policy);
        Directory.CreateDirectory(publishedDirectory);
        // Stage on the destination filesystem so replacement uses a rename, not a copy.
        var id = Guid.NewGuid().ToString("N");
        var stagedJson = Path.Combine(publishedDirectory, $".latest-{id}.tmp");
        var stagedHtml = Path.Combine(publishedDirectory, $".index-{id}.tmp");
        var stateDirectory = Path.GetDirectoryName(statePath)!;
        Directory.CreateDirectory(stateDirectory);
        var stagedState = Path.Combine(stateDirectory, $".publication-state-{id}.tmp");
        var staticFiles = StaticFiles
            .Select(file => (
                file.ResourceName,
                DestinationPath: Path.Combine(publishedDirectory, file.FileName),
                StagedPath: Path.Combine(publishedDirectory, $".{file.FileName}-{id}.tmp")))
            .ToArray();
        try
        {
            await StageTextAsync(artifacts.Json, stagedJson, cancellationToken);
            await StageTextAsync(artifacts.Html, stagedHtml, cancellationToken);
            var state = new PublicationState(1, scanPath, DateTimeOffset.UtcNow, hash);
            await StageTextAsync(JsonSerializer.Serialize(state, PublicationState.JsonOptions), stagedState, cancellationToken);

            foreach (var file in staticFiles)
            {
                if (File.Exists(file.DestinationPath))
                    continue;

                await using var source = typeof(ArtifactPublisher).Assembly
                    .GetManifestResourceStream(file.ResourceName)
                    ?? throw new InvalidOperationException($"Bundled static file is missing: {file.ResourceName}");
                await StageAsync(source, file.StagedPath, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            foreach (var file in staticFiles.Where(file => File.Exists(file.StagedPath)))
            {
                // Never replace an operator-maintained file, including one created during staging.
                try { File.Move(file.StagedPath, file.DestinationPath, overwrite: false); }
                catch (IOException) when (File.Exists(file.DestinationPath)) { }
            }

            // Both artifacts are complete before either stable name is replaced.
            File.Move(stagedJson, Path.Combine(publishedDirectory, "latest.json"), overwrite: true);
            File.Move(stagedHtml, Path.Combine(publishedDirectory, "index.html"), overwrite: true);
            // Never advance private state unless both public replacements succeeded.
            File.Move(stagedState, statePath, overwrite: true);
        }
        finally
        {
            File.Delete(stagedJson);
            File.Delete(stagedHtml);
            File.Delete(stagedState);
            foreach (var file in staticFiles)
                File.Delete(file.StagedPath);
        }
    }

    private static async Task StageTextAsync(string text, string destinationPath, CancellationToken cancellationToken)
    {
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(text));
        await StageAsync(source, destinationPath, cancellationToken);
    }

    private static async Task StageAsync(Stream source, string destinationPath, CancellationToken cancellationToken)
    {
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);
        await source.CopyToAsync(destination, cancellationToken);
        await destination.FlushAsync(cancellationToken);
        destination.Flush(flushToDisk: true);
    }
}

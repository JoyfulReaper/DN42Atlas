namespace DN42Atlas.Publishing;

public static class ArtifactPublisher
{
    private static readonly (string FileName, string ResourceName)[] StaticFiles =
    [
        ("about.html", "DN42Atlas.site.about.html"),
        ("opt-out.html", "DN42Atlas.site.opt-out.html"),
        ("robots.txt", "DN42Atlas.site.robots.txt")
    ];

    public static async Task PublishAsync(string scanPath, string publishedDirectory,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(publishedDirectory);
        // Stage on the destination filesystem so replacement uses a rename, not a copy.
        var id = Guid.NewGuid().ToString("N");
        var stagedJson = Path.Combine(publishedDirectory, $".latest-{id}.tmp");
        var stagedHtml = Path.Combine(publishedDirectory, $".index-{id}.tmp");
        var staticFiles = StaticFiles
            .Select(file => (
                file.ResourceName,
                DestinationPath: Path.Combine(publishedDirectory, file.FileName),
                StagedPath: Path.Combine(publishedDirectory, $".{file.FileName}-{id}.tmp")))
            .ToArray();
        try
        {
            await StageAsync(scanPath, stagedJson, cancellationToken);
            await StageAsync(Path.ChangeExtension(scanPath, ".html"), stagedHtml, cancellationToken);

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
        }
        finally
        {
            File.Delete(stagedJson);
            File.Delete(stagedHtml);
            foreach (var file in staticFiles)
                File.Delete(file.StagedPath);
        }
    }

    private static async Task StageAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);
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

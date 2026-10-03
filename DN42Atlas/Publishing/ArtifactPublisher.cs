namespace DN42Atlas.Publishing;

public static class ArtifactPublisher
{
    public static async Task PublishAsync(string scanPath, string publishedDirectory,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(publishedDirectory);
        // Stage on the destination filesystem so replacement uses a rename, not a copy.
        var id = Guid.NewGuid().ToString("N");
        var stagedJson = Path.Combine(publishedDirectory, $".latest-{id}.tmp");
        var stagedHtml = Path.Combine(publishedDirectory, $".index-{id}.tmp");
        var stagedOptOut = Path.Combine(publishedDirectory, $".opt-out-{id}.tmp");
        var optOutPath = Path.Combine(publishedDirectory, "opt-out.html");
        try
        {
            await StageAsync(scanPath, stagedJson, cancellationToken);
            await StageAsync(Path.ChangeExtension(scanPath, ".html"), stagedHtml, cancellationToken);
            if (!File.Exists(optOutPath))
            {
                await using var optOut = typeof(ArtifactPublisher).Assembly
                    .GetManifestResourceStream("DN42Atlas.site.opt-out.html")
                    ?? throw new InvalidOperationException("Bundled opt-out page is missing.");
                await StageAsync(optOut, stagedOptOut, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(stagedOptOut))
            {
                // Never replace an operator-maintained page, including one created during staging.
                try { File.Move(stagedOptOut, optOutPath, overwrite: false); }
                catch (IOException) when (File.Exists(optOutPath)) { }
            }

            // Both artifacts are complete before either stable name is replaced.
            File.Move(stagedJson, Path.Combine(publishedDirectory, "latest.json"), overwrite: true);
            File.Move(stagedHtml, Path.Combine(publishedDirectory, "index.html"), overwrite: true);
        }
        finally
        {
            File.Delete(stagedJson);
            File.Delete(stagedHtml);
            File.Delete(stagedOptOut);
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

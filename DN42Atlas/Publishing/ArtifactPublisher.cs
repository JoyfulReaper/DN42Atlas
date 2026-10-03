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
        try
        {
            await StageAsync(scanPath, stagedJson, cancellationToken);
            await StageAsync(Path.ChangeExtension(scanPath, ".html"), stagedHtml, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            // Both artifacts are complete before either stable name is replaced.
            File.Move(stagedJson, Path.Combine(publishedDirectory, "latest.json"), overwrite: true);
            File.Move(stagedHtml, Path.Combine(publishedDirectory, "index.html"), overwrite: true);
        }
        finally
        {
            File.Delete(stagedJson);
            File.Delete(stagedHtml);
        }
    }

    private static async Task StageAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);
        await source.CopyToAsync(destination, cancellationToken);
        await destination.FlushAsync(cancellationToken);
        destination.Flush(flushToDisk: true);
    }
}

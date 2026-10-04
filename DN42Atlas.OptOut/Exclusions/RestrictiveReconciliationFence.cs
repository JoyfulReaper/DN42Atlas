using DN42Atlas.IO;

namespace DN42Atlas.OptOut.Exclusions;

// Presence, including an interrupted/empty write, means reconciliation is unfinished.
public static class RestrictiveReconciliationFence
{
    private const string Contents = "reconciliation required\n";

    public static bool IsPending(MutationPaths paths)
    {
        try { _ = File.GetAttributes(paths.Pending); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    public static void Establish(MutationPaths paths)
    {
        if (IsPending(paths))
        {
            if ((File.GetAttributes(paths.Pending) & FileAttributes.Directory) != 0)
                throw new IOException("The reconciliation fence must be a private file.");
            // Never adopt/delete an unrelated file (especially raw scan evidence) at this reserved path.
            // A prefix, including empty content, is valid if the original write was interrupted.
            if (new FileInfo(paths.Pending).Length > Contents.Length ||
                !Contents.StartsWith(File.ReadAllText(paths.Pending), StringComparison.Ordinal))
                throw new InvalidDataException("The reconciliation fence contains unrelated data.");
            return;
        }
        using var stream = PrivateFile.CreateNew(paths.Pending);
        stream.Write("reconciliation required\n"u8);
        stream.Flush(flushToDisk: true);
    }
}

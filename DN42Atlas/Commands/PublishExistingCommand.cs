using DN42Atlas.Policy;
using DN42Atlas.Publishing;

namespace DN42Atlas.Commands;

public sealed class PublishExistingCommand(ExclusionPolicy policy, string publishedDirectory, string statePath)
{
    public static void PrintUsage() => Console.WriteLine("Usage: dn42atlas publish-existing <web-probe.json>");

    public async Task<int> ExecuteAsync(string[] args)
    {
        if (args.Length != 2)
        {
            PrintUsage();
            return 2;
        }

        var scanPath = Path.GetFullPath(args[1]);
        if (!File.Exists(scanPath))
        {
            Console.WriteLine($"File not found: {args[1]}");
            return 1;
        }

        await ArtifactPublisher.PublishAsync(scanPath, publishedDirectory, policy, statePath);
        Console.WriteLine($"Published existing scan: {scanPath}");
        return 0;
    }
}

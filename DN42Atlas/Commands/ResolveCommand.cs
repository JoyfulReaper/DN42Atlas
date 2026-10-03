using System.Text.Json;
using DN42Atlas.Registry;
using DN42Atlas.Scanning;

namespace DN42Atlas.Commands;

public sealed class ResolveCommand(RegistryResolver resolver, string? registryDirectory = null)
{
    public async Task<string> ExecuteAsync()
    {
        var registryPath =
            registryDirectory ?? Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder
                        .UserProfile),
                "dn42-registry",
                "data",
                "dns");

        var scan = await resolver.ResolveAsync(registryPath);
        var resolutions = scan.Resolutions;
        var resolved =
            resolutions.Count(
                x =>
                    x.Status ==
                    ResolutionStatus.Resolved);

        var notFound =
            resolutions.Count(
                x =>
                    x.Status ==
                    ResolutionStatus.NotFound);

        var temporaryFailures =
            resolutions.Count(
                x =>
                    x.Status ==
                    ResolutionStatus.TemporaryFailure);

        var errors =
            resolutions.Count(
                x =>
                    x.Status ==
                    ResolutionStatus.Error);

        Console.WriteLine();

        Console.WriteLine(
            $"Registered:        " +
            $"{scan.RegisteredDomainCount}");

        Console.WriteLine(
            $"Resolved:          " +
            $"{resolved}");

        Console.WriteLine(
            $"Not found:         " +
            $"{notFound}");

        Console.WriteLine(
            $"Temporary failure: " +
            $"{temporaryFailures}");

        Console.WriteLine(
            $"Errors:            " +
            $"{errors}");

        var resolutionOutputPath =
            Path.Combine(
                Environment.CurrentDirectory,
                "domain-resolution.json");

        var resolutionJson =
            JsonSerializer.Serialize(
                resolutions.Select(
                    x => new
                    {
                        x.Domain,

                        Status =
                            x.Status.ToString(),

                        Addresses =
                            x.Addresses.Select(
                                a => a.ToString()),

                        x.Error
                    }),
                new JsonSerializerOptions
                {
                    WriteIndented = true
                });

        await File.WriteAllTextAsync(
            resolutionOutputPath,
            resolutionJson);

        Console.WriteLine();

        Console.WriteLine(
            $"Results written to: " +
            $"{resolutionOutputPath}");


        return resolutionOutputPath;
    }
}

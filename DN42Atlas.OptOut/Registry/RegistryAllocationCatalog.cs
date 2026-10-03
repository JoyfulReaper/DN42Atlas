using DN42Atlas.Registry;

namespace DN42Atlas.OptOut.Registry;

public sealed class RegistryAllocationCatalog(
    string ipv4Directory,
    string ipv6Directory)
{
    private readonly string ipv4Directory =
        Path.GetFullPath(ipv4Directory);

    private readonly string ipv6Directory =
        Path.GetFullPath(ipv6Directory);

    public MaintainedAllocations FindAllocations(string activeMaintainer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activeMaintainer);

        return new MaintainedAllocations(
            FindPrefixes(
                ipv4Directory,
                AllocationParser.ParseIpv4,
                activeMaintainer),
            FindPrefixes(
                ipv6Directory,
                AllocationParser.ParseIpv6,
                activeMaintainer));
    }

    private static IReadOnlyList<string> FindPrefixes(
        string directory,
        Func<string, AllocationObject> parse,
        string activeMaintainer)
    {
        var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in Directory.EnumerateFiles(directory))
        {
            AllocationObject allocation;

            try
            {
                allocation = parse(path);
            }
            catch (InvalidDataException)
            {
                continue;
            }

            if (allocation.Maintainers.Any(
                    maintainer => string.Equals(
                        maintainer,
                        activeMaintainer,
                        StringComparison.OrdinalIgnoreCase)))
            {
                prefixes.Add(allocation.Prefix);
            }
        }

        return prefixes
            .OrderBy(prefix => prefix, StringComparer.Ordinal)
            .ToArray();
    }
}

using DN42Atlas.Registry;
using DN42Atlas.Policy;

namespace DN42Atlas.OptOut.Registry;

public sealed class RegistryDomainCatalog(string domainDirectory)
{
    private readonly string domainDirectory =
        Path.GetFullPath(domainDirectory);

    public IReadOnlyList<string> FindDomains(string activeMaintainer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activeMaintainer);

        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in Directory.EnumerateFiles(domainDirectory))
        {
            DomainObject domain;

            try
            {
                domain = DomainParser.Parse(path);
            }
            catch (InvalidDataException)
            {
                continue;
            }

            if (!domain.Domain.EndsWith(
                    ".dn42",
                    StringComparison.OrdinalIgnoreCase) ||
                !domain.Maintainers.Any(
                    maintainer => string.Equals(
                        maintainer,
                        activeMaintainer,
                        StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            try { domains.Add(ExclusionResourceNormalizer.NormalizeDomain(domain.Domain)); }
            catch (Exception ex) when (ex is ArgumentException or FormatException) { }
        }

        return domains
            .OrderBy(domain => domain, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

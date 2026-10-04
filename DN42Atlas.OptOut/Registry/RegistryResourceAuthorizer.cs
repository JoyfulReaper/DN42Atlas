using System.Net.Sockets;
using DN42Atlas.OptOut.Exclusions;
using DN42Atlas.Policy;
using DN42Atlas.Registry;

namespace DN42Atlas.OptOut.Registry;

public sealed record ExactResource(ExclusionResourceType Type, string Value)
{
    public static ExactResource Parse(string type, string value)
    {
        if (type.Length > 16 || value.Length > 253)
            throw new FormatException("Invalid resource length.");
        return type switch
        {
            "Domain" => new(ExclusionResourceType.Domain, ExclusionResourceNormalizer.NormalizeDomain(value)),
            "IPv4Prefix" => new(ExclusionResourceType.IPv4Prefix,
                ExclusionResourceNormalizer.NormalizePrefix(value, AddressFamily.InterNetwork)),
            "IPv6Prefix" => new(ExclusionResourceType.IPv6Prefix,
                ExclusionResourceNormalizer.NormalizePrefix(value, AddressFamily.InterNetworkV6)),
            _ => throw new FormatException("Unknown resource type.")
        };
    }
}

public sealed class RegistryResourceAuthorizer(RegistryDomainCatalog domains,
    RegistryAllocationCatalog allocations, Func<RegistrySnapshot> getSnapshot)
{
    public RegistrySnapshot? Authorize(string maintainer, ExactResource resource)
    {
        var before = getSnapshot();
        if (!Safe(before)) return null;
        var owned = resource.Type switch
        {
            ExclusionResourceType.Domain => domains.FindDomains(maintainer),
            ExclusionResourceType.IPv4Prefix => allocations.FindAllocations(maintainer).Ipv4Prefixes,
            ExclusionResourceType.IPv6Prefix => allocations.FindAllocations(maintainer).Ipv6Prefixes,
            _ => []
        };
        if (!owned.Contains(resource.Value, StringComparer.OrdinalIgnoreCase)) return null;
        // Reject a checkout change during the ownership read, not just one before POST.
        var after = getSnapshot();
        return Safe(after) && before.CommitSha == after.CommitSha && before.ObservedAt == after.ObservedAt
            ? after : null;
    }

    private static bool Safe(RegistrySnapshot snapshot) => snapshot.Status == RegistrySnapshotStatus.Fresh
        && !string.IsNullOrWhiteSpace(snapshot.CommitSha) && snapshot.ObservedAt != null;
}

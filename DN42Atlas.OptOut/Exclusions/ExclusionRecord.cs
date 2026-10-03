namespace DN42Atlas.OptOut.Exclusions;

public enum ExclusionResourceType
{
    Domain,
    IPv4Prefix,
    IPv6Prefix
}

public sealed record NewExclusionRecord(
    ExclusionResourceType ResourceType,
    string ResourceValue,
    string Subject,
    string Maintainer,
    uint Asn,
    string RegistryCommitSha,
    DateTimeOffset RegistryObservedAtUtc,
    DateTimeOffset CreatedUtc);

public sealed record ExclusionRecord(
    long Id,
    ExclusionResourceType ResourceType,
    string ResourceValue,
    string Subject,
    string Maintainer,
    uint Asn,
    string RegistryCommitSha,
    DateTimeOffset RegistryObservedAtUtc,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? RevokedUtc);

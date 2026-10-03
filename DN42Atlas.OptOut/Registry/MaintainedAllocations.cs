namespace DN42Atlas.OptOut.Registry;

public sealed record MaintainedAllocations(
    IReadOnlyList<string> Ipv4Prefixes,
    IReadOnlyList<string> Ipv6Prefixes);

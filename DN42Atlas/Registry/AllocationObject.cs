namespace DN42Atlas.Registry;

public sealed class AllocationObject
{
    public required string Prefix { get; init; }

    public required AllocationAddressFamily AddressFamily { get; init; }

    public List<string> Maintainers { get; init; } = [];
}

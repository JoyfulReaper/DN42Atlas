namespace DN42Atlas.Registry;

public sealed class DomainObject
{
    public required string Domain { get; init; }
    public List<string> Maintainers { get; init; } = [];
    public List<string> NameServers { get; init; } = [];
}
using System.Net;

namespace DN42Atlas.Registry;

public enum ResolutionStatus
{
    Resolved,
    NotFound,
    TemporaryFailure,
    Error
}

public sealed class DomainResolution
{
    public required string Domain { get; init; }

    public ResolutionStatus Status { get; init; }

    public List<IPAddress> Addresses { get; init; } = [];

    public string? Error { get; init; }
}
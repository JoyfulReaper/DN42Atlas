namespace DN42Atlas.Registry;

public enum RobotsStatus
{
    NotChecked,
    NoRules,
    Allowed,
    Disallowed,
    Unavailable
}


public sealed class HttpProbeResult
{
    public string? RedirectLocation { get; init; }

    public bool? RobotsAllowed { get; init; }

    public int? RobotsStatusCode { get; init; }

    public required string Domain { get; init; }

    public required string Scheme { get; init; }

    public int Port { get; init; }

    public bool Reachable { get; init; }

    public int? StatusCode { get; init; }

    public RobotsStatus Robots { get; init; } = RobotsStatus.NotChecked;

    public string? Error { get; init; }
}
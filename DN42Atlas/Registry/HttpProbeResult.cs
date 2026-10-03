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
    public required string Domain { get; init; }
    public required string Scheme { get; init; }

    public int Port { get; init; }

    public bool Reachable { get; init; }

    //
    // Homepage
    //
    public int? StatusCode { get; init; }

    public string? ContentType { get; init; }

    public string? Title { get; init; }

    public string? HomepageRedirectLocation { get; init; }

    public string? HomepageError { get; init; }

    public bool ContentTruncated { get; init; }

    //
    // robots.txt
    //
    public RobotsStatus Robots { get; init; } =
        RobotsStatus.NotChecked;

    public int? RobotsStatusCode { get; init; }

    public bool? RobotsAllowed { get; init; }

    public string? RedirectLocation { get; init; }

    //
    // Discovery only.
    // Atlas does not automatically follow these.
    //
    public List<string> DiscoveredLinks { get; init; } = [];

    public bool LinksTruncated { get; init; }

    public List<string> Dn42Mentions { get; init; } = [];

    public bool Dn42MentionsTruncated { get; init; }

    public string? Error { get; init; }
}
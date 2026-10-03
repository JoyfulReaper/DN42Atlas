using DN42Atlas.Registry;

namespace DN42Atlas.Scanning;

public sealed record WebScanResult(
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    int DomainCount,
    List<string> SkippedExternal,
    int ExcludedByHostname,
    int ExcludedByPrefix,
    int ProbeTargetCount,
    int Reachable,
    List<HttpProbeResult> Results);
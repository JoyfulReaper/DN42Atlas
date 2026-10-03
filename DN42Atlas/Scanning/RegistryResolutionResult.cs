using DN42Atlas.Registry;

namespace DN42Atlas.Scanning;

public sealed record RegistryResolutionResult(int RegisteredDomainCount, List<DomainResolution> Resolutions);
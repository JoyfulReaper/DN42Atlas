namespace DN42Atlas.Policy;

public sealed class MissingProbeAddressProvenanceException() : IOException(
    "Cannot publish under prefix exclusions: a retained result lacks valid scan-time ProbeAddresses. A new scan with destination provenance is required.");

using System.Net;
using System.Diagnostics.CodeAnalysis;
using DN42Atlas.Policy;

namespace DN42Atlas.Networking;

public static class ProbeDestination
{
    public static bool IsDn42Hostname([NotNullWhen(true)] string? hostname) =>
        !string.IsNullOrWhiteSpace(hostname) &&
        hostname.EndsWith(".dn42", StringComparison.OrdinalIgnoreCase) &&
        Uri.CheckHostName(hostname) == UriHostNameType.Dns &&
        !hostname.Contains('@') && !hostname.Contains(':');

    public static bool IsAllowed(ExclusionPolicy policy, string hostname, IReadOnlyList<IPAddress> addresses) =>
        IsDn42Hostname(hostname) &&
        !policy.IsHostExcluded(hostname) &&
        addresses.Count > 0 &&
        addresses.All(address =>
            Dn42AddressSpace.Contains(address.ToString()) && !policy.IsAddressExcluded(address));
}

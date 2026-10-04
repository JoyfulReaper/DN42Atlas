using System.Net;

namespace DN42Atlas.Networking;

internal static class ProbeAddressProvenance
{
    public static string[] Normalize(IEnumerable<IPAddress> addresses) => addresses
        .Select(address => (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString())
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();
}

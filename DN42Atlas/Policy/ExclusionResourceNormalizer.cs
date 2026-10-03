using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace DN42Atlas.Policy;

public static class ExclusionResourceNormalizer
{
    public static string NormalizeDomain(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var normalized = value
            .Trim()
            .TrimEnd('.')
            .ToLowerInvariant();

        if (normalized.Contains('*') ||
            !normalized.EndsWith(".dn42", StringComparison.Ordinal) ||
            Uri.CheckHostName(normalized) != UriHostNameType.Dns ||
            normalized.Contains('@') ||
            normalized.Contains(':'))
        {
            throw new FormatException(
                "Self-service domain exclusions must be exact .dn42 hostnames.");
        }

        return normalized;
    }

    public static string NormalizePrefix(
        string value,
        AddressFamily? expectedFamily = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var parts = value.Split(
            '/',
            StringSplitOptions.TrimEntries);

        if (parts.Length != 2 ||
            !IPAddress.TryParse(parts[0], out var address) ||
            address.IsIPv4MappedToIPv6 ||
            address.AddressFamily is not (
                AddressFamily.InterNetwork or
                AddressFamily.InterNetworkV6) ||
            expectedFamily is not null &&
            address.AddressFamily != expectedFamily ||
            !int.TryParse(
                parts[1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var prefixLength))
        {
            throw new FormatException("Invalid exclusion prefix.");
        }

        var addressBytes = address.GetAddressBytes();
        var maximumPrefixLength = addressBytes.Length * 8;

        if (prefixLength < 0 || prefixLength > maximumPrefixLength)
            throw new FormatException("Invalid exclusion prefix length.");

        var networkBytes = new byte[addressBytes.Length];

        for (var index = 0; index < addressBytes.Length; index++)
        {
            var remainingBits = prefixLength - (index * 8);
            var mask = remainingBits switch
            {
                >= 8 => byte.MaxValue,
                <= 0 => (byte)0,
                _ => (byte)(byte.MaxValue << (8 - remainingBits))
            };

            networkBytes[index] = (byte)(addressBytes[index] & mask);
        }

        return $"{new IPAddress(networkBytes)}/{prefixLength}";
    }
}

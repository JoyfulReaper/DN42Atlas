using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace DN42Atlas.Registry;

public static class AllocationParser
{
    public static AllocationObject ParseIpv4(string path) =>
        Parse(
            path,
            "inetnum",
            AddressFamily.InterNetwork,
            AllocationAddressFamily.IPv4);

    public static AllocationObject ParseIpv6(string path) =>
        Parse(
            path,
            "inet6num",
            AddressFamily.InterNetworkV6,
            AllocationAddressFamily.IPv6);

    private static AllocationObject Parse(
        string path,
        string rangeAttribute,
        AddressFamily expectedAddressFamily,
        AllocationAddressFamily allocationAddressFamily)
    {
        string? range = null;
        string? cidr = null;
        var maintainers = new List<string>();

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var colonIndex = line.IndexOf(':');

            if (colonIndex <= 0)
                continue;

            var attribute = line[..colonIndex].Trim();
            var value = line[(colonIndex + 1)..].Trim();

            if (attribute == rangeAttribute)
            {
                if (range is not null)
                    throw Malformed(path);

                range = value;
            }
            else if (attribute == "cidr")
            {
                if (cidr is not null)
                    throw Malformed(path);

                cidr = value;
            }
            else if (attribute == "mnt-by")
            {
                maintainers.Add(value);
            }
        }

        if (!TryParsePrefix(
                cidr,
                expectedAddressFamily,
                out var prefix,
                out var firstAddress,
                out var lastAddress) ||
            !TryParseRange(
                range,
                expectedAddressFamily,
                out var rangeStart,
                out var rangeEnd) ||
            !firstAddress.SequenceEqual(rangeStart) ||
            !lastAddress.SequenceEqual(rangeEnd))
        {
            throw Malformed(path);
        }

        return new AllocationObject
        {
            Prefix = prefix,
            AddressFamily = allocationAddressFamily,
            Maintainers = maintainers
        };
    }

    private static bool TryParsePrefix(
        string? value,
        AddressFamily expectedAddressFamily,
        out string prefix,
        out byte[] firstAddress,
        out byte[] lastAddress)
    {
        prefix = string.Empty;
        firstAddress = [];
        lastAddress = [];

        if (string.IsNullOrWhiteSpace(value))
            return false;

        var parts = value.Split('/');

        if (parts.Length != 2 ||
            !IPAddress.TryParse(parts[0], out var address) ||
            address.AddressFamily != expectedAddressFamily ||
            !int.TryParse(
                parts[1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var prefixLength))
        {
            return false;
        }

        var addressBytes = address.GetAddressBytes();
        var maximumPrefixLength = addressBytes.Length * 8;

        if (prefixLength < 0 || prefixLength > maximumPrefixLength)
            return false;

        firstAddress = new byte[addressBytes.Length];
        lastAddress = new byte[addressBytes.Length];

        for (var index = 0; index < addressBytes.Length; index++)
        {
            var remainingBits = prefixLength - (index * 8);
            var mask = remainingBits switch
            {
                >= 8 => byte.MaxValue,
                <= 0 => (byte)0,
                _ => (byte)(byte.MaxValue << (8 - remainingBits))
            };

            firstAddress[index] = (byte)(addressBytes[index] & mask);
            lastAddress[index] = (byte)(firstAddress[index] | ~mask);
        }

        if (!addressBytes.SequenceEqual(firstAddress))
            return false;

        prefix = $"{address}/{prefixLength}";
        return true;
    }

    private static bool TryParseRange(
        string? value,
        AddressFamily expectedAddressFamily,
        out byte[] start,
        out byte[] end)
    {
        start = [];
        end = [];

        if (string.IsNullOrWhiteSpace(value))
            return false;

        var parts = value.Split(
            '-',
            StringSplitOptions.TrimEntries);

        if (parts.Length != 2 ||
            !IPAddress.TryParse(parts[0], out var startAddress) ||
            !IPAddress.TryParse(parts[1], out var endAddress) ||
            startAddress.AddressFamily != expectedAddressFamily ||
            endAddress.AddressFamily != expectedAddressFamily)
        {
            return false;
        }

        start = startAddress.GetAddressBytes();
        end = endAddress.GetAddressBytes();
        return true;
    }

    private static InvalidDataException Malformed(string path) =>
        new($"Malformed allocation object '{path}'.");
}

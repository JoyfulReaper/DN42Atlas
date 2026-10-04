using System.Net;
using System.Net.Sockets;

namespace DN42Atlas.Policy;

public sealed class ExclusionPolicy
{
    private readonly HashSet<string> _exactHosts =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<WildcardHostRule> _wildcardHosts = [];

    private readonly List<CidrRule> _prefixes = [];

    public IReadOnlyList<string> HostRules { get; }

    public IReadOnlyList<string> PrefixRules { get; }


    private ExclusionPolicy(
        IReadOnlyList<string> hostRules,
        IReadOnlyList<string> prefixRules)
    {
        HostRules = hostRules;
        PrefixRules = prefixRules;

        foreach (var rule in hostRules)
        {
            if (rule.StartsWith(
                "*.",
                StringComparison.Ordinal))
            {
                var suffix =
                    NormalizeHostname(
                        rule[2..]);

                if (string.IsNullOrWhiteSpace(suffix))
                {
                    throw new FormatException(
                        $"Invalid hostname exclusion: {rule}");
                }

                _wildcardHosts.Add(
                    new WildcardHostRule(
                        rule,
                        suffix));

                continue;
            }

            var hostname =
                NormalizeHostname(rule);

            if (string.IsNullOrWhiteSpace(hostname))
            {
                throw new FormatException(
                    $"Invalid hostname exclusion: {rule}");
            }

            _exactHosts.Add(hostname);
        }

        foreach (var rule in prefixRules)
        {
            _prefixes.Add(
                CidrRule.Parse(rule));
        }
    }


    public static ExclusionPolicy Load(
        string hostsPath,
        string prefixesPath,
        string? runtimeBundlePath = null)
    {
        var hostRules =
            ReadRequiredRuleFile(hostsPath);

        var prefixRules =
            ReadRequiredRuleFile(prefixesPath);

        if (string.IsNullOrWhiteSpace(runtimeBundlePath))
        {
            return new ExclusionPolicy(
                hostRules,
                prefixRules);
        }

        var runtimeRules =
            RuntimeExclusionBundle.Load(
                runtimeBundlePath);

        return new ExclusionPolicy(
            hostRules
                .Concat(runtimeRules.HostRules)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            prefixRules
                .Concat(runtimeRules.PrefixRules)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList());
    }


    public bool IsHostExcluded(
        string hostname)
    {
        return IsHostExcluded(
            hostname,
            out _);
    }


    public bool IsHostExcluded(
        string hostname,
        out string? matchedRule)
    {
        matchedRule = null;

        var normalized =
            NormalizeHostname(hostname);

        if (_exactHosts.Contains(normalized))
        {
            matchedRule =
                HostRules.First(
                    rule =>
                        !rule.StartsWith(
                            "*.",
                            StringComparison.Ordinal) &&
                        string.Equals(
                            NormalizeHostname(rule),
                            normalized,
                            StringComparison.OrdinalIgnoreCase));

            return true;
        }

        foreach (var wildcard in
            _wildcardHosts)
        {
            //
            // *.example.dn42 matches:
            //
            // foo.example.dn42
            // bar.foo.example.dn42
            //
            // but does not match:
            //
            // example.dn42
            //
            if (normalized.EndsWith(
                    "." + wildcard.Suffix,
                    StringComparison.OrdinalIgnoreCase))
            {
                matchedRule =
                    wildcard.Rule;

                return true;
            }
        }

        return false;
    }


    public bool IsAddressExcluded(
        string address)
    {
        return IsAddressExcluded(
            address,
            out _);
    }


    public bool IsAddressExcluded(
        string address,
        out string? matchedRule)
    {
        matchedRule = null;

        if (!IPAddress.TryParse(
            address,
            out var parsedAddress))
        {
            return false;
        }

        return IsAddressExcluded(
            parsedAddress,
            out matchedRule);
    }


    public bool IsAddressExcluded(
        IPAddress address)
    {
        return IsAddressExcluded(
            address,
            out _);
    }


    public bool IsAddressExcluded(
        IPAddress address,
        out string? matchedRule)
    {
        matchedRule = null;

        if (address.IsIPv4MappedToIPv6)
        {
            address =
                address.MapToIPv4();
        }

        foreach (var prefix in
            _prefixes)
        {
            if (!prefix.Contains(address))
                continue;

            matchedRule =
                prefix.Rule;

            return true;
        }

        return false;
    }


    public bool IsPrefixExcluded(string prefix)
    {
        var candidate = CidrRule.Parse(prefix);
        return _prefixes.Any(rule => rule.Contains(candidate));
    }

    private static IReadOnlyList<string>
        ReadRequiredRuleFile(
            string path)
    {
        if (!File.Exists(path))
        {
            //
            // Deliberately fail closed.
            //
            // A scheduled crawler should not suddenly
            // ignore operator opt-outs just because a
            // config file disappeared during deployment.
            //
            throw new FileNotFoundException(
                $"Required exclusion file not found: {path}",
                path);
        }

        return File.ReadLines(path)
            .Select(
                line =>
                {
                    var commentIndex =
                        line.IndexOf('#');

                    if (commentIndex >= 0)
                    {
                        line =
                            line[..commentIndex];
                    }

                    return line.Trim();
                })
            .Where(
                line =>
                    !string.IsNullOrWhiteSpace(line))
            .Distinct(
                StringComparer.OrdinalIgnoreCase)
            .ToList();
    }


    private static string NormalizeHostname(
        string hostname)
    {
        return hostname
            .Trim()
            .TrimEnd('.')
            .ToLowerInvariant();
    }


    private sealed record WildcardHostRule(
        string Rule,
        string Suffix);


    private sealed class CidrRule
    {
        private readonly byte[] _networkBytes;

        private readonly AddressFamily _family;

        private readonly int _prefixLength;


        private CidrRule(
            string rule,
            IPAddress network,
            int prefixLength)
        {
            Rule = rule;

            if (network.IsIPv4MappedToIPv6)
            {
                network =
                    network.MapToIPv4();
            }

            _family =
                network.AddressFamily;

            _networkBytes =
                network.GetAddressBytes();

            _prefixLength =
                prefixLength;
        }


        public string Rule { get; }


        public static CidrRule Parse(
            string rule)
        {
            var parts =
                rule.Split(
                    '/',
                    StringSplitOptions.TrimEntries);

            if (parts.Length != 2)
            {
                throw new FormatException(
                    $"Invalid CIDR exclusion: {rule}");
            }

            if (!IPAddress.TryParse(
                parts[0],
                out var network))
            {
                throw new FormatException(
                    $"Invalid IP address in exclusion: {rule}");
            }

            if (network.IsIPv4MappedToIPv6)
            {
                network =
                    network.MapToIPv4();
            }

            if (!int.TryParse(
                parts[1],
                out var prefixLength))
            {
                throw new FormatException(
                    $"Invalid prefix length in exclusion: {rule}");
            }

            var maximumPrefixLength =
                network.AddressFamily ==
                AddressFamily.InterNetwork
                    ? 32
                    : 128;

            if (prefixLength < 0 ||
                prefixLength > maximumPrefixLength)
            {
                throw new FormatException(
                    $"Prefix length out of range in exclusion: {rule}");
            }

            return new CidrRule(
                rule,
                network,
                prefixLength);
        }


        public bool Contains(CidrRule other) =>
            _family == other._family && _prefixLength <= other._prefixLength &&
            Contains(new IPAddress(other._networkBytes));

        public bool Contains(
            IPAddress address)
        {
            if (address.IsIPv4MappedToIPv6)
            {
                address =
                    address.MapToIPv4();
            }

            if (address.AddressFamily !=
                _family)
            {
                return false;
            }

            var addressBytes =
                address.GetAddressBytes();

            var wholeBytes =
                _prefixLength / 8;

            var remainingBits =
                _prefixLength % 8;

            for (var i = 0;
                 i < wholeBytes;
                 i++)
            {
                if (_networkBytes[i] !=
                    addressBytes[i])
                {
                    return false;
                }
            }

            if (remainingBits == 0)
            {
                return true;
            }

            var mask =
                (byte)(
                    0xff <<
                    (8 - remainingBits));

            return
                (_networkBytes[wholeBytes] & mask) ==
                (addressBytes[wholeBytes] & mask);
        }
    }
}

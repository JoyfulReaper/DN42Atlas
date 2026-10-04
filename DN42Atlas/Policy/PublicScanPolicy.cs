using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Net;

namespace DN42Atlas.Policy;

public sealed class PublicScanPolicy(ExclusionPolicy exclusions)
{
    private static readonly Regex References = new(
        @"(?:[A-Za-z0-9_-]+\.)+[A-Za-z0-9_-]+\.?|(?:[A-Fa-f0-9]{0,4}:){2,}[A-Fa-f0-9:.%]*",
        RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    public void Apply(JsonNode scan)
    {
        if (scan is JsonObject root && root["Results"] is JsonArray results)
        {
            var removedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var removedPrefixHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = results.Count - 1; index >= 0; index--)
            {
                if (results[index] is JsonObject result &&
                    result["Domain"] is JsonValue domainValue &&
                    domainValue.TryGetValue<string>(out var domain) && exclusions.IsHostExcluded(domain))
                {
                    results.RemoveAt(index);
                    removedHosts.Add(domain);
                    continue;
                }
                if (exclusions.PrefixRules.Count == 0) continue;
                // Only recorded scan-time evidence can establish a historical destination.
                // A host-excluded row is already gone and needs no prefix evidence.
                if (results[index] is not JsonObject candidate ||
                    candidate["ProbeAddresses"] is not JsonArray addresses || addresses.Count == 0)
                    throw new MissingProbeAddressProvenanceException();
                var matches = false;
                foreach (var address in addresses)
                {
                    if (address is not JsonValue scalar || !scalar.TryGetValue<string>(out var text) ||
                        !IPAddress.TryParse(text, out var parsed))
                        throw new MissingProbeAddressProvenanceException();
                    // IsAddressExcluded explicitly maps IPv4-mapped IPv6 to IPv4.
                    matches |= exclusions.IsAddressExcluded(parsed);
                }
                if (matches)
                {
                    results.RemoveAt(index);
                    if (candidate["Domain"] is JsonValue name && name.TryGetValue<string>(out var hostname))
                        removedPrefixHosts.Add(hostname);
                }
            }
            if (removedHosts.Count > 0 && root["ExcludedByHostname"] is JsonValue count && count.TryGetValue<int>(out var previous))
                root["ExcludedByHostname"] = previous + removedHosts.Count;
            if (removedPrefixHosts.Count > 0 && root["ExcludedByPrefix"] is JsonValue prefixCount && prefixCount.TryGetValue<int>(out var previousPrefix))
                root["ExcludedByPrefix"] = previousPrefix + removedPrefixHosts.Count;
        }
        Filter(scan);
    }

    private void Filter(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(pair => pair.Key).ToArray())
            {
                if (obj[key] is JsonArray references && key is "DiscoveredLinks" or "Dn42Mentions" or "SkippedExternalOrMixedDomains")
                {
                    for (var index = references.Count - 1; index >= 0; index--)
                    {
                        if (references[index] is JsonValue value && value.TryGetValue<string>(out var text) &&
                            (Redact(text) != text || (key == "DiscoveredLinks" && !IsSafeLink(text))))
                            references.RemoveAt(index);
                    }
                }
                if (obj[key] is JsonValue scalar && scalar.TryGetValue<string>(out var original))
                    obj[key] = Redact(original);
                else if (obj[key] is JsonNode child)
                    Filter(child);
            }
        }
        else if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                if (array[index] is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                    array[index] = Redact(text);
                else if (array[index] is JsonNode child)
                    Filter(child);
            }
        }
    }

    private string Redact(string text)
    {
        foreach (var rule in exclusions.HostRules.Where(rule => rule.StartsWith("*.", StringComparison.Ordinal)).Concat(exclusions.PrefixRules))
            text = text.Replace(rule, "[excluded]", StringComparison.OrdinalIgnoreCase);

        return References.Replace(text, match =>
            exclusions.IsHostExcluded(match.Value) || exclusions.IsAddressExcluded(match.Value.TrimEnd('.'))
                ? "[excluded]" : match.Value);
    }

    private bool IsSafeLink(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme is "http" or "https" or "gopher" or "gemini") &&
        !exclusions.IsHostExcluded(uri.IdnHost) &&
        !exclusions.IsAddressExcluded(uri.Host.Trim('[', ']'));
}

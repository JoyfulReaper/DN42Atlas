using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DN42Atlas.Policy;
using DN42Atlas.Registry;

namespace DN42Atlas.Reporting;

public static class AtlasReportGenerator
{
    private static readonly string Template = LoadTemplate();

    public static async Task GenerateAsync(
        string inputPath,
        string outputPath,
        ExclusionPolicy exclusionPolicy,
        CancellationToken cancellationToken = default)
    {
        var scanJson = await File.ReadAllTextAsync(inputPath, cancellationToken);
        var publicScan = JsonNode.Parse(scanJson) ?? throw new InvalidDataException("Scan JSON must not be null.");
        try { new PublicScanPolicy(exclusionPolicy).Apply(publicScan); }
        catch (MissingProbeAddressProvenanceException)
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!Path.GetFullPath(inputPath).Equals(Path.GetFullPath(outputPath), comparison))
                File.Delete(outputPath);
            throw;
        }

        await File.WriteAllTextAsync(outputPath, GenerateHtml(publicScan), Encoding.UTF8, cancellationToken);
    }

    // The caller owns policy filtering. Render only explicit public fields; never use raw provenance.
    public static string GenerateHtml(JsonNode publicScan)
    {
        var entries = (publicScan["Results"] as JsonArray ?? [])
            .OfType<JsonObject>().Select(CreateEntry)
            .OrderBy(entry => entry.Origin, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Id, StringComparer.Ordinal).ToArray();
        var seeds = entries.Select(entry => NormalizeHost(entry.Domain)).ToHashSet(StringComparer.Ordinal);
        var entriesByHost = entries.GroupBy(entry => NormalizeHost(entry.Domain))
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var allLinks = new HashSet<string>(StringComparer.Ordinal);
        var discovered = new SortedDictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            foreach (var link in Strings(entry.Result, "DiscoveredLinks"))
            {
                allLinks.Add(link);
                if (SafeLink(link, out var uri)) AddHost(uri!.IdnHost, entry.Origin);
            }
            foreach (var host in Strings(entry.Result, "Dn42Mentions")) AddHost(host, entry.Origin);
        }

        void AddHost(string host, string source)
        {
            host = NormalizeHost(host);
            if (!host.EndsWith(".dn42", StringComparison.Ordinal)) return;
            if (!discovered.TryGetValue(host, out var sources)) discovered[host] = sources = [];
            sources.Add(source);
        }

        var outside = discovered.Where(pair => !seeds.Contains(pair.Key)).ToArray();
        var summary = new StringBuilder();
        Stat(summary, entries.Length, "public service results");
        Stat(summary, entries.Count(entry => Flag(entry.Result, "Reachable")), "reachable public origins");
        Stat(summary, seeds.Count, "public seed domains");
        Stat(summary, allLinks.Count, "unique discovered links");
        Stat(summary, discovered.Count, "discovered DN42 hosts");
        Stat(summary, outside.Length, "hosts outside seed set");
        // Historical scan totals can include rows removed by later exclusions; label them accordingly.
        OptionalStat(summary, publicScan, "ResolvedDomainsProbed", "domains probed at scan time");
        OptionalStat(summary, publicScan, "ProbeTargetCount", "probes at scan time");
        OptionalStat(summary, publicScan, "ExcludedByHostname", "domains excluded by hostname");
        OptionalStat(summary, publicScan, "ExcludedByPrefix", "domains excluded by prefix");

        var scanTimes = new StringBuilder();
        if (Timestamp(publicScan, "StartedAt") is { } started)
            scanTimes.Append($"<p>Scan started: {TimeHtml(started)}</p>");
        if (Timestamp(publicScan, "FinishedAt") is { } finished)
            scanTimes.Append($"<p>Scan finished: {TimeHtml(finished)}</p>");
        var duration = Number(publicScan, "DurationSeconds");
        if (duration == null && Timestamp(publicScan, "StartedAt") is { } start &&
            Timestamp(publicScan, "FinishedAt") is { } end && end >= start)
            duration = (end - start).TotalSeconds;
        if (duration is >= 0)
            scanTimes.Append($"<p>Scan duration: {E(duration.Value.ToString("0.##", CultureInfo.InvariantCulture))} seconds</p>");

        var rows = new StringBuilder();
        var details = new StringBuilder();
        foreach (var entry in entries)
        {
            var result = entry.Result;
            var title = Text(result, "Title");
            var hasTitle = !string.IsNullOrWhiteSpace(title);
            if (!hasTitle) title = "(no title)";
            var robots = RobotsName(result);
            var links = Strings(result, "DiscoveredLinks");
            var hasResponse = result["StatusCode"] is JsonValue status && status.TryGetValue<int>(out var statusCode) &&
                statusCode is >= 100 and <= 599;
            var standardPort = entry.Scheme == "http" && entry.Port == 80 || entry.Scheme == "https" && entry.Port == 443;
            var portKind = entry.Scheme is "http" or "https" && entry.Port is >= 1 and <= 65535
                ? (standardPort ? "standard" : "other") : "";
            var searchable = $"{entry.Domain} {entry.Origin} {title} {Text(result, "ContentType")}";
            rows.Append($"""
                <tr data-service="{entry.Id}" data-search="{E(searchable)}" data-scheme="{E(entry.Scheme)}" data-robots="{E(robots)}" data-reachable="{(Flag(result, "Reachable") ? "true" : "false")}"
                    data-port="{entry.Port}" data-response="{(hasResponse ? "true" : "false")}" data-title="{(hasTitle ? "true" : "false")}" data-links="{(links.Length > 0 ? "true" : "false")}" data-port-kind="{portKind}" role="row">
                    <th scope="row" role="rowheader" id="origin-{entry.Id}" headers="column-origin"><span class="cell-label" aria-hidden="true">Origin</span><span class="cell-value">{OriginHtml(entry)}</span></th>
                    <td role="cell" headers="column-title origin-{entry.Id}"><span class="cell-label" aria-hidden="true">Title</span><span class="cell-value">{E(title)}</span><button class="row-details" type="button" aria-label="View details for {E(entry.Origin)}" aria-controls="{entry.Id}" hidden>View details</button></td>
                    <td role="cell" headers="column-scheme origin-{entry.Id}"><span class="cell-label" aria-hidden="true">Scheme / port</span><span class="cell-value">{E(entry.Scheme.ToUpperInvariant())} / {entry.Port}</span></td>
                    <td role="cell" headers="column-status origin-{entry.Id}"><span class="cell-label" aria-hidden="true">Status</span><span class="cell-value">{E(ServiceStatus(result))}<br><span class="muted">robots: {E(robots)}</span></span></td>
                </tr>
                """);
            details.Append($"""
                <article class="card service-detail" id="{entry.Id}" tabindex="-1" aria-labelledby="title-{entry.Id}">
                    <p class="origin">{OriginHtml(entry)}</p>
                    <h3 id="title-{entry.Id}">{E(title)}</h3>
                    <div class="meta"><span class="badge">{E(ServiceStatus(result))}</span>
                    <span class="badge">{E(entry.Scheme.ToUpperInvariant())} / {entry.Port}</span>
                    <span class="badge">{E(Text(result, "ContentType") ?? "unknown content type")}</span>
                    <span class="badge">robots: {E(robots)}</span></div>
                """);
            Field(details, result, "RobotsStatusCode", "robots.txt HTTP status");
            Field(details, result, "RobotsAllowed", "Homepage allowed by robots");
            Field(details, result, "RedirectLocation", "robots.txt redirect");
            Field(details, result, "HomepageRedirectLocation", "Homepage redirect");
            Field(details, result, "HomepageError", "Homepage error");
            Field(details, result, "Error", "Probe error");
            if (Flag(result, "ContentTruncated")) details.Append("<p class=\"warn\">Homepage body was truncated during discovery.</p>");
            details.Append($"<details><summary>Discovered links ({links.Length}{(Flag(result, "LinksTruncated") ? "+" : "")})</summary>");
            if (links.Length == 0) details.Append("<p class=\"muted\">No links discovered.</p>");
            else
            {
                details.Append("<ul>");
                foreach (var link in links)
                {
                    var safe = SafeLink(link, out var uri);
                    var kind = uri?.Scheme is "gopher" or "gemini" ? uri.Scheme :
                        uri?.IdnHost.EndsWith(".dn42", StringComparison.OrdinalIgnoreCase) == true ? "dn42" : "clearnet";
                    details.Append($"<li><span class=\"badge {kind}\">{kind}</span> ");
                    details.Append(safe ? ExternalLink(link, link) : E(link));
                    details.Append("</li>");
                }
                details.Append("</ul>");
            }
            details.Append("</details>");
            var mentions = Strings(result, "Dn42Mentions");
            details.Append($"<details><summary>DN42 mentions ({mentions.Length}{(Flag(result, "Dn42MentionsTruncated") ? "+" : "")})</summary>");
            if (mentions.Length == 0) details.Append("<p class=\"muted\">No DN42 names mentioned.</p>");
            else
            {
                details.Append("<ul>");
                foreach (var host in mentions) details.Append($"<li>{HostHtml(host, entriesByHost)}</li>");
                details.Append("</ul>");
            }
            details.Append("</details><p><a href=\"#directory\">Back to directory</a></p></article>");
        }

        var hostsHtml = new StringBuilder();
        HostGroup(hostsHtml, "Discovered hosts outside seed set", outside, entriesByHost);
        // These hosts already have scan results, so offer direct navigation to their services.
        HostGroup(hostsHtml, "Discovered hosts in seed set", discovered.Where(pair => seeds.Contains(pair.Key)).ToArray(), entriesByHost);
        var values = new Dictionary<string, string>
        {
            ["summary"] = summary.ToString(), ["scanTimes"] = scanTimes.ToString(),
            ["rows"] = rows.ToString(), ["details"] = details.ToString(), ["hosts"] = hostsHtml.ToString(),
            ["empty"] = entries.Length == 0 ? "<p>No public service results.</p>" : "",
            ["lastScan"] = Timestamp(publicScan, "GeneratedAt") is { } timestamp
                ? $"<p class=\"last-scan\">Last scan: {TimeHtml(timestamp)}</p>" : "",
            // Keep the safely serialized public snapshot for offline inspection and existing consumers.
            ["scanJson"] = publicScan.ToJsonString()
        };
        // One pass: scan values resembling placeholders must never be interpreted as template material.
        return Regex.Replace(Template, @"\{\{(\w+)\}\}", match => values[match.Groups[1].Value]);
    }

    private sealed record Entry(JsonObject Result, string Domain, string Scheme, int Port, string Origin, string Id);

    private static Entry CreateEntry(JsonObject result)
    {
        var domain = Text(result, "Domain") ?? "(unknown hostname)";
        var scheme = (Text(result, "Scheme") ?? "unknown").ToLowerInvariant();
        var port = result["Port"] is JsonValue value && value.TryGetValue<int>(out var number) ? number : 0;
        var defaultPort = scheme == "http" && port == 80 || scheme == "https" && port == 443;
        var origin = $"{scheme}://{domain}{(defaultPort ? "" : ":" + port.ToString(CultureInfo.InvariantCulture))}/";
        // Normalize hostname case/trailing dot. IDs never depend on ordering, title, status or scan time.
        var identity = $"{scheme}\n{NormalizeHost(domain)}\n{port.ToString(CultureInfo.InvariantCulture)}";
        var id = "service-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return new Entry(result, domain, scheme, port, origin, id);
    }

    private static string ServiceStatus(JsonNode result)
    {
        if (!Flag(result, "Reachable")) return "Unreachable";
        if (result["StatusCode"] is JsonValue status && status.TryGetValue<int>(out var code)) return $"Reachable · HTTP {code}";
        if ((result["RobotsAllowed"] is JsonValue allowed && allowed.TryGetValue<bool>(out var canFetch) && !canFetch) ||
            RobotsName(result) is "Disallowed" or "Unavailable")
            return "Reachable · homepage not fetched / robots restricted";
        return Text(result, "HomepageError") is { Length: > 0 }
            ? "Reachable · homepage fetch failed (no HTTP status)"
            : "Reachable · homepage not fetched (no HTTP status)";
    }

    private static string RobotsName(JsonNode result)
    {
        if (result["Robots"] is JsonValue value)
        {
            if (value.TryGetValue<int>(out var number) && Enum.IsDefined(typeof(RobotsStatus), number))
                return ((RobotsStatus)number).ToString();
            if (value.TryGetValue<string>(out var text)) return text;
        }
        return "NotChecked";
    }

    private static string OriginHtml(Entry entry) =>
        entry.Scheme is "http" or "https" && SafeLink(entry.Origin, out var uri) &&
        uri!.UserInfo.Length == 0 && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        uri.Port == entry.Port && NormalizeHost(uri.Host.Trim('[', ']')) == NormalizeHost(entry.Domain.Trim('[', ']'))
            ? ExternalLink(entry.Origin, entry.Origin) : E(entry.Origin);

    private static bool SafeLink(string link, out Uri? uri) =>
        Uri.TryCreate(link, UriKind.Absolute, out uri) && uri.IsWellFormedOriginalString() &&
        uri.Scheme is "http" or "https" or "gopher" or "gemini";

    private static string ExternalLink(string href, string label) =>
        $"<a href=\"{E(href)}\" target=\"_blank\" rel=\"noopener noreferrer\">{E(label)}</a>";

    private static string HostHtml(string host, IReadOnlyDictionary<string, Entry[]> entriesByHost)
    {
        if (!entriesByHost.TryGetValue(NormalizeHost(host), out var matches)) return E(host);
        return E(host) + " · " + string.Join(" · ", matches.Select(entry =>
            $"<a href=\"#{entry.Id}\">{E(entry.Scheme.ToUpperInvariant())}:{entry.Port}</a>"));
    }

    private static void HostGroup(StringBuilder html, string label, KeyValuePair<string, HashSet<string>>[] hosts, IReadOnlyDictionary<string, Entry[]> entriesByHost)
    {
        html.Append($"<details><summary>{label} ({hosts.Length})</summary>");
        if (hosts.Length == 0) html.Append("<p class=\"muted\">None.</p>");
        foreach (var (host, sources) in hosts)
        {
            html.Append($"<div class=\"host-row\">{HostHtml(host, entriesByHost)}<div class=\"muted\">discovered from {sources.Count} origin(s)</div><details><summary>Sources (up to 10)</summary><ul>");
            foreach (var source in sources.Order(StringComparer.Ordinal).Take(10)) html.Append($"<li>{E(source)}</li>");
            html.Append("</ul></details></div>");
        }
        html.Append("</details>");
    }

    private static void Field(StringBuilder html, JsonNode result, string key, string label)
    {
        if (result[key] is JsonValue value) html.Append($"<p><strong>{label}:</strong> {E(value.ToString())}</p>");
    }

    private static void Stat(StringBuilder html, int count, string label) =>
        html.Append($"<div class=\"stat\"><div class=\"stat-value\">{count}</div><div class=\"stat-label\">{label}</div></div>");

    private static void OptionalStat(StringBuilder html, JsonNode scan, string key, string label)
    {
        if (scan[key] is JsonValue value && value.TryGetValue<int>(out var count) && count >= 0) Stat(html, count, label);
    }

    private static string E(string? value) => WebUtility.HtmlEncode(value ?? "");
    private static string NormalizeHost(string host) => host.Trim().TrimEnd('.').ToLowerInvariant();
    private static string? Text(JsonNode node, string key) =>
        node[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static bool Flag(JsonNode node, string key) =>
        node[key] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
    private static string[] Strings(JsonNode node, string key) =>
        (node[key] as JsonArray ?? []).OfType<JsonValue>().Select(value =>
            value.TryGetValue<string>(out var text) ? text : null).OfType<string>().ToArray();
    private static double? Number(JsonNode node, string key) =>
        node[key] is JsonValue value && value.TryGetValue<double>(out var number) && double.IsFinite(number) ? number : null;
    private static DateTimeOffset? Timestamp(JsonNode node, string key) =>
        DateTimeOffset.TryParse(Text(node, key), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp)
            ? timestamp.ToUniversalTime() : null;
    private static string TimeHtml(DateTimeOffset time) =>
        $"<time datetime=\"{E(time.ToString("O", CultureInfo.InvariantCulture))}\">{E(time.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture))}</time>";

    private static string LoadTemplate()
    {
        using var stream = typeof(AtlasReportGenerator).Assembly.GetManifestResourceStream("DN42Atlas.Reporting.viewer.html")
            ?? throw new InvalidOperationException("Bundled viewer template is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

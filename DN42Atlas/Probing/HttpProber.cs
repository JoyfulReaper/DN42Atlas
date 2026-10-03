using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using DN42Atlas.Registry;
using DN42Atlas.Networking;
using DN42Atlas.Policy;

namespace DN42Atlas.Probing;

public static class HttpProber
{
    private const string UserAgent =
        "DN42Atlas/0.2 (+https://dn42atlas.dn42/)";

    private const int MaxRobotsBytes = 64 * 1024;
    private const int MaxHomepageBytes = 256 * 1024;

    private const int MaxDiscoveredLinks = 250;
    private const int MaxDn42Mentions = 250;

    private static readonly Regex TitleRegex = new(
        @"<title\b[^>]*>(?<title>.*?)</title>",
        RegexOptions.IgnoreCase |
        RegexOptions.Singleline |
        RegexOptions.Compiled);

    private static readonly Regex HrefRegex = new(
        """href\s*=\s*["'](?<url>[^"'<>]+)["']""",
        RegexOptions.IgnoreCase |
        RegexOptions.Compiled);

    private static readonly Regex PlainUrlRegex = new(
        """(?<url>(?:https?|gopher|gemini)://[^\s"'<>]+)""",
        RegexOptions.IgnoreCase |
        RegexOptions.Compiled);

    private static readonly Regex Dn42NameRegex = new(
        @"(?<![A-Za-z0-9-])(?:[A-Za-z0-9-]+\.)+dn42\b",
        RegexOptions.IgnoreCase |
        RegexOptions.Compiled);

    public static async Task<HttpProbeResult> ProbeAsync(
        string domain,
        string scheme,
        int port,
        ExclusionPolicy exclusionPolicy,
        IReadOnlyList<IPAddress> addresses,
        CancellationToken cancellationToken = default)
    {
        var approvedAddresses = addresses.ToArray();
        if (!ProbeDestination.IsAllowed(exclusionPolicy, domain, approvedAddresses))
            throw new InvalidOperationException("Probe destination is excluded or outside DN42.");

        using var client = new HttpClient(PinnedHttpConnection.CreateHandler(domain, approvedAddresses))
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        return await ProbeAsync(domain, scheme, port, client, TimeSpan.FromSeconds(10), cancellationToken);
    }

    // Tests supply responses here; production always uses the policy-checked pinned transport above.
    internal static async Task<HttpProbeResult> ProbeAsync(
        string domain, string scheme, int port, HttpClient client, TimeSpan requestTimeout,
        CancellationToken cancellationToken = default)
    {

        var defaultPort =
            (scheme == "http" && port == 80) ||
            (scheme == "https" && port == 443);

        var authority = defaultPort
            ? domain
            : $"{domain}:{port}";

        var origin =
            new Uri($"{scheme}://{authority}/");

        var robotsUri =
            new Uri(origin, "robots.txt");

        RobotsStatus robotsStatus;
        int? robotsStatusCode;
        bool? robotsAllowed;
        string? robotsRedirect;

        //
        // robots.txt first.
        //
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(requestTimeout);
            using var request =
                new HttpRequestMessage(
                    HttpMethod.Get,
                    robotsUri);

            AddAtlasHeaders(request);

            using var response =
                await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    deadline.Token);

            robotsStatusCode =
                (int)response.StatusCode;

            robotsRedirect =
                response.Headers.Location?.ToString();

            if (response.IsSuccessStatusCode)
            {
                var robotsBody =
                    await ReadTextLimitedAsync(
                        response.Content,
                        MaxRobotsBytes,
                        deadline.Token);

                robotsAllowed = robotsBody.Truncated ? null :
                    IsPathAllowed(
                        robotsBody.Text,
                        "DN42Atlas",
                        "/");

                robotsStatus =
                    robotsAllowed == null ? RobotsStatus.Unavailable : robotsAllowed.Value
                        ? RobotsStatus.Allowed
                        : RobotsStatus.Disallowed;
            }
            else if (
                response.StatusCode ==
                HttpStatusCode.NotFound)
            {
                robotsStatus =
                    RobotsStatus.NoRules;

                robotsAllowed = true;
            }
            else
            {
                robotsStatus =
                    RobotsStatus.Unavailable;

                robotsAllowed = null;
            }
        }
        catch (Exception ex) when (
            ex is HttpRequestException or
            OperationCanceledException or IOException)
        {
            return new HttpProbeResult
            {
                Domain = domain,
                Scheme = scheme,
                Port = port,

                Reachable = false,

                Robots =
                    RobotsStatus.Unavailable,

                Error = ex.Message
            };
        }

        //
        // We talked HTTP successfully, but don't fetch /
        // unless robots explicitly permits it.
        //
        if (robotsAllowed != true)
        {
            return new HttpProbeResult
            {
                Domain = domain,
                Scheme = scheme,
                Port = port,

                Reachable = true,

                Robots = robotsStatus,

                RobotsStatusCode =
                    robotsStatusCode,

                RobotsAllowed =
                    robotsAllowed,

                RedirectLocation =
                    robotsRedirect
            };
        }

        //
        // Fetch exactly one page: /
        //
        // Redirects are recorded, never followed.
        //
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(requestTimeout);
            using var request =
                new HttpRequestMessage(
                    HttpMethod.Get,
                    origin);

            AddAtlasHeaders(request);

            using var response =
                await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    deadline.Token);

            var statusCode =
                (int)response.StatusCode;

            var contentType =
                response.Content.Headers
                    .ContentType?
                    .MediaType;

            var homepageRedirect =
                response.Headers
                    .Location?
                    .ToString();

            string? title = null;

            var discoveredLinks =
                new List<string>();

            var linksTruncated = false;

            var dn42Mentions =
                new List<string>();

            var dn42MentionsTruncated = false;

            var contentTruncated = false;

            if (response.IsSuccessStatusCode &&
                IsTextLike(contentType))
            {
                var body =
                    await ReadTextLimitedAsync(
                        response.Content,
                        MaxHomepageBytes,
                        deadline.Token);

                contentTruncated =
                    body.Truncated;

                title =
                    ExtractTitle(body.Text);

                var links =
                    ExtractLinks(
                        body.Text,
                        origin);

                discoveredLinks =
                    links.Items;

                linksTruncated =
                    links.Truncated;

                var mentions =
                    ExtractDn42Mentions(
                        body.Text);

                dn42Mentions =
                    mentions.Items;

                dn42MentionsTruncated =
                    mentions.Truncated;
            }

            return new HttpProbeResult
            {
                Domain = domain,
                Scheme = scheme,
                Port = port,

                Reachable = true,

                StatusCode =
                    statusCode,

                ContentType =
                    contentType,

                Title =
                    title,

                HomepageRedirectLocation =
                    homepageRedirect,

                ContentTruncated =
                    contentTruncated,

                Robots =
                    robotsStatus,

                RobotsStatusCode =
                    robotsStatusCode,

                RobotsAllowed =
                    robotsAllowed,

                RedirectLocation =
                    robotsRedirect,

                DiscoveredLinks =
                    discoveredLinks,

                LinksTruncated =
                    linksTruncated,

                Dn42Mentions =
                    dn42Mentions,

                Dn42MentionsTruncated =
                    dn42MentionsTruncated
            };
        }
        catch (Exception ex) when (
            ex is HttpRequestException or
            OperationCanceledException or IOException)
        {
            return new HttpProbeResult
            {
                Domain = domain,
                Scheme = scheme,
                Port = port,

                Reachable = true,

                Robots =
                    robotsStatus,

                RobotsStatusCode =
                    robotsStatusCode,

                RobotsAllowed =
                    robotsAllowed,

                RedirectLocation =
                    robotsRedirect,

                HomepageError =
                    ex.Message
            };
        }
    }

    private static void AddAtlasHeaders(
        HttpRequestMessage request)
    {
        //
        // TryAddWithoutValidation avoids ProductInfoHeaderValue
        // getting picky about our contact URL.
        //
        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            UserAgent);
    }

    private static bool IsTextLike(
        string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return true;

        return
            contentType.StartsWith(
                "text/",
                StringComparison.OrdinalIgnoreCase) ||

            contentType.Contains(
                "html",
                StringComparison.OrdinalIgnoreCase) ||

            contentType.Contains(
                "xml",
                StringComparison.OrdinalIgnoreCase) ||

            contentType.Contains(
                "json",
                StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractTitle(
        string text)
    {
        var match =
            TitleRegex.Match(text);

        if (!match.Success)
            return null;

        var title =
            WebUtility.HtmlDecode(
                match.Groups["title"].Value);

        title =
            Regex.Replace(
                title,
                @"\s+",
                " ")
            .Trim();

        if (title.Length == 0)
            return null;

        if (title.Length > 200)
            title = title[..200];

        return title;
    }

    private static (
        List<string> Items,
        bool Truncated) ExtractLinks(
            string text,
            Uri sourceUri)
    {
        var links =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        foreach (
            Match match in
            HrefRegex.Matches(text))
        {
            AddLink(
                links,
                sourceUri,
                match.Groups["url"].Value);
        }

        foreach (
            Match match in
            PlainUrlRegex.Matches(text))
        {
            AddLink(
                links,
                sourceUri,
                match.Groups["url"].Value);
        }

        var ordered =
            links
                .OrderBy(x => x)
                .ToList();

        return (
            ordered
                .Take(MaxDiscoveredLinks)
                .ToList(),

            ordered.Count >
                MaxDiscoveredLinks);
    }

    private static void AddLink(
        HashSet<string> links,
        Uri sourceUri,
        string rawValue)
    {
        var value =
            WebUtility.HtmlDecode(rawValue)
                .Trim();

        if (string.IsNullOrWhiteSpace(value))
            return;

        if (value.StartsWith("#"))
            return;

        if (value.StartsWith(
            "javascript:",
            StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (value.StartsWith(
            "mailto:",
            StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        value =
            value.TrimEnd(
                '.',
                ',',
                ';',
                ')',
                ']');

        if (!Uri.TryCreate(
            sourceUri,
            value,
            out var uri))
        {
            return;
        }

        if (!IsInterestingScheme(uri.Scheme))
            return;

        try
        {
            var builder =
                new UriBuilder(uri)
                {
                    Fragment = string.Empty
                };

            uri = builder.Uri;
        }
        catch
        {
            // Some non-HTTP URIs may not cooperate
            // with UriBuilder. Keep the original.
        }

        links.Add(uri.ToString());
    }

    private static bool IsInterestingScheme(
        string scheme)
    {
        return
            scheme.Equals(
                "http",
                StringComparison.OrdinalIgnoreCase) ||

            scheme.Equals(
                "https",
                StringComparison.OrdinalIgnoreCase) ||

            scheme.Equals(
                "gopher",
                StringComparison.OrdinalIgnoreCase) ||

            scheme.Equals(
                "gemini",
                StringComparison.OrdinalIgnoreCase);
    }

    private static (
        List<string> Items,
        bool Truncated) ExtractDn42Mentions(
            string text)
    {
        var mentions =
            Dn42NameRegex
                .Matches(text)
                .Select(
                    x =>
                        x.Value
                            .ToLowerInvariant())
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x)
                .ToList();

        return (
            mentions
                .Take(MaxDn42Mentions)
                .ToList(),

            mentions.Count >
                MaxDn42Mentions);
    }

    private static async Task<(
        string Text,
        bool Truncated)> ReadTextLimitedAsync(
            HttpContent content,
            int maxBytes,
            CancellationToken cancellationToken)
    {
        await using var stream =
            await content.ReadAsStreamAsync(
                cancellationToken);

        using var memory =
            new MemoryStream();

        var buffer =
            new byte[8192];

        while (memory.Length < maxBytes)
        {
            var remaining =
                maxBytes -
                (int)memory.Length;

            var toRead =
                Math.Min(
                    buffer.Length,
                    remaining);

            var read =
                await stream.ReadAsync(
                    buffer.AsMemory(
                        0,
                        toRead),
                    cancellationToken);

            if (read == 0)
                break;

            memory.Write(
                buffer,
                0,
                read);
        }

        var truncated = false;

        if (memory.Length >= maxBytes)
        {
            var extra =
                await stream.ReadAsync(
                    buffer.AsMemory(0, 1),
                    cancellationToken);

            truncated =
                extra > 0;
        }

        return (
            Encoding.UTF8.GetString(
                memory.ToArray()),
            truncated);
    }

    private static bool? IsPathAllowed(
        string robotsText,
        string userAgent,
        string path)
    {
        robotsText = robotsText.TrimStart('\uFEFF');
        // A nonempty response without a recognizable policy is ambiguous, not permission.
        if (!string.IsNullOrWhiteSpace(robotsText) &&
            robotsText.Split('\n').Any(line =>
            {
                var value = line.Split('#', 2)[0].Trim();
                if (value.Length == 0) return false;
                var colon = value.IndexOf(':');
                if (colon <= 0) return true;
                var field = value[..colon].Trim();
                var directive = value[(colon + 1)..].Trim();
                if (field.Equals("User-agent", StringComparison.OrdinalIgnoreCase))
                    return directive.Length == 0;
                if (field.Equals("Allow", StringComparison.OrdinalIgnoreCase) ||
                    field.Equals("Disallow", StringComparison.OrdinalIgnoreCase))
                    return directive.Length > 0 && !directive.StartsWith('/');
                return false;
            }))
            return null;

        var groups =
            ParseRobotsGroups(
                robotsText);

        if (groups.Count == 0 && robotsText.Split('\n').Any(line => line.Split('#', 2)[0].Trim().Length > 0))
            return null;

        var applicable =
            groups
                .Where(
                    g => g.Agents.Any(
                        a => a.Equals(
                            userAgent,
                            StringComparison.OrdinalIgnoreCase)))
                .ToList();

        if (applicable.Count == 0)
        {
            applicable =
                groups
                    .Where(
                        g => g.Agents.Any(
                            a => a == "*"))
                    .ToList();
        }

        if (applicable.Count == 0)
            return true;

        var matchingRules =
            applicable
                .SelectMany(g => g.Rules)
                .Where(
                    rule =>
                        !string.IsNullOrEmpty(
                            rule.Pattern) &&
                        RobotsPatternMatches(
                            rule.Pattern,
                            path))
                .Select(
                    rule => new
                    {
                        Rule = rule,

                        Specificity =
                            rule.Pattern.Count(
                                c =>
                                    c != '*' &&
                                    c != '$')
                    })
                .OrderByDescending(
                    x => x.Specificity)
                .ThenByDescending(
                    x => x.Rule.Allow)
                .ToList();

        if (matchingRules.Count == 0)
            return true;

        return matchingRules[0]
            .Rule
            .Allow;
    }

    private static List<RobotsGroup>
        ParseRobotsGroups(
            string robotsText)
    {
        var groups =
            new List<RobotsGroup>();

        RobotsGroup? current = null;

        var sawRule = false;

        foreach (
            var rawLine in
            robotsText.Split('\n'))
        {
            var line =
                rawLine
                    .Split('#', 2)[0]
                    .Trim();

            if (string.IsNullOrWhiteSpace(line))
                continue;

            var colon =
                line.IndexOf(':');

            if (colon <= 0)
                continue;

            var field =
                line[..colon].Trim();

            var value =
                line[(colon + 1)..].Trim();

            if (field.Equals(
                "User-agent",
                StringComparison.OrdinalIgnoreCase))
            {
                if (current == null ||
                    sawRule)
                {
                    current =
                        new RobotsGroup();

                    groups.Add(current);

                    sawRule = false;
                }

                current.Agents.Add(value);

                continue;
            }

            if (current == null)
                continue;

            if (field.Equals(
                "Allow",
                StringComparison.OrdinalIgnoreCase))
            {
                current.Rules.Add(
                    new RobotsRule(
                        true,
                        value));

                sawRule = true;

                continue;
            }

            if (field.Equals(
                "Disallow",
                StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(value))
                {
                    current.Rules.Add(
                        new RobotsRule(
                            false,
                            value));
                }

                sawRule = true;
            }
        }

        return groups;
    }

    private static bool RobotsPatternMatches(
        string pattern,
        string path)
    {
        var endAnchored =
            pattern.EndsWith('$');

        if (endAnchored)
            pattern = pattern[..^1];

        var regexPattern =
            Regex.Escape(pattern)
                .Replace(
                    @"\*",
                    ".*");

        regexPattern =
            "^" + regexPattern;

        if (endAnchored)
            regexPattern += "$";

        return Regex.IsMatch(
            path,
            regexPattern);
    }

    private sealed class RobotsGroup
    {
        public List<string> Agents { get; } = [];

        public List<RobotsRule> Rules { get; } = [];
    }

    private sealed record RobotsRule(
        bool Allow,
        string Pattern);
}

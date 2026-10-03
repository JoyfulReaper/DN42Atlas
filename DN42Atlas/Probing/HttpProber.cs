using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using DN42Atlas.Registry;

namespace DN42Atlas.Probing;

public static class HttpProber
{
    private const int MaxRobotsBytes = 64 * 1024;
    private const int MaxHomepageBytes = 256 * 1024;

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
        CancellationToken cancellationToken = default)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),

            SslOptions =
            {
                //
                // Discovery should still see services using
                // self-signed / weird DN42 certificates.
                //
                RemoteCertificateValidationCallback =
                    (_, _, _, _) => true
            }
        };

        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        var defaultPort =
            (scheme == "http" && port == 80) ||
            (scheme == "https" && port == 443);

        var authority = defaultPort
            ? domain
            : $"{domain}:{port}";

        var origin = new Uri(
            $"{scheme}://{authority}/");

        var robotsUri = new Uri(
            origin,
            "robots.txt");

        RobotsStatus robotsStatus;
        int? robotsStatusCode;
        bool? robotsAllowed;
        string? robotsRedirect;

        //
        // First: robots.txt.
        //
        // If we cannot determine permission, we do NOT
        // fetch the homepage.
        //
        try
        {
            using var request =
                new HttpRequestMessage(
                    HttpMethod.Get,
                    robotsUri);

            request.Headers.UserAgent.ParseAdd(
                "DN42Atlas/0.1");

            using var response =
                await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

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
                        cancellationToken);

                robotsAllowed =
                    IsPathAllowed(
                        robotsBody.Text,
                        "DN42Atlas",
                        "/");

                robotsStatus =
                    robotsAllowed.Value
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
                //
                // Includes redirects.
                //
                // We deliberately don't follow them yet.
                //
                robotsStatus =
                    RobotsStatus.Unavailable;

                robotsAllowed = null;
            }
        }
        catch (Exception ex) when (
            ex is HttpRequestException or
            TaskCanceledException)
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
        // We successfully talked HTTP to this origin.
        //
        // But if robots did not explicitly permit /,
        // discovery stops here.
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
        // robots.txt permits /.
        //
        // Fetch exactly one page: the origin root.
        //
        // Redirects are NOT followed.
        //
        try
        {
            using var request =
                new HttpRequestMessage(
                    HttpMethod.Get,
                    origin);

            request.Headers.UserAgent.ParseAdd(
                "DN42Atlas/0.1");

            using var response =
                await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

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

            var title =
                default(string);

            var discoveredLinks =
                new List<string>();

            var dn42Mentions =
                new List<string>();

            var truncated = false;

            //
            // Don't bother parsing error pages or redirect
            // bodies as site content.
            //
            if (response.IsSuccessStatusCode &&
                IsTextLike(contentType))
            {
                var body =
                    await ReadTextLimitedAsync(
                        response.Content,
                        MaxHomepageBytes,
                        cancellationToken);

                truncated =
                    body.Truncated;

                title =
                    ExtractTitle(body.Text);

                discoveredLinks =
                    ExtractLinks(
                        body.Text,
                        origin);

                dn42Mentions =
                    ExtractDn42Mentions(
                        body.Text);
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
                    truncated,

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

                Dn42Mentions =
                    dn42Mentions
            };
        }
        catch (Exception ex) when (
            ex is HttpRequestException or
            TaskCanceledException)
        {
            //
            // robots.txt already proved the origin exists.
            //
            // A homepage failure does NOT make the service
            // disappear from Atlas.
            //
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

    private static bool IsTextLike(
        string? contentType)
    {
        //
        // Plenty of homemade services have no useful
        // Content-Type header, so absence is not fatal.
        //
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

    private static List<string> ExtractLinks(
        string text,
        Uri sourceUri)
    {
        var links =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        //
        // HTML href attributes:
        //
        // <a href>
        // <link href>
        // etc.
        //
        foreach (
            Match match in
            HrefRegex.Matches(text))
        {
            AddLink(
                links,
                sourceUri,
                match.Groups["url"].Value);
        }

        //
        // Also find literal protocol URLs in page text.
        //
        foreach (
            Match match in
            PlainUrlRegex.Matches(text))
        {
            AddLink(
                links,
                sourceUri,
                match.Groups["url"].Value);
        }

        return links
            .OrderBy(x => x)
            .Take(250)
            .ToList();
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

        if (value.StartsWith(
                "#",
                StringComparison.Ordinal))
        {
            return;
        }

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

        //
        // Plain-text URL regexes often grab punctuation
        // immediately following the URL.
        //
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

        //
        // Fragments are not separate resources for Atlas.
        //
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
            // Some odd-but-valid URI schemes may not
            // cooperate with UriBuilder. Keep original.
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

    private static List<string> ExtractDn42Mentions(
        string text)
    {
        return Dn42NameRegex
            .Matches(text)
            .Select(
                x => x.Value.ToLowerInvariant())
            .Distinct(
                StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .Take(250)
            .ToList();
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

    //
    // Small robots.txt parser focused on answering one
    // question safely:
    //
    //     May DN42Atlas request "/"?
    //
    // Specific DN42Atlas groups beat "*" groups.
    // Longest matching rule wins.
    // Allow wins a tie.
    //
    private static bool IsPathAllowed(
        string robotsText,
        string userAgent,
        string path)
    {
        var groups =
            ParseRobotsGroups(
                robotsText);

        var applicable =
            groups
                .Where(
                    g => g.Agents.Any(
                        a => a.Equals(
                            userAgent,
                            StringComparison
                                .OrdinalIgnoreCase)))
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

        RobotsGroup? current =
            null;

        var sawRule =
            false;

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
                //
                // Empty Disallow means no restriction.
                //
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
            pattern.EndsWith(
                '$');

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
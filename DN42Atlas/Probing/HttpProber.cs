using DN42Atlas.Registry;

namespace DN42Atlas.Probing;

public static class HttpProber
{
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
                RemoteCertificateValidationCallback = (_, _, _, _) => true
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

        var uri = new Uri($"{scheme}://{authority}/robots.txt");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);

            request.Headers.UserAgent.ParseAdd("DN42Atlas/0.1");

            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            var redirectLocation = response.Headers.Location?.ToString();

            var robotsStatus = RobotsStatus.Unavailable;
            bool? robotsAllowed = null;

            if (response.IsSuccessStatusCode)
            {
                var robotsText =
                    await response.Content.ReadAsStringAsync(cancellationToken);

                robotsAllowed = IsRootAllowed(robotsText);

                robotsStatus = robotsAllowed.Value
                    ? RobotsStatus.Allowed
                    : RobotsStatus.Disallowed;
            }
            else if ((int)response.StatusCode == 404)
            {
                robotsStatus = RobotsStatus.NoRules;
                robotsAllowed = true;
            }

            return new HttpProbeResult
            {
                Domain = domain,
                Scheme = scheme,
                Port = port,
                Reachable = true,
                RobotsStatusCode = (int)response.StatusCode,
                RedirectLocation = redirectLocation,
                Robots = robotsStatus,
                RobotsAllowed = robotsAllowed
            };
        }
        catch (Exception ex) when (
            ex is HttpRequestException or TaskCanceledException)
        {
            return new HttpProbeResult
            {
                Domain = domain,
                Scheme = scheme,
                Port = port,
                Reachable = false,
                Robots = RobotsStatus.Unavailable,
                Error = ex.Message
            };
        }
    }

    private static bool IsRootAllowed(string robotsText)
    {
        var applies = false;
        var sawSpecificAgent = false;

        foreach (var rawLine in robotsText.Split('\n'))
        {
            var line = rawLine.Split('#', 2)[0].Trim();

            if (string.IsNullOrWhiteSpace(line))
                continue;

            var colon = line.IndexOf(':');

            if (colon <= 0)
                continue;

            var field = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            if (field.Equals(
                "User-agent",
                StringComparison.OrdinalIgnoreCase))
            {
                if (value.Equals(
                    "DN42Atlas",
                    StringComparison.OrdinalIgnoreCase))
                {
                    applies = true;
                    sawSpecificAgent = true;
                }
                else if (value == "*" && !sawSpecificAgent)
                {
                    applies = true;
                }
                else
                {
                    applies = false;
                }

                continue;
            }

            if (!applies)
                continue;

            if (field.Equals(
                    "Disallow",
                    StringComparison.OrdinalIgnoreCase) &&
                value == "/")
            {
                return false;
            }

            if (field.Equals(
                    "Allow",
                    StringComparison.OrdinalIgnoreCase) &&
                value == "/")
            {
                return true;
            }
        }

        return true;
    }
}
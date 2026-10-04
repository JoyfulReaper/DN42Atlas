using System.Text;
using System.Text.Encodings.Web;
using DN42Atlas.OptOut.Auth;
using DN42Atlas.Registry;
using DN42Atlas.OptOut.Registry;
using DN42Atlas.OptOut.Exclusions;

namespace DN42Atlas.OptOut.Web;

public static class OptOutPage
{
    public static string RenderSignedOut()
    {
        return Layout(
            "DN42Atlas operator self-service",
            """
            <h1>DN42Atlas operator self-service</h1>
            <p>This service lets DN42 operators review the registered domains associated with their maintainer object.</p>
            <p><a class="button" href="/login">Login with DN42</a></p>
            """);
    }

    public static string RenderSignedIn(
        Auth42Identity identity,
        IReadOnlyList<string> domains,
        IReadOnlyList<string> ipv4Prefixes,
        IReadOnlyList<string> ipv6Prefixes,
        RegistrySnapshot snapshot,
        bool ownershipAvailable = true,
        IReadOnlySet<ExactResource>? selfService = null,
        string? requestToken = null,
        string? result = null,
        IReadOnlySet<ExactResource>? manualExcluded = null)
    {
        var encoder = HtmlEncoder.Default;
        var content = new StringBuilder();

        content.Append("<h1>DN42Atlas operator self-service</h1>");
        content.Append("<p>Authenticated as <strong>");
        content.Append(encoder.Encode(identity.ActiveMaintainer));
        content.Append("</strong><br>AS");
        content.Append(identity.Asn);
        content.Append("</p>");

        AppendRegistryStatus(content, encoder, snapshot);
        if (result == "excluded") content.Append("<p>Exclusion recorded and public Atlas regenerated.</p>");
        if (result == "included") content.Append("<p>Self-service exclusion revoked and public Atlas regenerated.</p>");

        if (!ownershipAvailable)
        {
            content.Append("<p>Registry ownership data is currently unavailable.</p>");
        }
        else
        {
            content.Append("<h2>Domains you can manage</h2>");

            AppendList(
                content,
                encoder,
                domains,
                "No matching registered .dn42 domains were found.", ExclusionResourceType.Domain, selfService, manualExcluded,
                snapshot.IsSafeForAutomaticApproval, requestToken);

            content.Append("<h2>IPv4 prefixes you can manage</h2>");
            AppendList(
                content,
                encoder,
                ipv4Prefixes,
                "No matching IPv4 allocations were found.", ExclusionResourceType.IPv4Prefix, selfService, manualExcluded,
                snapshot.IsSafeForAutomaticApproval, requestToken);

            content.Append("<h2>IPv6 prefixes you can manage</h2>");
            AppendList(
                content,
                encoder,
                ipv6Prefixes,
                "No matching IPv6 allocations were found.", ExclusionResourceType.IPv6Prefix, selfService, manualExcluded,
                snapshot.IsSafeForAutomaticApproval, requestToken);
        }

        content.Append("<p>Excluding a domain prevents future probes for that hostname and removes it from the currently published Atlas without another crawl.</p>");
        content.Append("<p>Excluding a prefix prevents future probes to addresses in that allocation and removes results whose recorded probe destinations match it. If an older scan lacks destination evidence, the public listing is withdrawn until a safe publication is available.</p>");
        content.Append("<p>Self-service exclusions cover exact registered resources only. Wildcards and broader or narrower allocations are not supported.</p>");
        content.Append("<p><a href=\"/logout\">Logout</a></p>");

        return Layout(
            "DN42Atlas operator self-service",
            content.ToString());
    }

    private static void AppendRegistryStatus(
        StringBuilder content,
        HtmlEncoder encoder,
        RegistrySnapshot snapshot)
    {
        content.Append("<h2>Registry snapshot</h2><dl>");
        AppendStatusValue(
            content,
            encoder,
            "Age",
            FormatAge(snapshot.Age));
        AppendStatusValue(
            content,
            encoder,
            "Status",
            snapshot.Status switch
            {
                RegistrySnapshotStatus.Fresh => "Fresh",
                RegistrySnapshotStatus.Stale => "STALE",
                RegistrySnapshotStatus.Dirty => "DIRTY",
                _ => "UNKNOWN"
            });
        content.Append("</dl>");

        if (snapshot.Status == RegistrySnapshotStatus.Stale)
        {
            content.Append("<p><strong>Automatic opt-out approval is disabled until the registry is refreshed.</strong></p>");
        }
        else if (snapshot.Status == RegistrySnapshotStatus.Dirty)
        {
            content.Append("<p><strong>The registry working tree is modified. Automatic opt-out approval is disabled.</strong></p>");
        }
        else if (snapshot.Status == RegistrySnapshotStatus.Unknown)
        {
            content.Append("<p><strong>Registry freshness could not be established. Automatic opt-out approval is disabled.</strong></p>");
        }
    }

    private static void AppendStatusValue(
        StringBuilder content,
        HtmlEncoder encoder,
        string label,
        string value)
    {
        content.Append("<dt>");
        content.Append(encoder.Encode(label));
        content.Append("</dt><dd>");
        content.Append(encoder.Encode(value));
        content.Append("</dd>");
    }

    private static string FormatAge(TimeSpan? value)
    {
        if (value is null)
            return "Unavailable";

        var age = value.Value;

        return age.TotalDays >= 1
            ? $"{(int)age.TotalDays}d {age.Hours}h"
            : $"{(int)age.TotalHours}h {age.Minutes}m";
    }

    private static void AppendList(
        StringBuilder content,
        HtmlEncoder encoder,
        IReadOnlyList<string> values,
        string emptyMessage, ExclusionResourceType type, IReadOnlySet<ExactResource>? selfService,
        IReadOnlySet<ExactResource>? manualExcluded,
        bool fresh, string? token)
    {
        if (values.Count == 0)
        {
            content.Append("<p>");
            content.Append(encoder.Encode(emptyMessage));
            content.Append("</p>");
            return;
        }

        content.Append("<ul>");

        foreach (var value in values)
        {
            content.Append("<li>");
            content.Append(encoder.Encode(value));
            var manual = manualExcluded?.Contains(new(type, value)) == true;
            var active = selfService?.Contains(new(type, value)) == true;
            content.Append(manual ? " <strong>Excluded by operator policy</strong>" :
                active ? " <strong>Excluded by self-service</strong>" : " <strong>Included</strong>");
            if (!manual && fresh && !string.IsNullOrEmpty(token))
            {
                content.Append("<form method=\"post\" action=\"/operator\">");
                content.Append($"<input type=\"hidden\" name=\"intent\" value=\"{(active ? "confirm-include" : "confirm-exclude")}\">");
                content.Append($"<input type=\"hidden\" name=\"resourceType\" value=\"{encoder.Encode(type.ToString())}\">");
                content.Append($"<input type=\"hidden\" name=\"resourceValue\" value=\"{encoder.Encode(value)}\">");
                content.Append($"<input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"{encoder.Encode(token)}\">");
                content.Append($"<button type=\"submit\">{(active ? "Include again" : "Exclude from DN42Atlas")}</button></form>");
            }
            content.Append("</li>");
        }

        content.Append("</ul>");
    }

    public static string RenderConfirmation(string operation, ExactResource resource, string requestToken, string confirmationToken)
    {
        var encoder = HtmlEncoder.Default;
        var include = operation == "include";
        var content = $"<h1>{(include ? "Include" : "Exclude")} {encoder.Encode(resource.Value)} {(include ? "in DN42Atlas again" : "from DN42Atlas")}?</h1>";
        content += include
            ? "<p>The self-service exclusion will be revoked. The current Atlas will be regenerated from the recorded scan, so existing scan data may reappear immediately, and future Atlas scans may probe the resource again.</p>"
            : "<p>This will prevent future Atlas probes covered by this resource and immediately remove/redact it from the currently published Atlas without another crawl.</p>";
        content += "<form method=\"post\" action=\"/operator\">";
        foreach (var field in new Dictionary<string, string> { ["intent"] = operation,
            ["resourceType"] = resource.Type.ToString(), ["resourceValue"] = resource.Value,
            ["__RequestVerificationToken"] = requestToken, ["confirmationToken"] = confirmationToken })
            content += $"<input type=\"hidden\" name=\"{field.Key}\" value=\"{encoder.Encode(field.Value)}\">";
        content += $"<button type=\"submit\">Confirm {(include ? "inclusion" : "exclusion")}</button></form><p><a href=\"/operator\">Cancel</a></p>";
        return Layout("DN42Atlas confirmation", content);
    }

    public static string RenderMutationStatus(MutationStatus status)
    {
        var message = status switch
        {
            MutationStatus.Conflict => "This operation is no longer available, or an independent operator exclusion applies. Return to the dashboard to review current state.",
            MutationStatus.InclusionRestored => "Inclusion failed. The original exclusion was restored and the public Atlas remains filtered. Please contact the operator.",
            MutationStatus.InclusionUnavailable => "Inclusion recovery could not be confirmed. Crawling and the public listing are unavailable pending operator reconciliation.",
            MutationStatus.InvalidRequest => "Invalid resource request. No change was made.",
            MutationStatus.NotAuthorized => "The current registry does not authorize this exact resource, or is not fresh. No change was made.",
            MutationStatus.NotRecorded => "The operation was not completed. Please contact the operator.",
            MutationStatus.Busy => "An Atlas crawl or publication is in progress. No change was made. Please retry shortly.",
            MutationStatus.RecordedRuntimeUnavailable => "Your exclusion has been recorded, but runtime policy reconciliation failed. Crawling and the public listing are unavailable pending reconciliation.",
            MutationStatus.RecordedPublicationWithdrawn => "Your exclusion has been recorded and future probing is blocked, but the public Atlas could not be regenerated and has been withdrawn pending reconciliation.",
            MutationStatus.WithdrawalFailed => "The operation failed and policy or public withdrawal could not be completed. Immediate operator intervention is required.",
            MutationStatus.UncertainWithdrawalFailed => "The exclusion status could not be confirmed and public withdrawal could not be completed. Immediate operator intervention is required.",
            _ => "The exclusion status could not be confirmed. The public listing has been withdrawn pending operator reconciliation."
        };
        return Layout("DN42Atlas resource status", $"<h1>Resource status</h1><p>{HtmlEncoder.Default.Encode(message)}</p><p><a href=\"/operator\">Back to operator dashboard</a></p>");
    }

    private static string Layout(string title, string content)
    {
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>{{HtmlEncoder.Default.Encode(title)}}</title>
              <style>
                :root { color-scheme: light dark; font-family: system-ui, sans-serif; }
                body { max-width: 48rem; margin: 4rem auto; padding: 0 1.25rem; line-height: 1.55; }
                .button { display: inline-block; padding: .6rem .9rem; border: 1px solid currentColor; border-radius: .3rem; text-decoration: none; }
              </style>
            </head>
            <body>
            <p><a href="/">← Back to DN42Atlas</a></p>
            {{content}}
            </body>
            </html>
            """;
    }
}

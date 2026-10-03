using System.Text;
using System.Text.Encodings.Web;
using DN42Atlas.OptOut.Auth;

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
        IReadOnlyList<string> ipv6Prefixes)
    {
        var encoder = HtmlEncoder.Default;
        var content = new StringBuilder();

        content.Append("<h1>DN42Atlas operator self-service</h1>");
        content.Append("<p>Authenticated as <strong>");
        content.Append(encoder.Encode(identity.ActiveMaintainer));
        content.Append("</strong><br>AS");
        content.Append(identity.Asn);
        content.Append("</p><h2>Domains you can manage</h2>");

        AppendList(
            content,
            encoder,
            domains,
            "No matching registered .dn42 domains were found.");

        content.Append("<h2>IPv4 prefixes you can manage</h2>");
        AppendList(
            content,
            encoder,
            ipv4Prefixes,
            "No matching IPv4 allocations were found.");

        content.Append("<h2>IPv6 prefixes you can manage</h2>");
        AppendList(
            content,
            encoder,
            ipv6Prefixes,
            "No matching IPv6 allocations were found.");

        content.Append("<p>This first release is read-only. Exclusion controls are not available yet.</p>");
        content.Append("<p><a href=\"/logout\">Logout</a></p>");

        return Layout(
            "DN42Atlas operator self-service",
            content.ToString());
    }

    private static void AppendList(
        StringBuilder content,
        HtmlEncoder encoder,
        IReadOnlyList<string> values,
        string emptyMessage)
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
            content.Append("</li>");
        }

        content.Append("</ul>");
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
            {{content}}
            </body>
            </html>
            """;
    }
}

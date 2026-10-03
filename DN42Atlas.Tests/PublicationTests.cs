using System.Text.Json;
using System.Text.Json.Nodes;
using DN42Atlas.Policy;
using DN42Atlas.Reporting;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DN42Atlas.Tests;

[TestClass]
public sealed class PublicationTests
{
    [TestMethod]
    public void PublicOutputRemovesExcludedServicesReferencesAndRuleText()
    {
        using var files = new TestFiles("blocked.dn42\n*.private.dn42", "172.20.16.0/20\nfd42:1234::/48");
        var scan = JsonNode.Parse("""
            {
              "ExcludedByHostname": 2,
              "Results": [
                {"Domain":"blocked.dn42","Scheme":"http","Port":80},
                {"Domain":"BLOCKED.DN42","Scheme":"https","Port":443},
                {"Domain":"good.dn42","Scheme":"http","Port":80,
                 "Title":"References BLOCKED.DN42 and child.private.dn42 at 172.20.16.7, [fd42:1234::1]; rules *.private.dn42 and fd42:1234::/48",
                 "RedirectLocation":"http://blocked.dn42/",
                 "DiscoveredLinks":["http://blocked.dn42/","http://child.private.dn42/","http://172.20.16.7/","http://[fd42:1234::1]/","http://private.dn42/","https://notblocked.dn42/","javascript:alert(1)"],
                 "Dn42Mentions":["blocked.dn42","child.private.dn42","private.dn42","good.dn42"]}
              ]
            }
            """)!;
        new PublicScanPolicy(files.LoadPolicy()).Apply(scan);
        Assert.AreEqual(3, scan["ExcludedByHostname"]!.GetValue<int>());
        Assert.HasCount(1, scan["Results"]!.AsArray());
        var result = scan["Results"]![0]!;
        CollectionAssert.AreEqual(new[] { "http://private.dn42/", "https://notblocked.dn42/" }, result["DiscoveredLinks"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
        CollectionAssert.AreEqual(new[] { "private.dn42", "good.dn42" }, result["Dn42Mentions"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
        var text = scan.ToJsonString();
        foreach (var excluded in new[] { "BLOCKED.DN42", "child.private.dn42", "172.20.16.7", "fd42:1234", "*.private.dn42" })
            Assert.IsFalse(text.Contains(excluded, StringComparison.Ordinal));
        var once = text;
        new PublicScanPolicy(files.LoadPolicy()).Apply(scan);
        Assert.AreEqual(once, scan.ToJsonString());
    }

    [TestMethod]
    public async Task ImportedReportJsonCannotTerminateScriptAndUsesCurrentExclusions()
    {
        using var files = new TestFiles("blocked.dn42");
        var input = files.Write("scan.json", """
            {"Results":[
              {"Domain":"blocked.dn42","Scheme":"http","Port":80,"Reachable":true,"StatusCode":200},
              {"Domain":"good.dn42","Scheme":"http","Port":80,"Reachable":true,"StatusCode":200,
               "Title":"</ScRiPt><script>alert(1)</script>",
               "DiscoveredLinks":["javascript:alert(2)","https://good.dn42/"]}
            ]}
            """);
        var originalJson = await File.ReadAllTextAsync(input);
        var output = Path.ChangeExtension(input, ".html");
        await AtlasReportGenerator.GenerateAsync(input, output, files.LoadPolicy());
        var html = await File.ReadAllTextAsync(output);
        Assert.IsFalse(html.Contains("<script>alert(1)", StringComparison.OrdinalIgnoreCase));
        Assert.HasCount(1, System.Text.RegularExpressions.Regex.Matches(html, "</script>", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        Assert.IsFalse(html.Contains("javascript:alert(2)", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("blocked.dn42", StringComparison.OrdinalIgnoreCase));
        var begin = html.IndexOf("const scan = ", StringComparison.Ordinal) + "const scan = ".Length;
        var end = html.IndexOf(";", begin);
        using var embedded = JsonDocument.Parse(html[begin..end]);
        Assert.AreEqual("</ScRiPt><script>alert(1)</script>", embedded.RootElement.GetProperty("Results")[0].GetProperty("Title").GetString());
        // The source snapshot is immutable; only the newly generated HTML is filtered.
        Assert.AreEqual(originalJson, await File.ReadAllTextAsync(input));
        Assert.IsFalse(html.Contains("Last scan:", StringComparison.Ordinal));
        Assert.IsFalse(embedded.RootElement.TryGetProperty("GeneratedAt", out _));
    }

    [TestMethod]
    public async Task GeneratedViewerIncludesPublicNavigationFooterAndScanTimestamp()
    {
        using var files = new TestFiles();
        var input = files.Write("scan.json", """
            {
              "GeneratedAt":"2026-10-03T00:42:03.4461149-04:00",
              "Results":[]
            }
            """);
        var output = Path.ChangeExtension(input, ".html");

        await AtlasReportGenerator.GenerateAsync(
            input,
            output,
            files.LoadPolicy());

        var html = await File.ReadAllTextAsync(output);

        StringAssert.Contains(html, "href=\"/about.html\"");
        StringAssert.Contains(html, "href=\"/opt-out.html\"");
        StringAssert.Contains(
            html,
            "href=\"https://github.com/JoyfulReaper/DN42Atlas\"");
        StringAssert.Contains(
            html,
            "DN42Atlas does not automatically follow discovered links.");
        StringAssert.Contains(html, "AS4242420425");
        StringAssert.Contains(html, "Last scan:");
        StringAssert.Contains(html, "2026-10-03 04:42:03 UTC");
        StringAssert.Contains(
            html,
            "href=\"https://greencloudvps.com/billing/aff.php?aff=10295\"");
        StringAssert.Contains(
            html,
            "rel=\"sponsored noopener noreferrer\"");
        StringAssert.Contains(html, "target=\"_blank\"");
        StringAssert.Contains(html, "Hosted on");
        StringAssert.Contains(
            html,
            "Affiliate link — I may earn a commission if you sign up.");
    }
}

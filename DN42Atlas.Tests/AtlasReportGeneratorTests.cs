using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DN42Atlas.Publishing;
using DN42Atlas.Reporting;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DN42Atlas.Tests;

[TestClass]
public sealed class AtlasReportGeneratorTests
{
    [TestMethod]
    public void BasicDirectoryAndDetailsIncludeEveryPublicResultWithoutJavaScript()
    {
        var html = Generate("""
            {"Results":[
              {"Domain":"allowed.dn42","Scheme":"https","Port":443,"Reachable":true,"StatusCode":200,"Robots":2,"Title":"Directory title","ContentType":"text/html"},
              {"Domain":"restricted.dn42","Scheme":"http","Port":8080,"Reachable":true,"StatusCode":null,"Robots":3,"RobotsStatusCode":200,"RobotsAllowed":false},
              {"Domain":"unavailable.dn42","Scheme":"https","Port":8443,"Reachable":true,"Robots":4,"RobotsAllowed":false},
              {"Domain":"failed.dn42","Scheme":"http","Port":80,"Reachable":true,"Robots":1,"HomepageError":"Read timeout"},
              {"Domain":"offline.dn42","Scheme":"http","Port":80,"Reachable":false,"Error":"Connection refused"}
            ]}
            """);
        var basic = WithoutScript(html);
        Assert.HasCount(5, Regex.Matches(basic, "<tr data-service="));
        Assert.HasCount(5, Regex.Matches(basic, "<article class=\"card service-detail\""));
        foreach (var host in new[] { "allowed", "restricted", "unavailable", "failed", "offline" })
            Assert.Contains(host + ".dn42", basic);
        Assert.Contains("https://allowed.dn42/", basic);
        Assert.Contains("http://restricted.dn42:8080/", basic);
        Assert.Contains("HTTP / 8080", basic);
        Assert.Contains("Directory title", basic);
        Assert.Contains("text/html", basic);
        Assert.Contains("Reachable · HTTP 200", WebUtility.HtmlDecode(basic));
        Assert.Contains("Reachable · homepage not fetched / robots restricted", WebUtility.HtmlDecode(basic));
        Assert.Contains("Reachable · homepage fetch failed (no HTTP status)", WebUtility.HtmlDecode(basic));
        Assert.Contains("Unreachable", basic);
        Assert.Contains("Read timeout", basic);
        Assert.Contains("Connection refused", basic);
        Assert.DoesNotContain("HTTP null", basic);
        Assert.Contains("<div class=\"stat-value\">4</div><div class=\"stat-label\">reachable public origins", basic);
        foreach (Match row in Regex.Matches(basic, "<tr data-service=\"([^\"]+)\""))
        {
            Assert.Contains($"aria-controls=\"{row.Groups[1].Value}\"", basic);
            Assert.Contains($"id=\"{row.Groups[1].Value}\"", basic);
        }
        Assert.Contains("id=\"search\"", basic);
        Assert.Contains("id=\"scheme\"", basic);
        Assert.Contains("id=\"robots\"", basic);
        Assert.Contains("id=\"previous\"", basic);
        Assert.Contains("id=\"random\"", basic);
        Assert.Contains("id=\"next\"", basic);
        Assert.DoesNotContain("innerHTML", html);
    }

    [TestMethod]
    public void BrowseControlsIncludeShortcutHelpAndAccessibleCopyFallback()
    {
        var basic = WithoutScript(Generate("""{"Results":[]}"""));
        var controls = Regex.Match(basic, "<div class=\"controls\"[^>]*>.*?</div>", RegexOptions.Singleline).Value;
        Assert.Contains("id=\"controls\" hidden", controls,
            "JavaScript-only controls must stay hidden when scripting is unavailable.");
        Assert.Contains("<kbd>←</kbd> previous", controls);
        Assert.Contains("<kbd>→</kbd> next", controls);
        Assert.Contains("<kbd>R</kbd> random (outside controls)", controls);
        Assert.Contains("<button id=\"copy-link\" type=\"button\" disabled>Copy link</button>", controls,
            "Copying must be unavailable until JavaScript has selected a service.");
        var feedback = Regex.Match(controls, "<span[^>]*id=\"copy-status\"[^>]*>").Value;
        Assert.Contains("role=\"status\"", feedback);
        Assert.Contains("aria-live=\"polite\"", feedback);
        Assert.Contains("aria-atomic=\"true\"", feedback);
        Assert.Contains("for=\"copy-url\" hidden>Service link", controls);
        Assert.Contains("<input id=\"copy-url\" type=\"text\" readonly>", controls,
            "The manual-copy fallback must have a label and a selectable read-only URL.");
    }

    [TestMethod]
    public void DiscoveryFilterMetadataUsesOnlyRecordedPublicFields()
    {
        var basic = WithoutScript(Generate("""
            {"Results":[
              {"Domain":"response.dn42","Scheme":"http","Port":80,"StatusCode":404,"Title":"  ","DiscoveredLinks":["https://linked.dn42/"]},
              {"Domain":"unknown.dn42","Scheme":"https","Port":443,"RobotsStatusCode":200,"Title":"Named"},
              {"Domain":"other.dn42","Scheme":"http","Port":443,"StatusCode":200,"DiscoveredLinks":[]},
              {"Domain":"invalid.dn42","Scheme":"http","Port":0},
              {"Domain":"placeholder.dn42","Scheme":"http","Port":8080,"Title":"(no title)"}
            ]}
            """));
        string Row(string domain) => Regex.Match(basic,
            $"<tr data-service=\"[^\"]+\" data-search=\"{Regex.Escape(domain)}[^>]*>").Value;
        Assert.Contains("data-response=\"true\" data-title=\"false\" data-links=\"true\" data-port-kind=\"standard\"", Row("response.dn42"));
        Assert.Contains("data-response=\"false\" data-title=\"true\" data-links=\"false\" data-port-kind=\"standard\"", Row("unknown.dn42"),
            "A robots.txt response must not be mistaken for a homepage response.");
        Assert.Contains("data-port=\"443\"", Row("other.dn42"));
        Assert.Contains("data-port-kind=\"other\"", Row("other.dn42"), "HTTP on 443 is not its scheme's default port.");
        Assert.Contains("data-port-kind=\"\"", Row("invalid.dn42"), "An unknown/invalid port must not be classified as a web port.");
        Assert.Contains("data-title=\"true\"", Row("placeholder.dn42"), "A real title matching the display placeholder still counts as a title.");
        Assert.Contains("<details class=\"discovery-filters\" id=\"discovery-filters\">", basic);
        foreach (var key in new[] { "origin", "title", "scheme", "status" })
        {
            var header = Regex.Match(basic, $"<th scope=\"col\" data-sort=\"{key}\"[^>]*>.*?</th>").Value;
            Assert.Contains("<button class=\"sort-button\" type=\"button\" disabled>", header,
                "Sorting controls are enhanced by JavaScript; static column labels must remain available without it.");
            Assert.Contains("aria-hidden=\"true\"", header);
        }
    }

    [TestMethod]
    public void MobileDirectoryKeepsOneTableWithAccessibleFieldAssociations()
    {
        var basic = WithoutScript(Generate("""
            {"Results":[
              {"Domain":"mobile.dn42","Scheme":"https","Port":8443,"Reachable":true,"StatusCode":200,"Title":"A long mobile service title","Robots":2},
              {"Domain":"offline.dn42","Scheme":"http","Port":80,"Reachable":false}
            ]}
            """));
        Assert.HasCount(1, Regex.Matches(basic, "<table role=\"table\">"));
        Assert.HasCount(2, Regex.Matches(basic, "<tr data-service="));
        Assert.Contains("<thead role=\"rowgroup\"><tr role=\"row\">", basic);
        Assert.Contains("<tbody id=\"services\" role=\"rowgroup\">", basic);
        foreach (var id in Ids(basic))
        {
            Assert.Contains($"scope=\"row\" role=\"rowheader\" id=\"origin-{id}\" headers=\"column-origin\"", basic);
            Assert.Contains($"aria-controls=\"{id}\" hidden>View details</button>", basic);
            foreach (var field in new[] { "title", "scheme", "status" })
            {
                Assert.Contains($"role=\"columnheader\" id=\"column-{field}\"", basic);
                Assert.Contains($"role=\"cell\" headers=\"column-{field} origin-{id}\"", basic);
            }
        }
        foreach (var label in new[] { "Origin", "Title", "Scheme / port", "Status" })
            Assert.Contains($"<span class=\"cell-label\" aria-hidden=\"true\">{label}</span>", basic,
                "Visual mobile labels must not be announced in addition to the associated table headers.");
        Assert.Contains("A long mobile service title", basic);
        Assert.Contains("HTTPS / 8443", basic);
        Assert.Contains("robots: Allowed", basic);
        Assert.Contains("Unreachable", basic);
    }

    [TestMethod]
    public void DirectoryOriginsUseTheSameSafeExternalRenderingAsServiceDetails()
    {
        var basic = WithoutScript(Generate("""
            {"Results":[
              {"Domain":"plain.dn42","Scheme":"http","Port":80,"Reachable":true,"Title":"Plain"},
              {"Domain":"secure.dn42","Scheme":"https","Port":8443,"Reachable":true},
              {"Domain":"user@unsafe.dn42","Scheme":"http","Port":80},
              {"Domain":"path.dn42/injected","Scheme":"https","Port":443},
              {"Domain":"unsupported.dn42","Scheme":"gopher","Port":70}
            ]}
            """));
        var directory = Regex.Match(basic, "<tbody id=\"services\"[^>]*>(.*?)</tbody>", RegexOptions.Singleline).Value;
        foreach (var origin in new[] { "http://plain.dn42/", "https://secure.dn42:8443/" })
        {
            var link = $"<a href=\"{origin}\" target=\"_blank\" rel=\"noopener noreferrer\">{origin}</a>";
            Assert.Contains(link, directory);
            Assert.HasCount(2, Regex.Matches(basic, Regex.Escape(link)), "Directory and details must share the same safe Origin renderer.");
        }
        Assert.DoesNotContain("href=\"#service-", directory);
        foreach (var unsafeOrigin in new[] { "http://user@unsafe.dn42/", "https://path.dn42/injected/", "gopher://unsupported.dn42:70/" })
        {
            Assert.Contains(unsafeOrigin, directory);
            Assert.DoesNotContain($"href=\"{unsafeOrigin}\"", basic);
        }
        Assert.Contains("class=\"row-details\" type=\"button\"", directory);
        Assert.Contains("aria-label=\"View details for http://plain.dn42/\"", directory);
        foreach (Match row in Regex.Matches(directory, "<tr[^>]*>"))
        {
            Assert.Contains("role=\"row\"", row.Value);
            Assert.DoesNotContain("tabindex", row.Value, "Use native buttons for keyboard activation, retaining row semantics.");
        }
        Assert.Contains("All public service details are listed below.", basic);
    }

    [TestMethod]
    public void UntrustedFieldsAreEscapedAndUnsafeUrlsNeverBecomeActiveLinks()
    {
        const string hostile = "\"><img src=x onerror=alert(1)> & ' {{rows}} </script>";
        const string safeLink = "https://good.dn42/?q=%22quoted%22&x=1";
        var result = new JsonObject
        {
            ["Domain"] = hostile + ".dn42", ["Scheme"] = "http", ["Port"] = 80,
            ["Reachable"] = true, ["Title"] = hostile, ["ContentType"] = hostile,
            ["Robots"] = hostile, ["HomepageError"] = hostile,
            ["HomepageRedirectLocation"] = "javascript:alert(2)",
            ["DiscoveredLinks"] = new JsonArray("javascript:alert(3)", "data:text/html,<script>alert(4)</script>", safeLink),
            ["Dn42Mentions"] = new JsonArray(hostile + ".dn42")
        };
        var html = AtlasReportGenerator.GenerateHtml(new JsonObject { ["Results"] = new JsonArray(result) });
        var basic = WithoutScript(html);
        Assert.Contains(WebUtility.HtmlEncode(hostile), basic);
        Assert.Contains(WebUtility.HtmlEncode(hostile + ".dn42"), basic);
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("href=\"javascript:", html);
        Assert.DoesNotContain("href=\"data:", html);
        Assert.DoesNotContain("href=\"http://&quot;", html);
        // Invalid imported links remain inert text even when calling the policy-filtered renderer directly.
        Assert.Contains("javascript:alert(3)", basic);
        Assert.Contains("{{rows}}", basic);
        Assert.HasCount(1, Regex.Matches(html, "</script>", RegexOptions.IgnoreCase));
        Assert.Contains($"href=\"{WebUtility.HtmlEncode(safeLink)}\"", basic);
        Assert.DoesNotContain("innerHTML", html);
    }

    [TestMethod]
    public void DeepLinkIdsSurviveReorderingScanChangesAndHostnameCase()
    {
        var scan = JsonNode.Parse("""
            {"GeneratedAt":"2026-10-03T12:00:00Z","Results":[
              {"Domain":"stable.dn42","Scheme":"http","Port":80,"Title":"Old title","Reachable":true,"StatusCode":200},
              {"Domain":"stable.dn42","Scheme":"https","Port":443,"Reachable":true},
              {"Domain":"stable.dn42","Scheme":"https","Port":8443,"Reachable":true}
            ]}
            """)!;
        var first = AtlasReportGenerator.GenerateHtml(scan);
        var ids = Ids(first);
        Assert.HasCount(3, ids);
        Assert.AreEqual(3, ids.Distinct().Count());
        CollectionAssert.AreEqual(ids, Ids(AtlasReportGenerator.GenerateHtml(scan.DeepClone())));
        scan["GeneratedAt"] = "2026-10-04T12:00:00Z";
        scan["Results"]![0]!["Title"] = "New title";
        scan["Results"]![0]!["StatusCode"] = null;
        scan["Results"]![0]!["Domain"] = "STABLE.DN42.";
        var reversed = scan["Results"]!.AsArray().Reverse().Select(row => row!.DeepClone()).ToArray();
        scan["Results"] = new JsonArray(reversed);
        CollectionAssert.AreEquivalent(ids, Ids(AtlasReportGenerator.GenerateHtml(scan)));
    }

    [TestMethod]
    public void HeadBootstrapPreparesEnhancementWithoutBreakingTheStaticFallback()
    {
        var html = Generate("""
            {"Results":[
              {"Domain":"online.dn42","Scheme":"http","Port":80,"Reachable":true},
              {"Domain":"offline.dn42","Scheme":"https","Port":443,"Reachable":false}
            ]}
            """);
        Assert.IsLessThan(html.IndexOf("</head>", StringComparison.Ordinal), html.IndexOf("<script>", StringComparison.Ordinal));
        Assert.Contains(".js-pending body { visibility: hidden; }", html);
        Assert.Contains(".js-pending .service-detail { display: none; }", html,
            "JS-enabled parsing must not lay out every service panel before initialization.");
        Assert.Contains("document.addEventListener('DOMContentLoaded'", html);
        Assert.Contains("root.classList.remove('js-pending')", html);
        Assert.DoesNotContain("<html lang=\"en\" class=\"js-pending\"", html,
            "Only executing JavaScript may enable the pending presentation.");
        var basic = WithoutScript(html);
        Assert.HasCount(2, Regex.Matches(basic, "<tr data-service="));
        Assert.HasCount(2, Regex.Matches(basic, "<article class=\"card service-detail\""));
        foreach (Match article in Regex.Matches(basic, "<article class=\"card service-detail\"[^>]*>"))
            Assert.DoesNotContain(" hidden", article.Value);
        Assert.Contains("<noscript>", basic);
        Assert.Contains("id=\"controls\" hidden", basic);
    }

    [TestMethod]
    public void ClientNavigationUpdatesHistoryWithoutNativeAnchorScrolling()
    {
        var html = Generate("""{"Results":[]}""");
        var script = Regex.Match(html, "<script>(.*?)</script>", RegexOptions.Singleline).Groups[1].Value;
        Assert.IsFalse(Regex.IsMatch(script, @"location\.hash\s*=(?!=)"),
            "Hash assignment performs native anchor navigation and scrolls the viewport.");
        Assert.Contains("history.pushState(null, '', '#' + selected)", script);
        Assert.Contains("history.replaceState(null, '', '#' + selected)", script);
        Assert.Contains("window.addEventListener('popstate', historyNavigation)", script);
        Assert.Contains("window.addEventListener('hashchange', historyNavigation)", script);
        var selection = script[script.IndexOf("function select(", StringComparison.Ordinal)..
            script.IndexOf("function applyFilter(", StringComparison.Ordinal)];
        Assert.DoesNotContain("scrollIntoView", selection);
        Assert.DoesNotContain(".focus(", selection);
        var navigation = script[script.IndexOf("function move(", StringComparison.Ordinal)..
            script.IndexOf("copyLink.addEventListener(", StringComparison.Ordinal)];
        Assert.DoesNotContain("scrollIntoView", navigation);
        Assert.DoesNotContain(".focus(", navigation);
    }

    [TestMethod]
    public void DiscoveryLabelsDistinguishSeedMembershipAndLinkToExistingServices()
    {
        var basic = WithoutScript(Generate("""
            {"Results":[
              {"Domain":"source.dn42","Scheme":"http","Port":80,"Reachable":true,"DiscoveredLinks":["https://existing.dn42/","gopher://outside.dn42/"],"Dn42Mentions":["EXISTING.DN42","outside.dn42"]},
              {"Domain":"existing.dn42","Scheme":"https","Port":443,"Reachable":true,"StatusCode":null,"Robots":3}
            ]}
            """));
        Assert.Contains("Discovered hosts outside seed set (1)", basic);
        Assert.Contains("Discovered hosts in seed set (1)", basic);
        Assert.DoesNotContain("Newly discovered", basic);
        var existingId = Regex.Match(basic, "data-service=\"([^\"]+)\"[^>]*data-search=\"existing.dn42").Groups[1].Value;
        Assert.IsFalse(string.IsNullOrEmpty(existingId));
        var hosts = basic[basic.IndexOf("<section class=\"card discovered-hosts\"", StringComparison.Ordinal)..];
        Assert.Contains($"href=\"#{existingId}\"", hosts);
        Assert.Contains("outside.dn42", hosts);
    }

    [TestMethod]
    public void SummaryShowsOnlyAvailableMetadataAndUsesPublicReachability()
    {
        var basic = WithoutScript(Generate("""
            {"StartedAt":"2026-10-03T08:00:00-04:00","FinishedAt":"2026-10-03T12:01:05Z",
             "DurationSeconds":65,"ResolvedDomainsProbed":7,"ProbeTargetCount":28,"ReachableOrigins":19,
             "ExcludedByHostname":2,"ExcludedByPrefix":3,"Results":[
              {"Domain":"good.dn42","Scheme":"http","Port":80,"Reachable":true}
             ]}
            """));
        foreach (var (count, label) in new[] { (7, "domains probed at scan time"), (28, "probes at scan time"),
                     (2, "domains excluded by hostname"), (3, "domains excluded by prefix"), (1, "reachable public origins") })
            Assert.Contains($"<div class=\"stat-value\">{count}</div><div class=\"stat-label\">{label}", basic);
        Assert.Contains("2026-10-03 12:00:00 UTC", basic);
        Assert.Contains("2026-10-03 12:01:05 UTC", basic);
        Assert.Contains("Scan duration: 65 seconds", basic);
        var legacy = WithoutScript(Generate("""{"Results":[]}"""));
        Assert.Contains("No public service results.", legacy);
        Assert.DoesNotContain("Scan duration:", legacy);
        Assert.DoesNotContain("Scan started:", legacy);
        Assert.DoesNotContain("domains excluded by", legacy);
        Assert.DoesNotContain("src=", legacy);
    }

    [TestMethod]
    public async Task PublicArtifactsAndImportedReportsDoNotReintroduceExcludedOrPrivateData()
    {
        using var files = new TestFiles("blocked.dn42", "172.20.16.0/24");
        var raw = """
            {"ExcludedByHostname":0,"ExcludedByPrefix":0,"Results":[
              {"Domain":"blocked.dn42","Scheme":"http","Port":80,"Reachable":true},
              {"Domain":"prefix.dn42","Scheme":"https","Port":443,"Reachable":true,"ProbeAddresses":["172.20.16.8"]},
              {"Domain":"good.dn42","Scheme":"http","Port":80,"Reachable":true,"Robots":3,"RobotsAllowed":false,"ProbeAddresses":["fd42:abcd::99"],
               "Title":"blocked.dn42 prefix.dn42 172.20.16.8","HomepageError":"prefix.dn42",
               "DiscoveredLinks":["https://prefix.dn42/","https://blocked.dn42/","javascript:alert(1)"],
               "Dn42Mentions":["prefix.dn42","blocked.dn42"]}
            ]}
            """;
        var artifacts = PublicArtifactGenerator.Generate(Encoding.UTF8.GetBytes(raw), files.LoadPolicy());
        var input = files.Write("raw.json", raw);
        var output = Path.Combine(files.DirectoryPath, "report.html");
        await AtlasReportGenerator.GenerateAsync(input, output, files.LoadPolicy());
        Assert.AreEqual(artifacts.Html, await File.ReadAllTextAsync(output));
        Assert.AreEqual(raw, await File.ReadAllTextAsync(input));
        foreach (var text in new[] { artifacts.Json, artifacts.Html })
            foreach (var excluded in new[] { "blocked.dn42", "prefix.dn42", "172.20.16.8", "fd42:abcd", "ProbeAddresses", "javascript:alert" })
                Assert.DoesNotContain(excluded, text);
        var basic = WithoutScript(artifacts.Html);
        Assert.HasCount(1, Regex.Matches(basic, "<tr data-service="));
        Assert.Contains("good.dn42", basic);
        Assert.Contains("homepage not fetched / robots restricted", basic);
    }

    private static string Generate(string json) => AtlasReportGenerator.GenerateHtml(JsonNode.Parse(json)!);
    private static string WithoutScript(string html) => Regex.Replace(html, "<script>.*?</script>", "", RegexOptions.Singleline);
    private static string[] Ids(string html) => Regex.Matches(html, "<tr data-service=\"(service-[a-f0-9]+)\"")
        .Select(match => match.Groups[1].Value).ToArray();
}

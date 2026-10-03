using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DN42Atlas.Policy;

namespace DN42Atlas.Reporting;

public static class AtlasReportGenerator
{
    public static async Task GenerateAsync(
        string inputPath,
        string outputPath,
        ExclusionPolicy exclusionPolicy,
        CancellationToken cancellationToken = default)
    {
        var scanJson =
            await File.ReadAllTextAsync(
                inputPath,
                cancellationToken);

        //
        // Validate before embedding it.
        //
        var publicScan = JsonNode.Parse(scanJson) ?? throw new InvalidDataException("Scan JSON must not be null.");
        new PublicScanPolicy(exclusionPolicy).Apply(publicScan);

        var lastScanHtml = string.Empty;
        if (publicScan["GeneratedAt"] is JsonValue generatedAtValue &&
            generatedAtValue.TryGetValue<string>(out var generatedAtText) &&
            DateTimeOffset.TryParse(
                generatedAtText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var generatedAt))
        {
            var generatedAtUtc = generatedAt.ToUniversalTime();
            var machineTimestamp = WebUtility.HtmlEncode(
                generatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            var displayTimestamp = WebUtility.HtmlEncode(
                generatedAtUtc.ToString(
                    "yyyy-MM-dd HH:mm:ss 'UTC'",
                    CultureInfo.InvariantCulture));

            lastScanHtml =
                $"<p class=\"last-scan\">Last scan: <time datetime=\"{machineTimestamp}\">{displayTimestamp}</time></p>";
        }

        // Safe JSON serialization prevents string values from closing the HTML script element.
        scanJson = publicScan.ToJsonString();

        var html = $$"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">

<title>DN42Atlas</title>

<style>
    :root {
        color-scheme: dark;
        font-family:
            ui-monospace,
            SFMono-Regular,
            Menlo,
            Monaco,
            Consolas,
            monospace;
    }

    body {
        margin: 0;
        background: #0d1117;
        color: #c9d1d9;
    }

    header {
        padding: 1rem 1.5rem;
        border-bottom: 1px solid #30363d;
        background: #161b22;
    }

    h1 {
        margin: 0;
        font-size: 1.4rem;
    }

    .subtitle {
        margin-top: .4rem;
        color: #8b949e;
    }

    .site-nav,
    .footer-links {
        display: flex;
        flex-wrap: wrap;
        gap: 1rem;
    }

    .site-nav {
        margin-top: .75rem;
    }

    main {
        max-width: 1200px;
        margin: 0 auto;
        padding: 1.5rem;
    }

    .stats {
        display: grid;
        grid-template-columns:
            repeat(auto-fit, minmax(150px, 1fr));
        gap: .75rem;
        margin-bottom: 1rem;
    }

    .stat,
    .card {
        background: #161b22;
        border: 1px solid #30363d;
        border-radius: 8px;
        padding: 1rem;
    }

    .stat-value {
        font-size: 1.5rem;
        font-weight: bold;
        color: #58a6ff;
    }

    .stat-label {
        color: #8b949e;
        font-size: .85rem;
    }

    .controls {
        display: flex;
        flex-wrap: wrap;
        gap: .5rem;
        align-items: center;
        margin: 1rem 0;
    }

    button,
    input {
        font: inherit;
        color: #c9d1d9;
        background: #21262d;
        border: 1px solid #30363d;
        border-radius: 6px;
        padding: .55rem .75rem;
    }

    button {
        cursor: pointer;
    }

    button:hover {
        background: #30363d;
    }

    input {
        flex: 1;
        min-width: 240px;
    }

    a {
        color: #58a6ff;
    }

    .origin {
        font-size: 1.15rem;
        margin-bottom: .5rem;
        word-break: break-all;
    }

    .title {
        font-size: 1.4rem;
        margin: .5rem 0 1rem 0;
    }

    .meta {
        display: flex;
        flex-wrap: wrap;
        gap: .5rem;
        margin-bottom: 1rem;
    }

    .badge {
        display: inline-block;
        padding: .2rem .45rem;
        border-radius: 5px;
        background: #21262d;
        border: 1px solid #30363d;
        font-size: .8rem;
    }

    .good {
        color: #3fb950;
    }

    .warn {
        color: #d29922;
    }

    .bad {
        color: #f85149;
    }

    details {
        margin-top: 1rem;
    }

    summary {
        cursor: pointer;
        font-weight: bold;
    }

    ul {
        padding-left: 1.5rem;
    }

    li {
        margin: .3rem 0;
        word-break: break-all;
    }

    .dn42 {
        color: #3fb950;
    }

    .clearnet {
        color: #d29922;
    }

    .gopher,
    .gemini {
        color: #bc8cff;
    }

    .muted {
        color: #8b949e;
    }

    .counter {
        margin-left: auto;
        color: #8b949e;
    }

    .discovered-hosts {
        margin-top: 1rem;
    }

    .host-row {
        padding: .5rem 0;
        border-bottom: 1px solid #21262d;
    }

    .host-row:last-child {
        border-bottom: 0;
    }

    .new-host {
        color: #3fb950;
        font-weight: bold;
    }

    .footer {
        color: #8b949e;
        margin-top: 2rem;
        text-align: center;
    }

    .footer p {
        margin: .65rem 0;
    }

    .footer-links {
        justify-content: center;
    }

    .last-scan,
    .hosting-note {
        font-size: .85rem;
    }
</style>
</head>

<body>

<header>
    <h1>DN42Atlas</h1>

    <div class="subtitle">
        A polite map of public stuff on DN42.
    </div>

    <nav
        class="site-nav"
        aria-label="Project information">
        <a href="/about.html">About</a>
        <a href="/opt-out.html">Opt out</a>
        <a href="https://github.com/JoyfulReaper/DN42Atlas">Source</a>
    </nav>
</header>

<main>

<div class="stats">
    <div class="stat">
        <div
            class="stat-value"
            id="seedCount">
            -
        </div>
        <div class="stat-label">
            seed domains
        </div>
    </div>

    <div class="stat">
        <div
            class="stat-value"
            id="originCount">
            -
        </div>
        <div class="stat-label">
            browseable origins
        </div>
    </div>

    <div class="stat">
        <div
            class="stat-value"
            id="linkCount">
            -
        </div>
        <div class="stat-label">
            unique links
        </div>
    </div>

    <div class="stat">
        <div
            class="stat-value"
            id="hostCount">
            -
        </div>
        <div class="stat-label">
            discovered DN42 hosts
        </div>
    </div>

    <div class="stat">
        <div
            class="stat-value"
            id="newHostCount">
            -
        </div>
        <div class="stat-label">
            hosts outside seed set
        </div>
    </div>
</div>

<div class="controls">
    <button id="previous">
        ← Previous
    </button>

    <button id="random">
        Random
    </button>

    <button id="next">
        Next →
    </button>

    <input
        id="search"
        type="search"
        placeholder="Filter hostname or title...">

    <span
        class="counter"
        id="counter">
    </span>
</div>

<section
    class="card"
    id="viewer">
</section>

<section
    class="card discovered-hosts">

    <details>
        <summary>
            Newly discovered DN42 hosts
        </summary>

        <div id="newHosts">
        </div>
    </details>

</section>

<footer class="footer">
    <p>
        DN42Atlas does not automatically follow discovered links.
    </p>

    {{lastScanHtml}}

    <nav
        class="footer-links"
        aria-label="Project information">
        <span>AS4242420425</span>
        <a href="/about.html">About</a>
        <a href="/opt-out.html">Opt out</a>
        <a href="https://github.com/JoyfulReaper/DN42Atlas">Source</a>
    </nav>

    <p class="hosting-note">
        Infrastructure note:
        <a
            href="https://greencloudvps.com/billing/aff.php?aff=10295"
            target="_blank"
            rel="sponsored noopener noreferrer">GreenCloud VPS</a>
        is an affiliate link; the operator may earn a commission.
    </p>
</footer>

</main>

<script>
const scan = {{scanJson}};

const robotsNames = [
    "NotChecked",
    "NoRules",
    "Allowed",
    "Disallowed",
    "Unavailable"
];

const allResults =
    Array.isArray(scan.Results)
        ? scan.Results
        : [];

function originFor(result) {
    if (result.Scheme !== "http" && result.Scheme !== "https")
        return "#";

    const defaultPort =
        (result.Scheme === "http" &&
         result.Port === 80) ||
        (result.Scheme === "https" &&
         result.Port === 443);

    return (
        result.Scheme +
        "://" +
        result.Domain +
        (defaultPort
            ? ""
            : ":" + result.Port) +
        "/"
    );
}

function escapeHtml(value) {
    return String(value ?? "")
        .replaceAll("&", "&amp;")
        .replaceAll("<", "&lt;")
        .replaceAll(">", "&gt;")
        .replaceAll('"', "&quot;")
        .replaceAll("'", "&#39;");
}

function classifyLink(value) {
    try {
        const url =
            new URL(value);

        if (url.protocol === "gopher:")
            return "gopher";

        if (url.protocol === "gemini:")
            return "gemini";

        if (
            url.hostname
                .toLowerCase()
                .endsWith(".dn42"))
        {
            return "dn42";
        }

        return "clearnet";
    }
    catch {
        return "clearnet";
    }
}

const seeds =
    new Set(
        allResults
            .map(x =>
                String(x.Domain ?? "")
                    .toLowerCase())
            .filter(Boolean)
    );

const browseable =
    allResults
        .filter(x =>
            x.Reachable === true &&
            x.StatusCode !== null)
        .sort((a, b) =>
            originFor(a)
                .localeCompare(
                    originFor(b)));

const allLinks =
    new Set();

const discoveredHosts =
    new Map();

function addHost(
    host,
    source,
    discoveryType)
{
    host =
        String(host ?? "")
            .toLowerCase();

    if (!host.endsWith(".dn42"))
        return;

    if (!discoveredHosts.has(host)) {
        discoveredHosts.set(
            host,
            {
                host,
                sources: new Set(),
                types: new Set()
            });
    }

    const item =
        discoveredHosts.get(host);

    item.sources.add(source);
    item.types.add(discoveryType);
}

for (const result of allResults) {
    const source =
        originFor(result);

    for (
        const link of
        result.DiscoveredLinks ?? [])
    {
        allLinks.add(link);

        try {
            const url =
                new URL(link);

            addHost(
                url.hostname,
                source,
                "link");
        }
        catch {
        }
    }

    for (
        const mention of
        result.Dn42Mentions ?? [])
    {
        addHost(
            mention,
            source,
            "mention");
    }
}

const newlyDiscovered =
    Array.from(
        discoveredHosts.values())
    .filter(x =>
        !seeds.has(x.host))
    .sort((a, b) =>
        a.host.localeCompare(b.host));

document
    .getElementById("seedCount")
    .textContent =
        seeds.size;

document
    .getElementById("originCount")
    .textContent =
        browseable.length;

document
    .getElementById("linkCount")
    .textContent =
        allLinks.size;

document
    .getElementById("hostCount")
    .textContent =
        discoveredHosts.size;

document
    .getElementById("newHostCount")
    .textContent =
        newlyDiscovered.length;

const newHostsContainer =
    document.getElementById(
        "newHosts");

newHostsContainer.innerHTML =
    newlyDiscovered.length === 0
        ? `<p class="muted">None.</p>`
        : newlyDiscovered
            .map(item => {
                const sources =
                    Array.from(
                        item.sources)
                    .slice(0, 10);

                return `
                    <div class="host-row">
                        <div class="new-host">
                            ${escapeHtml(item.host)}
                        </div>

                        <div class="muted">
                            discovered from
                            ${item.sources.size}
                            origin(s)
                        </div>

                        <details>
                            <summary>
                                sources
                            </summary>

                            <ul>
                                ${sources
                                    .map(source => `
                                        <li>
                                            ${escapeHtml(source)}
                                        </li>
                                    `)
                                    .join("")}
                            </ul>
                        </details>
                    </div>
                `;
            })
            .join("");

let filtered =
    [...browseable];

let currentIndex = 0;

const search =
    document.getElementById(
        "search");

function applyFilter() {
    const value =
        search.value
            .trim()
            .toLowerCase();

    filtered =
        browseable.filter(result => {
            if (!value)
                return true;

            return (
                String(
                    result.Domain ?? "")
                    .toLowerCase()
                    .includes(value) ||

                String(
                    result.Title ?? "")
                    .toLowerCase()
                    .includes(value) ||

                String(
                    result.ContentType ?? "")
                    .toLowerCase()
                    .includes(value)
            );
        });

    currentIndex = 0;

    render();
}

function render() {
    const viewer =
        document.getElementById(
            "viewer");

    const counter =
        document.getElementById(
            "counter");

    if (filtered.length === 0) {
        counter.textContent = "0 / 0";

        viewer.innerHTML =
            `<p>No matching origins.</p>`;

        return;
    }

    if (currentIndex < 0)
        currentIndex =
            filtered.length - 1;

    if (
        currentIndex >=
        filtered.length)
    {
        currentIndex = 0;
    }

    const result =
        filtered[currentIndex];

    const origin =
        originFor(result);

    counter.textContent =
        `${currentIndex + 1} / ${filtered.length}`;

    const links =
        result.DiscoveredLinks ?? [];

    const mentions =
        result.Dn42Mentions ?? [];

    const statusClass =
        result.StatusCode >= 200 &&
        result.StatusCode < 400
            ? "good"
            : result.StatusCode >= 400
                ? "warn"
                : "";

    const linkHtml =
        links.length === 0
            ? `<p class="muted">No links discovered.</p>`
            : `
                <ul>
                    ${links
                        .map(link => {
                            const kind =
                                classifyLink(link);

                            return `
                                <li>
                                    <span
                                        class="badge ${kind}">
                                        ${escapeHtml(kind)}
                                    </span>

                                    <a
                                        href="${escapeHtml(link)}"
                                        target="_blank"
                                        rel="noopener noreferrer">
                                        ${escapeHtml(link)}
                                    </a>
                                </li>
                            `;
                        })
                        .join("")}
                </ul>
            `;

    const mentionHtml =
        mentions.length === 0
            ? `<p class="muted">No DN42 names mentioned.</p>`
            : `
                <ul>
                    ${mentions
                        .map(host => `
                            <li class="dn42">
                                ${escapeHtml(host)}
                            </li>
                        `)
                        .join("")}
                </ul>
            `;

    viewer.innerHTML = `
        <div class="origin">
            <a
                href="${escapeHtml(origin)}"
                target="_blank"
                rel="noopener noreferrer">
                ${escapeHtml(origin)}
            </a>
        </div>

        <div class="title">
            ${escapeHtml(
                result.Title ||
                "(no title)")}
        </div>

        <div class="meta">

            <span class="badge ${statusClass}">
                HTTP
                ${escapeHtml(result.StatusCode)}
            </span>

            <span class="badge">
                ${escapeHtml(
                    result.ContentType ||
                    "unknown content type")}
            </span>

            <span class="badge">
                robots:
                ${escapeHtml(
                    robotsNames[
                        result.Robots
                    ] ??
                    result.Robots)}
            </span>

            <span class="badge">
                ${links.length}
                links
                ${result.LinksTruncated
                    ? "+"
                    : ""}
            </span>

            <span class="badge">
                ${mentions.length}
                DN42 mentions
                ${result.Dn42MentionsTruncated
                    ? "+"
                    : ""}
            </span>

        </div>

        ${
            result.HomepageRedirectLocation
                ? `
                    <p>
                        <strong>Redirect:</strong>
                        ${escapeHtml(
                            result.HomepageRedirectLocation)}
                    </p>
                `
                : ""
        }

        ${
            result.HomepageError
                ? `
                    <p class="bad">
                        Homepage error:
                        ${escapeHtml(
                            result.HomepageError)}
                    </p>
                `
                : ""
        }

        ${
            result.ContentTruncated
                ? `
                    <p class="warn">
                        Homepage body was truncated
                        during discovery.
                    </p>
                `
                : ""
        }

        <details>
            <summary>
                Discovered links
                (${links.length}${result.LinksTruncated ? "+" : ""})
            </summary>

            ${linkHtml}
        </details>

        <details>
            <summary>
                DN42 mentions
                (${mentions.length}${result.Dn42MentionsTruncated ? "+" : ""})
            </summary>

            ${mentionHtml}
        </details>
    `;
}

document
    .getElementById("previous")
    .addEventListener(
        "click",
        () => {
            currentIndex--;
            render();
        });

document
    .getElementById("next")
    .addEventListener(
        "click",
        () => {
            currentIndex++;
            render();
        });

document
    .getElementById("random")
    .addEventListener(
        "click",
        () => {
            if (filtered.length === 0)
                return;

            currentIndex =
                Math.floor(
                    Math.random() *
                    filtered.length);

            render();
        });

search.addEventListener(
    "input",
    applyFilter);

document.addEventListener(
    "keydown",
    event => {
        if (
            document.activeElement ===
            search)
        {
            return;
        }

        if (event.key === "ArrowLeft") {
            currentIndex--;
            render();
        }

        if (event.key === "ArrowRight") {
            currentIndex++;
            render();
        }

        if (event.key === "r") {
            if (filtered.length === 0)
                return;

            currentIndex =
                Math.floor(
                    Math.random() *
                    filtered.length);

            render();
        }
    });

render();
</script>

</body>
</html>
""";

        await File.WriteAllTextAsync(
            outputPath,
            html,
            Encoding.UTF8,
            cancellationToken);
    }
}

# DN42Atlas

DN42Atlas is a small .NET 10 tool for discovering and browsing publicly reachable services on the [DN42](https://dn42.dev/) network.

It starts with registered `.dn42` domains, resolves them through a DN42-aware DNS resolver, probes a curated set of HTTP/HTTPS ports, respects `robots.txt`, and records basic metadata such as page titles, redirects, links, and referenced `.dn42` hostnames.

It can also turn a scan result into a self-contained HTML viewer for casually flipping through public DN42 services.

This is primarily a hobby project for exploring DN42 and learning more about routing, service discovery, and the weird little corners of private networks.

## What It Does

DN42Atlas currently has three main stages:

1. Parse DN42 registry DNS objects.
2. Resolve registered `.dn42` names.
3. Probe reachable web services and optionally inspect their homepage.

For web services, Atlas records information such as:

- hostname
- scheme and port
- whether the origin responded
- `robots.txt` status
- homepage HTTP status
- page title
- content type
- redirects
- discovered links
- `.dn42` hostname mentions
- truncation status for large pages/link sets

Scan results are stored as JSON.

DN42Atlas can then ingest that JSON and generate a self-contained HTML browser for the results.

## Polite Discovery

DN42Atlas is intended to be a relatively conservative discovery tool, not a vulnerability scanner.

For each potential HTTP/HTTPS origin it:

1. Requests `/robots.txt`.
2. Does not automatically follow redirects.
3. Fetches `/` only when robots rules explicitly permit it or no robots file exists.
4. Does not recursively crawl discovered links.
5. Does not attempt authentication.
6. Does not brute-force paths.
7. Does not perform vulnerability checks.
8. Uses bounded concurrency and short timeouts.
9. Limits the amount of homepage content it reads.

The current scanner identifies itself as:

```text
DN42Atlas/0.1 (+https://joyfulreaper.dn42/)
```

If `robots.txt` cannot be evaluated safely, Atlas does not fetch the homepage.

Discovered links are recorded for later analysis but are not automatically followed.

## Complete Exclusion

Atlas loads two required files from the current working directory before executing any resolution, scan, probe, or report command. Help and unknown commands do not load configuration or perform network work:

```text
config/excluded-hosts.txt
config/excluded-prefixes.txt
```

Keep both files present, even if they contain no rules. If either file is missing, Atlas fails closed: it stops rather than continuing without exclusions. Malformed CIDR rules also stop startup.

Use one rule per line. Blank lines are ignored, and `#` starts a comment, including after a rule.

Hostname rules go in `config/excluded-hosts.txt`, for example:

```text
example.dn42        # this exact hostname
*.private.dn42     # descendants of private.dn42
```

Exact rules match only the named host. Wildcard rules match both `foo.private.dn42` and `bar.foo.private.dn42`, but do not match the bare parent `private.dn42`. Add a separate exact rule to exclude the parent too. Matching ignores case, surrounding whitespace, and a trailing dot.

IPv4 and IPv6 CIDR rules go in `config/excluded-prefixes.txt`, for example:

```text
172.20.16.0/20
fd42:1234::/48
```

During the default registry/DNS command, hostname exclusions are checked before DNS resolution. If any resolved address matches an excluded prefix, the entire domain is omitted from `domain-resolution.json`.

`web-scan` checks hostname exclusions again when loading an existing resolution file. It then checks every saved address against prefix exclusions before probing. A hostname match or any matching address skips the entire domain: excluded scan targets are not probed and do not appear as service results in the scan JSON or its generated viewer.

The public scan JSON records `ExcludedByHostname` and `ExcludedByPrefix` counts, without publishing the matched excluded hostnames or prefix rules. Excluded links and hostname mentions are removed; excluded identifiers in other metadata are redacted. This filtering does not resolve or probe discovered references. Console diagnostics can include the matched hostname, address, and rule.

`robots.txt` controls HTTP page-content fetching. Atlas still requests `/robots.txt` and can record the origin and robots status when homepage fetching is disallowed. Complete exclusion removes the target from the scan entirely, before those HTTP requests.

`probe-test` also checks hostname exclusions before DNS and validates the entire resolved address set before probing. `report` reapplies current hostname exclusions and reference redaction when generating HTML, without changing the input snapshot or making network requests. Historical service results do not include destination addresses, so their current prefix membership cannot be verified offline; review old snapshots before publishing them under a changed prefix policy.

## DN42 Address Guard

Before probing a hostname from a resolution file, `web-scan` requires a valid DNS hostname ending in `.dn42` and verifies that all its saved addresses fall within expected DN42 address space. Conflicting duplicate rows cannot override an exclusion or an unsafe address set.

Currently accepted ranges include:

```text
172.20.0.0/14
172.31.0.0/16
10.100.0.0/14
10.127.0.0/16
fd00::/8
```

Domains with any address outside these ranges, including mixed DN42/external results, are skipped rather than allowing DNS records to direct the scanner onto arbitrary clearnet hosts.

HTTP connections are pinned to the validated addresses while retaining the hostname for HTTP Host and TLS SNI. `web-scan` does not resolve the hostname again during probing or use ambient HTTP proxies. A stale resolution file can therefore produce failed probes rather than silently switching to a new destination; regenerate DNS results when needed.

## Requirements

- .NET 10 SDK
- access to DN42
- a DNS resolver capable of resolving `.dn42`
- a local checkout of the DN42 registry
- both exclusion files in `config/` under the current working directory

The local registry checkout is needed for the default resolution command. Resolution and probing need access to DN42; `web-scan` reads saved DNS results instead of the registry. Generating a report from an existing JSON file does not need network access, but still requires both exclusion files at startup.

The registry is currently expected at:

```text
~/dn42-registry
```

Specifically:

```text
~/dn42-registry/data/dns
```

## Build

From the repository root:

```bash
dotnet build DN42Atlas/DN42Atlas.csproj
```

## Architecture and Tests

The project is split by responsibility:

```text
DN42Atlas/
  Program.cs
  Commands/
    ResolveCommand.cs
    WebScanCommand.cs
    ProbeTestCommand.cs
    ReportCommand.cs
    RunCommand.cs
    CommandUsage.cs
  Scanning/
    RegistryResolver.cs
    RegistryResolutionResult.cs
    WebScanner.cs
    WebScanResult.cs
  Networking/
    Dn42AddressSpace.cs
    ProbeDestination.cs
    PinnedHttpConnection.cs
  Probing/
    HttpProber.cs
    HttpProbeTargets.cs
  Policy/
    ExclusionPolicy.cs
    PublicScanPolicy.cs
  Registry/
    DomainParser.cs
    DomainObject.cs
    DomainResolution.cs
    HttpProbeResult.cs
  Reporting/
    AtlasReportGenerator.cs
DN42Atlas.Tests/
config/
  excluded-hosts.txt
  excluded-prefixes.txt
```

`Program.cs` loads the required exclusion policy and shared HTTP target list, then dispatches commands. Commands handle CLI arguments, output files, report generation, and summaries. `RegistryResolver` parses registry objects and resolves DNS; `WebScanner` filters saved resolutions and coordinates HTTP probes with concurrency limited to 32. Their result records carry data back to the commands without changing the public JSON schema. `Dn42AddressSpace` holds the address guard.

`Probing/HttpProber.cs`, `Policy/ExclusionPolicy.cs`, the registry models/parser,
and `Reporting/AtlasReportGenerator.cs` retain their existing responsibilities.
Networking also validates probe destinations and pins HTTP connections; `PublicScanPolicy` filters public metadata and report inputs.
Both exclusion files remain required for every operational command. Registry hostname
exclusions run before DNS; saved-resolution hostname exclusions, prefix exclusions,
and the DN42 address guard run before web probing.

Run the automated tests from the repository root:

```bash
dotnet test
```

Tests use temporary fixtures, fake DNS/probe functions, HTTP responses, and in-memory connection streams; they do not contact DN42
or external services. MSTest is used only by the test project; CIDR matching uses
the existing implementation without additional networking packages.

## Commands

The CLI command structure is:

```text
dn42atlas                              Registry parsing and DNS resolution
dn42atlas resolve                      Explicit registry/DNS resolution
dn42atlas web-scan [resolution-file]    HTTP/HTTPS scan from saved DNS results
dn42atlas probe-test                    Single-host HTTP/HTTPS probe test
dn42atlas report <web-probe.json>        HTML viewer from existing scan JSON
dn42atlas run                           Resolve, scan fresh results, generate HTML
dn42atlas --help | -h | help             Show usage without scanning
```

The examples below use `dotnet run --project DN42Atlas` from the repository root. Generated resolution and scan files are written under the current working directory.

Unknown commands print usage and exit with code 2. Missing report arguments also return 2; missing input files and other command failures return 1. Successful commands return 0. Individual DNS lookup failures and unreachable HTTP origins remain recorded scan outcomes rather than failing the entire command.

### Combined Run

```bash
dotnet run --project DN42Atlas -- run
```

`run` resolves the registry into `domain-resolution.json`, passes that exact file to `web-scan`, and generates the corresponding HTML viewer through the existing web-scan report stage. It stops if a stage fails. It does not use the standalone web-scan default backup file, schedule future scans, or publish output.

For unattended execution, set the working directory explicitly: exclusion files, relative input paths, `domain-resolution.json`, and `results/` depend on it. The registry is read from `~/dn42-registry/data/dns` for the account running Atlas. Commands currently have no pipeline-wide cancellation support. Resolution output overwrites its fixed filename; scan filenames use local start time to the second, so concurrent runs can collide. Output writes are not atomic publication. These limitations remain for future pipeline work.

### Registry / DNS Scan

Running without arguments, or using `resolve`, parses the registry and resolves registered `.dn42` domains:

```bash
dotnet run --project DN42Atlas
```

The result is written to:

```text
domain-resolution.json
```

### Single-Host Probe Test

For development, Atlas can probe `burble.dn42` across the configured port list:

```bash
dotnet run --project DN42Atlas -- probe-test
```

This is useful for checking HTTP probing, TLS handling, robots parsing, homepage metadata extraction, and link discovery before running a full scan.

Results are printed to the console; this command does not write scan JSON or an HTML viewer. It requires both exclusion files and skips its fixed host when hostname, prefix, or DN42 address checks reject it.

### Web Scan

Use an existing DNS-resolution file:

```bash
dotnet run --project DN42Atlas -- web-scan
```

The default source file is:

```text
domain-resolution.json.bk2
```

A different file can be supplied:

```bash
dotnet run --project DN42Atlas -- web-scan path/to/domain-resolution.json
```

Results are written under:

```text
results/
```

For example:

```text
results/web-probe-20261003-002948.json
```

The scan also generates a corresponding HTML viewer:

```text
results/web-probe-20261003-002948.html
```

The filename timestamp comes from the scan's local start time. Results are ordered by domain, scheme, then port.

To preserve console output as well:

```bash
mkdir -p results

dotnet run --project DN42Atlas -- web-scan \
  2>&1 | tee "results/web-scan-console-$(date +%Y%m%d-%H%M%S).log"
```

## Generate a Viewer From an Existing Scan

You do not need to rescan the network to generate the HTML viewer.

The command is `report <web-probe.json>`. It filters supplied results and references against current hostname exclusions, safely embeds the JSON, and writes an HTML file alongside the JSON with the same basename. It does not alter the source snapshot or re-probe services; historical prefix limitations are described under Complete Exclusion.

```bash
dotnet run --project DN42Atlas -- \
  report results/web-probe-20261003-002948.json
```

This creates:

```text
results/web-probe-20261003-002948.html
```

On a Linux desktop:

```bash
xdg-open results/web-probe-20261003-002948.html
```

## HTML Atlas Viewer

The generated report is a self-contained HTML file.

It supports:

- Previous / Next navigation
- random origin selection
- hostname/title filtering
- direct links to discovered services
- HTTP status and content type
- robots status
- page title
- discovered links
- `.dn42` hostname mentions
- a list of discovered DN42 hosts outside the original seed set

Keyboard shortcuts:

```text
Left Arrow   Previous origin
Right Arrow  Next origin
R            Random origin
```

No framework or build system is required for the report.

It is intentionally just HTML, CSS, and a little JavaScript.

## Currently Probed Web Ports

The current curated list includes:

```text
HTTP:
80
81
3000
3001
4000
5000
5001
7000
8000
8001
8008
8080
8081
8088
8880
8888
9000
9090

HTTPS:
443
4443
8443
9443
10443
```

The goal is not to sweep every TCP port.

The list is intentionally biased toward ports commonly used for web applications and self-hosted services.

## Link Discovery

When homepage access is permitted, Atlas currently extracts:

- HTML `href` links
- plain HTTP URLs
- HTTPS URLs
- other supported URL text found in page content
- `.dn42` hostname mentions

Relative links are resolved against the source origin.

Fragments are removed.

`mailto:` and `javascript:` links are ignored.

The current limits are:

```text
Homepage body:       256 KiB
robots.txt body:      64 KiB
Discovered links:       250
DN42 mentions:           250
```

The JSON records when these limits caused truncation.

Recording a link does not mean Atlas probes its protocol. Discovery records references from HTTP/HTTPS pages; links are not automatically followed.

## Current Scope

DN42Atlas currently probes HTTP/HTTPS services only.

Planned or possible future work includes:

- deduplicated discovered-host graph
- showing which pages referenced each discovered host
- optional second-layer discovery
- better service classification
- Gopher probing
- Gemini probing
- Finger probing
- QOTD probing
- scan profiles such as `core`, `extended`, and `all`
- comparing scan history over time
- hosted Atlas UI
- richer topology and relationship visualization

Any future recursive discovery should continue to use conservative limits and respect the policy of the destination service.

## Example Findings

A scan can uncover the usual mixture of DN42 infrastructure, personal pages, experiments, dashboards, and self-hosted software.

Examples observed during development included services identifying themselves as:

- Forgejo
- Gitea
- SearXNG
- CyberChef
- Portainer
- Vaultwarden
- qBittorrent WebUI
- BlueMap
- Kasm
- looking glasses
- network dashboards
- blogs
- personal homepages
- authentication services
- DN42-specific discovery sites

These names are based only on publicly returned page metadata.

DN42Atlas does not attempt to log in or inspect private application state.

## Why?

Mostly because DN42 is full of interesting things and manually finding them is annoying.

The registry gives you names and network resources, but it does not necessarily tell you what people are actually running on them.

DN42Atlas tries to bridge that gap without turning into a hostile scanner.

The end goal is something closer to:

```text
"What public stuff exists on DN42 right now?"
```

than:

```text
"How many ways can I annoy every router operator?"
```

## Status

Very experimental.

Expect:

- changing JSON formats
- false positives
- disappearing services
- temporary DNS failures
- strange certificates
- unusual HTTP implementations
- servers that respond differently between scans
- code written primarily because it seemed fun at the time

That is also approximately the expected operating environment of DN42.

## License

DN42Atlas is licensed under the GNU Affero General Public License v3.0.

See [`LICENSE`](LICENSE) for details.

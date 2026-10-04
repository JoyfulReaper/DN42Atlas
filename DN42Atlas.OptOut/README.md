# DN42Atlas operator self-service

`DN42Atlas.OptOut` authenticates operators with Auth42 and permits exclusion and re-inclusion of exact registered `.dn42` domains and `inetnum`/`inet6num` allocations whose `mnt-by` matches the authenticated active maintainer. OIDC establishes identity; only the current local canonical registry authorizes a resource. The application owns the private SQLite audit store, materializes runtime policy, and republishes the recorded scan without crawling. Wildcard self-service exclusions, delegated domains, and ASN-wide exclusions are not supported.

Configure the application with:

- `DN42ATLAS_OIDC_CLIENT_ID`
- `DN42ATLAS_OIDC_CLIENT_SECRET`
- `DN42ATLAS_OIDC_AUTHORITY` (currently `https://auth.iedon.net`)
- `DN42ATLAS_REGISTRY_PATH` (the root of a local DN42 registry checkout)
- `DN42ATLAS_REGISTRY_MAX_AGE_HOURS` (optional; defaults to `72`)
- `DN42ATLAS_EXCLUSION_DB_PATH` (the private SQLite audit database)
- `DN42ATLAS_MANUAL_REQUEST_DB_PATH` (a separate, initialized private SQLite request database)
- `DN42ATLAS_RUNTIME_EXCLUSIONS_PATH` (the active-rules JSON consumed by Atlas)
- `DN42ATLAS_EXCLUDED_HOSTS_PATH` (the required manual hostname rules)
- `DN42ATLAS_EXCLUDED_PREFIXES_PATH` (the required manual prefix rules)
- `DN42ATLAS_PUBLISHED_PATH` (the generated static web root)
- `DN42ATLAS_PUBLICATION_STATE_PATH` (private state recording the raw scan and SHA-256)

All filesystem paths used by the web mutation service must be explicit absolute paths. The manual files, SQLite database, runtime bundle, and publication state must have distinct paths outside the public web root. Do not use aliases or links into the served directory. No paths are inferred from the working directory or by walking up from the content root. For example, manual rules and public output can live under `/opt/DN42Atlas`, while the database, runtime bundle, and publication state live under `/var/lib/dn42atlas`. Core CLI commands retain their existing working-directory defaults.

The configured registry checkout must contain its domain objects under `data/dns`. The operator dashboard is served at `/operator`, and the registered OIDC callback remains `/signin-oidc`. The client secret must be supplied through deployment configuration and must not be committed.

In production, nginx should proxy only these application routes:

- `/operator`
- `/login`
- `/logout`
- `/signin-oidc`
- `/contact` (anonymous manual request form)

All other paths on `dn42atlas.dn42`, including `/`, should continue to be served by the static site. Publish the proxied routes through HTTPS so OIDC redirects and secure cookies work correctly.

## Manual requests and notifications

Authenticated self-service is preferred. Anonymous `GET /contact` and `POST /contact` provide a manual-review fallback for urgent opt-outs, broader or wildcard requests, ownership/authentication problems, corrections, and other concerns. Submitting a request never changes exclusions, runtime policy, publication state, or crawl results. There is no HTTP endpoint for reading private requests.

The form requires Resource and Contact (each at most 255 characters), a RequestType of `OptOut`, `BroaderOrWildcard`, `OwnershipOrAuthentication`, `Correction`, or `Other`, and Message (at most 2000 characters). Contact may be any human contact string, not just email. Values are trimmed; control characters are rejected except newlines and tabs in Message. Submitted values are never redisplayed in HTML or placed in redirect URLs. There are no attachments or URL fetches.

POST requires an antiforgery cookie/token pair, an empty `Website` honeypot, and exactly the six expected form fields. Only UTF-8 `application/x-www-form-urlencoded` is accepted, with a 32 KiB body limit including chunked requests. A filled honeypot receives the generic redirect without storing or notifying. A process-local global limit permits ten POST attempts per minute, including invalid attempts; excess attempts return 429. It does not use client IPs or `X-Forwarded-For`. This simple shared budget resets on restart and is not coordinated across processes.

Initialize request storage explicitly before web startup:

```text
DN42ATLAS_MANUAL_REQUEST_DB_PATH=/var/lib/dn42atlas/manual-requests.sqlite
DN42ATLAS_PUBLISHED_PATH=/opt/DN42Atlas/published
dotnet run --project DN42Atlas.OptOut -- manual-requests-init
```

Supply these values through your deployment environment. Both paths must be absolute. The database must be outside the public root, separate from exclusion storage and other policy/state files; do not use filesystem aliases or links into the web root. Initialization refuses to overwrite any existing database. Startup validates existing storage and never creates a replacement for missing storage. New databases use owner read/write permissions (`0600`) on Unix; existing permissions and Windows ACLs are unchanged. Restrict access to the database and its directory to the service/operator accounts.

The version-1 `ManualRequests` table records Id, CreatedUtc, Resource, Contact, RequestType, Message, Status, and nullable ReviewedUtc. New records are `Pending`; operators can change them to `Reviewed`, `Resolved`, or `Rejected`. Records and the full message remain private and are never serialized into public Atlas artifacts. Friendly labels in the contact form do not change the canonical request-type strings stored in SQLite.

Web startup also requires the standard JoyfulReaperLib.Ntfy configuration, supplied through environment variables, for example:

```text
Ntfy__ServerUrl=https://ntfy.kgivler.com
Ntfy__Topic=<private-topic>
Ntfy__AccessToken=<deployment-secret>
Ntfy__Timeout=00:00:05
```

Use a private topic and supply the bearer token through deployment secrets, never source control. The library validates the server URL, topic, and positive timeout at startup. Its default timeout is ten seconds. The application references only the ntfy project from the pinned `external/JoyfulReaperLib` Git submodule; initialize submodules before building. No custom notification HTTP client is used.

A valid request commits to SQLite first, then completes a redirect to `/contact?result=recorded`, then attempts notification. The notification title is `DN42Atlas manual request`, priority is High, and tags are `dn42` and `atlas`. Its short summary includes request type, resource, contact, and request ID; the full Message and a click URL are omitted. Those summary fields are sent to the configured notification server. Notification failures log a warning without exposing token or message contents and preserve the saved request and successful response. There are no notification retries or durable queue: a process failure between saving and notifying can leave a pending request without a notification.

Review requests locally, even when notifications are unavailable:

```text
dotnet run --project DN42Atlas.OptOut -- manual-requests
dotnet run --project DN42Atlas.OptOut -- manual-request <id>
dotnet run --project DN42Atlas.OptOut -- manual-request-status <id> <Pending|Reviewed|Resolved|Rejected>
dotnet run --project DN42Atlas.OptOut -- manual-request-delete <id>
```

The first command lists pending request summaries without Message; the second shows a selected record including its full message and ReviewedUtc. Status names are exact and case-sensitive, and IDs must be positive integers. Invalid arguments return 2; nonexistent records return 1. Leaving Pending sets ReviewedUtc to the current UTC time. Subsequent changes between reviewed states preserve that timestamp; explicitly setting Pending clears it. Deletion permanently removes only the selected manual request. These commands require only the request database and published-root settings, and run before OIDC, registry, or ntfy web startup validation. Status changes and deletion never mutate exclusions, runtime policy, scan state, or public artifacts, and do not send notifications. No schema migration is needed.

For interactive review on a Bash deployment, use the executable helper at the repository root:

```bash
./dn42atlas-request-admin
./dn42atlas-request-admin /path/to/deployment.env
```

It loads `~/.config/dn42atlas/oidc.env` when present. Override that path with its optional argument or `DN42ATLAS_ADMIN_ENV_FILE`; an explicitly selected missing file is an error. Without a default file it uses the existing environment. The file is trusted shell configuration and is sourced with automatic export; keep it private and do not source untrusted files. Environment contents are not printed. The helper locates its repository directory, runs the OptOut CLI with `--no-launch-profile`, and requires Bash and the .NET SDK, not sqlite3. It offers pending list, detail, status selection, and deletion. Deletion shows the full request first and requires typing `DELETE` exactly. CLI failures are reported and return to the menu.

The current preview source pages already link to `/operator` and `/contact`. Deployment routes `/contact` to this application over HTTPS; Atlas does not install proxy configuration. Separately, generated `published/` support pages are intentionally preserved on republish; older installed copies may need updating. The preview site remains separate from generated listings until no earlier than October 16, 2026.

## Persistent deployment

The deployed systemd service runs `/usr/bin/dotnet /opt/dn42atlas-optout/current/DN42Atlas.OptOut.dll`, with OptOut listening on `127.0.0.1:5078` behind nginx. These are deployment settings; the source does not hard-code the listening address or include the unit/nginx configuration. Keep private database, policy, publication-state, registry and service-account Data Protection key paths persistent across releases. ASP.NET Core uses its default Data Protection configuration; ensure the account's persistent home/key directory is available and protected. Do not place secrets or private state inside release directories or served roots.

From a clean non-root deployment checkout, run `./deploy-optout.sh`. It pulls `--ff-only`, updates pinned submodules, publishes Release into `/opt/dn42atlas-optout/releases/<timestamp>-<sha>`, atomically replaces the `current` symlink, restarts the installed service, and verifies its active state. If no service is installed it skips restart. It does not source the OIDC env file or alter nginx, the systemd unit, or static preview content.

After successful activation and the service check, retention keeps current plus the three newest other completed releases. `DN42ATLAS_OPTOUT_RETAIN_PREVIOUS=0` keeps current only; the default is `3`. Values must be non-negative integers. Current is explicitly preserved even when it is older than other releases. Only direct real directories matching the script's release naming convention are eligible; hidden temps, symlinks and unrelated files/directories are ignored. Pruning failure returns nonzero with a clear error, while leaving the newly active deployment in place. Failed publish/activation cleans only this deployment's temporary directory/link; failed restart does not prune and does not automatically restore the old current symlink.

See [the architecture guide](../docs/ARCHITECTURE.md) for deployment boundaries, layout and recovery. Source edits affect the served source preview when those pages are the web root, but OptOut binary changes require deployment. No public listing launch is performed by this script.

## Registry freshness

The configured local checkout is the sole registry authority for request-time ownership checks. Login, `/operator`, and authorization checks never fetch registry data from the network.

Production must use a dedicated checkout intended only for DN42Atlas, for example:

```text
/var/lib/dn42atlas/registry
```

Set `DN42ATLAS_REGISTRY_PATH` to that checkout and explicitly configure:

```text
DN42ATLAS_REGISTRY_UPSTREAM=origin/master
```

Do not use a developer or operator working clone. `registry-update` hard-resets the checkout to its configured upstream, so human feature branches and manual edits must never share this directory. The application reads and the updater writes the same dedicated snapshot.

Run the explicit updater approximately once every 24 hours:

```text
dotnet run --project DN42Atlas -- registry-update
```

The updater fetches and prunes `origin`, then resets the clean local checkout to its configured upstream branch. It can discover the branch from Git's upstream configuration for development use, but production should set `DN42ATLAS_REGISTRY_UPSTREAM` explicitly. Concurrent updates are prevented by an exclusive lock in Git metadata. A successful update records the observed commit and UTC time under `.git`, without dirtying the checkout.

Snapshots older than `DN42ATLAS_REGISTRY_MAX_AGE_HOURS` are stale. A modified or untracked working-tree file makes the snapshot dirty and unsafe. Missing or unreadable Git state, observation metadata, or commit identity produces an unknown and unsafe freshness state. Automatic approval fails closed for stale, dirty, or unknown snapshots; previously established exclusions remain effective. This application does not schedule or trigger registry updates from web requests.

## Exclusion storage and materialization

Initialize exclusion storage explicitly during deployment:

```text
dotnet run --project DN42Atlas.OptOut -- exclusions-init
```

This command requires `DN42ATLAS_EXCLUSION_DB_PATH` and `DN42ATLAS_RUNTIME_EXCLUSIONS_PATH`. It creates the versioned SQLite schema and an empty, valid runtime policy. It refuses to overwrite a non-empty database or an existing runtime policy. Normal web startup only opens and validates an existing database; it never recreates a missing database.

Regenerate the runtime policy from the existing database with:

```text
dotnet run --project DN42Atlas.OptOut -- exclusions-materialize
```

Materialization reads only active records and atomically replaces one versioned JSON file. The runtime file contains only normalized hostname and prefix rules; authenticated subjects, maintainers, ASNs, registry evidence, and other audit data remain private in SQLite. Revocation preserves the database record while omitting it from later materializations.

Atlas continues to require its repository-managed manual host and prefix files. Set `DN42ATLAS_RUNTIME_EXCLUSIONS_PATH` for resolution, scan, probe, and report commands to combine the materialized rules with those manual rules. When that setting is absent, existing manual-only behavior is unchanged. When it is set, a missing, malformed, unsupported, unreadable, or invalid runtime policy stops the command rather than silently dropping self-service exclusions.

There is no maintenance command that adds or revokes an exclusion. These mutations require authenticated, authorized `POST /operator`.

## Operator requests

Authenticated `GET /operator` lists only resources maintained by the reduced cookie identity's active maintainer. The dashboard classifies active SQLite self-service records separately from manual policy:

- **Included** resources offer **Exclude from DN42Atlas**.
- **Excluded by self-service** resources offer **Include again**.
- **Excluded by operator policy** resources offer no mutation controls, including when a self-service record also exists.

Manual exact and wildcard hostname rules always win. A manual CIDR that fully contains an exact registered allocation also prevents re-inclusion; the comparison uses address family and prefix containment rather than string equality. Runtime DB rules are never classified as manual rules. Unsafe registry snapshots or unavailable ownership/storage/policy data disable mutation controls. The page displays freshness but not subjects, DB IDs, private registry evidence, or other operators' audit records. Dashboard, confirmation, and status responses use `Cache-Control: no-store` and all rendered values are HTML-encoded.

The same `/operator` route accepts both confirmation and final POSTs. Every form includes `intent`, `resourceType`, `resourceValue`, and an ASP.NET Core antiforgery token. Its cookie is Secure, HttpOnly, SameSite=Strict, and scoped to `/`. POST explicitly validates the cookie/request-token pair and reduced authenticated identity before acquiring the mutation lock. Missing or invalid tokens fail without changes. No mutation is performed by GET, and no new proxy route is needed.

The first POST uses `confirm-exclude` or `confirm-include`. It normalizes the resource, obtains a new Fresh snapshot, checks current exact ownership and operation availability, then renders a server-side confirmation page without changing SQLite, runtime policy, or public artifacts. Cancel returns to `/operator`. The second POST uses `exclude` or `include` and adds `confirmationToken`.

ASP.NET Core Data Protection authenticates and encrypts the confirmation token under a versioned purpose. It binds the operation, normalized resource type/value, subject, active maintainer, and issuance time, and expires after five minutes. Inclusion also binds the active record identity inside the protected token. Exclusion binds the latest historical record ID for that exact resource, including revoked records, or null if it has never been excluded. A raw DB ID is never rendered separately or accepted as browser authority. Tampered, expired, mismatched, missing, or future-dated tokens are rejected. Tokens do not authorize mutations: final POSTs obtain a new Fresh snapshot and repeat exact current ownership checks inside the same mutation gate. Ordinary final success redirects to `/operator?result=excluded` or `/operator?result=included`; resources, confirmation data, and audit identities never enter query strings.

Every POST parses and normalizes the resource on the server. The application rejects wildcards, non-DN42 domains, wrong address families, malformed prefixes, unreasonable lengths, and unknown types. Under a singleton semaphore, it obtains a new Fresh snapshot, reads current registry ownership, and checks the exact domain or canonical allocation against `mnt-by`. A second snapshot check rejects a checkout change during that read. Previously rendered resources, ASN values, Auth42 routes, DNS, and BGP do not authorize a request. Broader and narrower prefixes are rejected unless independently registered and maintained as exact resources.

**Include again** revokes only an active self-service exclusion. The current registry owner can revoke a previous owner's exclusion; matching the original subject or maintainer is not required. The original row remains in SQLite with `RevokedUtc` set and its creation identity, ASN, and registry evidence unchanged. Manual rules are never edited or bypassed. Inclusion regenerates the Atlas from the recorded raw scan, so historical service data may immediately reappear, and future scans may probe the resource according to the remaining effective policy. The raw scan bytes and original `GeneratedAt` stay unchanged. Replaying an inclusion after revocation returns a conflict; an old token cannot revoke a later exclusion record for the same resource. Exclusion remains idempotent.

An exclusion replay while the resource remains actively excluded reconciles policy and publication without inserting another row. When no active exclusion exists, final exclusion must match the token's historical record ID under the mutation gate; a changed generation returns a conflict before any mutation. Thus an exclude/include cycle invalidates earlier exclusion confirmations, and a fresh confirmation is needed to exclude again. Already-excluded resources do not issue new exclusion confirmations, because their active record ID alone would not detect its later revocation. These checks use existing monotonic SQLite IDs and require no schema migration.

## Mutation, failure, and recovery

The in-process semaphore serializes exclusion, inclusion, DB mutation, full runtime materialization, and public republish. GET does not hold it; confirmation takes it only for a consistent authorized read. Approved exclusions record the normalized resource, reduced identity, and the commit/observation actually checked. Duplicate active resources stay one record but still reconcile policy and publication.

Before a restrictive exclusion changes SQLite, a private pending fence is created beside runtime policy at `<DN42ATLAS_RUNTIME_EXCLUSIONS_PATH>.reconciliation-pending`, flushed to disk, and closed. Both stable listing files are then withdrawn before the DB commit; inability to establish the fence or withdraw either file prevents the commit. Support pages and private publication state remain intact. The marker path is reserved, outside the web root, and distinct from configured policy/state/database files; never use it for raw scans or other data. No new environment setting or database migration is required.

The existing runtime file is then atomically moved to a private uniquely named `.backup` file. The configured runtime path is missing, so newly started crawler/report commands fail closed. DB write failures restore the previous runtime file only after a DB read confirms the active record set did not change, then regenerate publication from that authoritative state. If the write outcome cannot be confirmed, the runtime remains unavailable and the listing remains withdrawn where possible. Do not restore an old backup after an active exclusion was recorded.

After an insert, the complete runtime bundle is rebuilt from active DB records, validated, and combined with the required manual files. The current publication-state raw scan is then republished under that policy. Client cancellation does not interrupt this recovery sequence after it has entered the critical mutation step. No crawler, DNS lookup, HTTP probe, or registry update is invoked.

Only successful full reconciliation removes the pending fence. On web startup, an unfinished fence triggers listing withdrawal and recovery from SQLite before normal runtime-policy validation. Failed recovery refuses startup and retains the fence; a committed exclusion is never rolled back to restore publication. Process termination before/during public replacement leaves absent or newly filtered listing files, rather than an old listing. This protects process-interruption recovery, not a transaction across SQLite and filesystems or a guarantee against power-loss/filesystem failure. Already-open responses and client/proxy caches cannot be recalled by deleting files.

Future raw scans record `ProbeAddresses` on every probe result: the normalized, deduplicated, deterministically ordered approved/pinned scan-time address set. Prefix re-filtering uses only this immutable recorded evidence. If any recorded destination matches a prefix exclusion, the whole result is removed from both public JSON and the HTML's shared embedded model, with matching references elsewhere filtered/redacted. After all row decisions succeed, `ProbeAddresses` is removed from every retained public result. This provenance stays private in raw scans and is not emitted in `latest.json` or HTML reports; public artifacts cannot replace raw scan evidence for later prefix re-filtering. Scan timestamps and raw bytes remain unchanged; publication never resolves current DNS to guess old destinations.

Legacy raw scans without provenance remain compatible with normal publication and hostname-only exclusions. Under any active manual or runtime prefix exclusion, a retained result without a non-empty, valid destination array makes republish fail closed. The listing files are withdrawn where possible, support pages are preserved, and the new DB/runtime exclusion remains active for future crawling. This is an operational failure, not successful historical prefix removal. Obtain a new provenance-aware scan through the normal crawler workflow before publishing under prefix policy; reconciliation does not modify or retrofit legacy scans.

If runtime reconciliation fails, the exclusion remains recorded, runtime policy remains unavailable, and the public listing is withdrawn. If republish fails, the exclusion and valid new runtime policy remain active; `published/index.html` and `published/latest.json` are removed while support pages remain. The response clearly reports partial failure without exposing paths or exception details. A withdrawal permission failure produces an operational error requiring immediate operator intervention; detailed diagnostics stay in server logs.

Inclusion uses a dedicated sequence. After current ownership, active-record identity, and independent manual policy checks, it moves runtime policy aside, revokes the exact record with a known timestamp, and materializes active records to a private `.candidate` path. It validates that bundle and effective policy, republishes the state-recorded scan, and only then atomically installs the candidate at the configured runtime path. The old backup is removed on success. Runtime remains unavailable throughout preparation/publication, so newly started crawlers fail closed until the operation completes.

Any detected failure after revocation attempts to reactivate the same row using a conditional update that matches this operation's exact revocation timestamp. Recovery verifies the original audit row, rebuilds effective runtime policy, and republishes with the exclusion restored. It never reports inclusion success. If recovery cannot be proven, configured runtime policy is withdrawn and both generated public listing files are removed where possible; support pages remain. File permission failures requiring intervention are reported as operational failures. Client disconnects do not cancel the critical sequence or recovery. Process termination is not an atomic transaction: inspect the recorded DB state and use reconciliation before restarting crawling; reconciliation reflects that state, including any recorded revocation.

Repair already-recorded state with:

```text
dotnet run --project DN42Atlas.OptOut -- exclusions-reconcile
```

This command requires the DB/runtime/manual/publication paths above but no OIDC credentials or registry authorization. It does not add records or bypass ownership checks: it establishes the same durable fence, withdraws the listing, materializes existing active records, validates effective policy, and republishes the recorded raw scan. It clears the fence only on success and can run even when web startup fails because the runtime file is missing. Failure returns nonzero and withdraws the listing where possible. The older `exclusions-materialize` command rebuilds runtime rules only and never clears a pending fence; use `exclusions-reconcile` for recovery involving public artifacts. Old private `.backup` files left by failed mutations may be removed after reconciliation succeeds; never reinstall them over the active runtime policy.

When upgrading an existing installation, stop the mutation service and coordinate all publishers, deploy the new version, and run `exclusions-reconcile` once before restarting. Older interrupted mutations have no pending fence, so the upgrade must first reconcile existing DB/publication state explicitly. The service account must be able to create/remove the fence beside runtime policy; it is created with `0600` permissions on Unix. Do not delete a pending fence to bypass failed recovery. No database migration or new environment variable is needed.

Run a single mutation web process. The semaphore is process-local: run maintenance while that process is stopped, and coordinate crawler publication and registry updates separately rather than concurrently replacing these files. A crawler that loaded policy before a mutation is not forcibly interrupted by this service. Individual atomic file replacements are not a transaction across SQLite, runtime policy, and public/state files; withdrawal and reconciliation handle partial failure.

The deployment account needs write permission for SQLite, runtime files, publication state, and both generated public files. On Unix, databases created by `exclusions-init`, new runtime files, and private publication-state files are created with owner read/write permissions (`0600`); staged files retain those permissions after rename. Existing database permissions are not changed. Ensure the crawler account can read the private policy/state, using the same account or deliberately managed permissions. Windows file behavior is unchanged.

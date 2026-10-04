# DN42Atlas operator self-service

`DN42Atlas.OptOut` authenticates operators with Auth42 and permits exclusion of exact registered `.dn42` domains and `inetnum`/`inet6num` allocations whose `mnt-by` matches the authenticated active maintainer. OIDC establishes identity; only the current local canonical registry authorizes a resource. The application owns the private SQLite audit store, materializes runtime policy, and republishes the recorded scan without crawling. Self-service revocation, wildcard exclusions, delegated domains, and ASN-wide exclusions are not supported.

Configure the application with:

- `DN42ATLAS_OIDC_CLIENT_ID`
- `DN42ATLAS_OIDC_CLIENT_SECRET`
- `DN42ATLAS_OIDC_AUTHORITY` (currently `https://auth.iedon.net`)
- `DN42ATLAS_REGISTRY_PATH` (the root of a local DN42 registry checkout)
- `DN42ATLAS_REGISTRY_MAX_AGE_HOURS` (optional; defaults to `72`)
- `DN42ATLAS_EXCLUSION_DB_PATH` (the private SQLite audit database)
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

All other paths on `dn42atlas.dn42`, including `/`, should continue to be served by the static site. Publish the proxied routes through HTTPS so OIDC redirects and secure cookies work correctly.

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

There is no maintenance command that adds an exclusion. New records can only be created through authenticated, authorized `POST /operator`.

## Operator requests

Authenticated `GET /operator` lists only resources maintained by the reduced cookie identity's active maintainer. Active DB/runtime exclusions show **Excluded**; fresh, unexcluded resources have an **Exclude from DN42Atlas** form. Unsafe registry snapshots or unavailable ownership/storage data disable mutation controls. The page displays freshness but not subjects, DB IDs, private registry evidence, or other operators' audit records. Responses use `Cache-Control: no-store` and all rendered values are HTML-encoded.

The same `/operator` route accepts POST. Each form contains only `resourceType`, `resourceValue`, and an ASP.NET Core antiforgery token. Its cookie is Secure, HttpOnly, SameSite=Strict, and scoped to `/`. POST explicitly validates the cookie/request-token pair and reduced authenticated identity before acquiring the mutation lock. Missing or invalid tokens fail without changes. Ordinary success redirects to `/operator?result=excluded`; resources and audit identities never enter the query string. No mutation is performed by GET, and no new proxy route is needed.

Every POST parses and normalizes the resource on the server. The application rejects wildcards, non-DN42 domains, wrong address families, malformed prefixes, unreasonable lengths, and unknown types. Under a singleton semaphore, it obtains a new Fresh snapshot, reads current registry ownership, and checks the exact domain or canonical allocation against `mnt-by`. A second snapshot check rejects a checkout change during that read. Previously rendered resources, ASN values, Auth42 routes, DNS, and BGP do not authorize a request. Broader and narrower prefixes are rejected unless independently registered and maintained as exact resources.

## Mutation, failure, and recovery

The in-process semaphore serializes DB mutation, full runtime materialization, and public republish. GET does not hold it. Approved requests record the normalized resource, reduced identity, and the commit/observation actually checked. Duplicate active resources stay one record but still reconcile policy and publication.

Before changing SQLite, the existing runtime file is atomically moved to a private uniquely named `.backup` file. The configured runtime path is then missing, so newly started crawler/report commands fail closed. DB write failures restore the previous runtime file only after a DB read confirms the active record set did not change. If the write outcome cannot be confirmed, the runtime remains unavailable and the listing is withdrawn where possible. Do not restore an old backup after an active exclusion was recorded.

After an insert, the complete runtime bundle is rebuilt from active DB records, validated, and combined with the required manual files. The current publication-state raw scan is then republished under that policy. Client cancellation does not interrupt this recovery sequence after it has entered the critical mutation step. No crawler, DNS lookup, HTTP probe, or registry update is invoked.

If runtime reconciliation fails, the exclusion remains recorded, runtime policy remains unavailable, and the public listing is withdrawn. If republish fails, the exclusion and valid new runtime policy remain active; `published/index.html` and `published/latest.json` are removed while support pages remain. The response clearly reports partial failure without exposing paths or exception details. A withdrawal permission failure produces an operational error requiring immediate operator intervention; detailed diagnostics stay in server logs.

Repair already-recorded state with:

```text
dotnet run --project DN42Atlas.OptOut -- exclusions-reconcile
```

This command requires the DB/runtime/manual/publication paths above but no OIDC credentials or registry authorization. It does not add records or bypass ownership checks: it materializes existing active records, validates effective policy, and republishes the recorded raw scan. It can run even when web startup fails because the runtime file is missing. Failure returns nonzero and withdraws the listing where possible. The older `exclusions-materialize` command rebuilds runtime rules only; use `exclusions-reconcile` for recovery involving public artifacts. Old private `.backup` files left by failed mutations may be removed after reconciliation succeeds; never reinstall them over the active runtime policy.

Run a single mutation web process. The semaphore is process-local: run maintenance while that process is stopped, and coordinate crawler publication and registry updates separately rather than concurrently replacing these files. A crawler that loaded policy before a mutation is not forcibly interrupted by this service. Individual atomic file replacements are not a transaction across SQLite, runtime policy, and public/state files; withdrawal and reconciliation handle partial failure.

The deployment account needs write permission for SQLite, runtime files, publication state, and both generated public files. On Unix, databases created by `exclusions-init`, new runtime files, and private publication-state files are created with owner read/write permissions (`0600`); staged files retain those permissions after rename. Existing database permissions are not changed. Ensure the crawler account can read the private policy/state, using the same account or deliberately managed permissions. Windows file behavior is unchanged.

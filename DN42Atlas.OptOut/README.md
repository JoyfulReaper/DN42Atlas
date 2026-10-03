# DN42Atlas operator self-service

`DN42Atlas.OptOut` is the read-only operator self-service application. It authenticates with Auth42 and lists exact registered `.dn42` domain objects and `inetnum`/`inet6num` allocations whose `mnt-by` value matches the authenticated maintainer. It does not create exclusions or change Atlas data.

Configure the application with:

- `DN42ATLAS_OIDC_CLIENT_ID`
- `DN42ATLAS_OIDC_CLIENT_SECRET`
- `DN42ATLAS_OIDC_AUTHORITY` (currently `https://auth.iedon.net`)
- `DN42ATLAS_REGISTRY_PATH` (the root of a local DN42 registry checkout)
- `DN42ATLAS_REGISTRY_MAX_AGE_HOURS` (optional; defaults to `72`)

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

Snapshots older than `DN42ATLAS_REGISTRY_MAX_AGE_HOURS` are stale. A modified or untracked working-tree file makes the snapshot dirty and unsafe. Missing or unreadable Git state, observation metadata, or commit identity produces an unknown and unsafe freshness state. Future automatic opt-out approval will fail closed for stale, dirty, or unknown snapshots; previously established exclusions will remain effective. This application does not schedule or trigger registry updates from web requests.

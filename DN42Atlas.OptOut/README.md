# DN42Atlas operator self-service

`DN42Atlas.OptOut` is the read-only first slice of the operator self-service application. It authenticates with Auth42 and lists exact registered `.dn42` domain objects whose `mnt-by` value matches the authenticated maintainer. It does not create exclusions or change Atlas data.

Configure the application with:

- `DN42ATLAS_OIDC_CLIENT_ID`
- `DN42ATLAS_OIDC_CLIENT_SECRET`
- `DN42ATLAS_OIDC_AUTHORITY` (currently `https://auth.iedon.net`)
- `DN42ATLAS_REGISTRY_PATH` (the root of a local DN42 registry checkout)

The configured registry checkout must contain its domain objects under `data/dns`. The registered OIDC callback is `/signin-oidc`; production deployments should publish it through HTTPS. The client secret must be supplied through deployment configuration and must not be committed.

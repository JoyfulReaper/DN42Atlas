# Daily crawler templates

These templates run one crawl/publication daily from the existing source checkout.
They do not install themselves, deploy OptOut, configure nginx, or switch the preview
root. Keep nginx serving `/opt/DN42Atlas/DN42Atlas/site` until no earlier than
October 16, 2026. `published/` can be refreshed while hidden; `results/` is private
historical data and must never be served.

The timer runs at 01:30 in the host's local timezone, with up to 15 minutes random
delay. `Persistent=true` catches a missed scheduled run. `After` orders against
`dn42atlas-registry-update.service` if both are queued; it neither starts that
service nor proves registry freshness. Keep the existing updater at about 00:16
and inspect its success. The crawler reads `/var/lib/dn42atlas/registry/data/dns`.

The oneshot runs as `joyfulreaper:joyfulreaper`, uses `/opt/DN42Atlas` as its working
directory, sets HOME, waits for network-online, uses NoNewPrivileges/PrivateTmp,
and allows 45 minutes including source build and operation-lock waiting. It uses
`dotnet run -c Release --no-launch-profile -- run`; there is no second crawler
release system. Ensure .NET 10 SDK, pinned submodules and package restore/build
work under that account. Coordinate source updates with the crawler; do not update
the checkout during a run. Timeout termination releases the OS operation lock.

Create a separate `/home/joyfulreaper/.config/dn42atlas/crawler.env`:

```ini
DN42ATLAS_REGISTRY_PATH=/var/lib/dn42atlas/registry
DN42ATLAS_RUNTIME_EXCLUSIONS_PATH=/var/lib/dn42atlas/runtime-exclusions.json
DN42ATLAS_PUBLICATION_STATE_PATH=/var/lib/dn42atlas/publication-state.json
```

No OIDC secret, ntfy token/topic, or web configuration belongs in it. The derived
lock is `/var/lib/dn42atlas/publication-state.json.operation-lock`. OptOut's existing
environment must use that exact same state path, runtime path, manual policy files
(`/opt/DN42Atlas/config/excluded-hosts.txt` and `excluded-prefixes.txt`) and public
directory (`/opt/DN42Atlas/published`). Required exclusion files must exist, even
when empty. Protect private storage; use the same account for both executables.
The template uses umask 0022 for readable generated public files; private lock,
runtime and publication-state creation explicitly uses 0600. Protect raw history
with its directory permissions. Existing nginx/read permissions should be checked
before the eventual public-root switch.

## Installation

Run from the updated repository. Adjust example paths/account if your deployment
differs. Deploy OptOut first: the old binary does not participate in this lock.
For a coordinated upgrade, stop mutation and publishers, update source/deploy the
new OptOut release, and run `exclusions-reconcile` under the normal OptOut environment
before restarting. Do not source the secret environment into the scheduled crawler.
No schema migration or additional application setting is needed. If the previous
durable-fence version was already reconciled and healthy, another reconciliation
is not required solely for the operation lock.

```bash
sudo install -d -o joyfulreaper -g joyfulreaper -m 0700 /home/joyfulreaper/.config/dn42atlas
sudo install -d -o joyfulreaper -g joyfulreaper -m 0700 /var/lib/dn42atlas /opt/DN42Atlas/results
sudo install -d -o joyfulreaper -g joyfulreaper -m 0755 /opt/DN42Atlas/published
sudo -u joyfulreaper nano /home/joyfulreaper/.config/dn42atlas/crawler.env
sudo chmod 0600 /home/joyfulreaper/.config/dn42atlas/crawler.env
sudo install -m 0644 deployment/systemd/dn42atlas-run.service /etc/systemd/system/dn42atlas-run.service
sudo install -m 0644 deployment/systemd/dn42atlas-run.timer /etc/systemd/system/dn42atlas-run.timer
sudo systemd-analyze verify /etc/systemd/system/dn42atlas-run.service /etc/systemd/system/dn42atlas-run.timer
sudo systemctl daemon-reload
sudo systemctl enable --now dn42atlas-run.timer
```

Do not modify the existing registry checkout or private databases to install these
templates. Confirm the service account can read policies and write checkout build
output, history, publication/state and lock directories. Never delete/rename the
operation-lock file while processes may hold it. A crawl owning the lock temporarily
causes self-service final mutations to return HTTP 503 with retry guidance; neither
policy nor exclusion DB changes before ownership. Startup/maintenance can wait for
a crawl, so deployment-local OptOut startup timeouts should accommodate that wait.

Inspect and optionally trigger a run:

```bash
systemctl list-timers dn42atlas-run.timer
systemctl status dn42atlas-run.service
journalctl -u dn42atlas-run.service
sudo systemctl start dn42atlas-run.service
```

The service writes `/opt/DN42Atlas/domain-resolution.json`, timestamped raw JSON/HTML
under `/opt/DN42Atlas/results`, filtered `published/latest.json`/`index.html`, and
configured private publication state. It never changes nginx or the launch gate.

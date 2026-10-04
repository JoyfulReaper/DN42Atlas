#!/usr/bin/env bash
set -euo pipefail

die()
{
    echo "Error: $*" >&2
    exit 1
}

valid_retention()
{
    [[ "$1" =~ ^[0-9]+$ ]]
}

release_name()
{
    [[ "$1" =~ ^[0-9]{8}T[0-9]{6}Z-[[:xdigit:]]{12,40}$ ]]
}

# Only direct, physical child directories with completed-release names are candidates.
# Keep current explicitly, even if it is not the newest release.
prune_releases()
{
    local releases="$1" current="$2" retain="$3"
    local active candidate name sorted kept=0
    local -a candidates=()
    valid_retention "$retain" || { echo "Invalid release retention." >&2; return 1; }
    [[ -d "$releases" && ! -L "$releases" ]] || { echo "Unsafe releases directory." >&2; return 1; }
    releases="$(cd -- "$releases" && pwd -P)" || return 1
    active="$(readlink -f -- "$current")" || return 1
    name="${active##*/}"
    if ! release_name "$name" || [[ "$active" != "$releases/$name" || ! -d "$active" || -L "$active" ]]; then
        echo "Current must target a completed direct child of releases; refusing to prune." >&2
        return 1
    fi
    for candidate in "$releases"/*; do
        [[ -d "$candidate" && ! -L "$candidate" ]] || continue
        release_name "${candidate##*/}" || continue
        [[ "$candidate" == "$active" ]] || candidates+=("$candidate")
    done
    echo "Pruning old releases..."
    echo "Keeping current release: $active"
    # Ignore leading zeroes and avoid arithmetic overflow for enormous valid settings.
    retain="${retain#"${retain%%[!0]*}"}"
    retain="${retain:-0}"
    if (( ${#candidates[@]} > 0 )); then
        sorted="$(printf '%s\n' "${candidates[@]}" | LC_ALL=C sort -r)" || {
            echo "Failed to sort release candidates; no releases pruned." >&2
            return 1
        }
        while IFS= read -r candidate; do
            name="${candidate##*/}"
            if ! release_name "$name" || [[ "$candidate" != "$releases/$name" ]]; then
                echo "Invalid release candidate; refusing to prune." >&2
                return 1
            fi
            if (( ${#retain} > 9 )) || (( kept < 10#$retain )); then
                echo "Keeping previous release: $candidate"
                kept=$((kept + 1))
            else
                # Recheck immediately before removal. rm does not follow nested symlinks.
                if [[ ! -d "$candidate" || -L "$candidate" || "$(readlink -f -- "$candidate")" != "$candidate" ||
                      "$(readlink -f -- "$current")" != "$active" || "$candidate" == "$active" ]]; then
                    echo "Release path changed; refusing to remove $candidate." >&2
                    return 1
                fi
                echo "Removing old release: $candidate"
                rm -rf -- "$candidate" || { echo "Failed to prune $candidate." >&2; return 1; }
            fi
        done <<< "$sorted"
    fi
}

cleanup_deploy()
{
    local status=$?
    trap - EXIT
    if [[ "${temp_owned:-false}" == true && -d "$TEMP" ]]; then
        if [[ ! -L "$TEMP" && "$(readlink -f -- "$TEMP")" == "$RELEASES_DIR/${TEMP##*/}" ]]; then
            rm -rf -- "$TEMP" || { echo "Failed to clean deployment temporary directory." >&2; status=1; }
        else
            echo "Unsafe deployment temporary path; refusing cleanup." >&2
            status=1
        fi
    fi
    if [[ "${link_owned:-false}" == true && -L "$NEXT_LINK" ]]; then
        rm -f -- "$NEXT_LINK" || { echo "Failed to clean deployment temporary link." >&2; status=1; }
    fi
    exit "$status"
}

main()
{
    local repo_dir project deploy_root service retain sha stamp release
    local temp_owned=false link_owned=false
    # These paths remain available to the EXIT trap in the main function's scope.
    local RELEASES_DIR CURRENT_LINK TEMP NEXT_LINK
    repo_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
    project="$repo_dir/DN42Atlas.OptOut/DN42Atlas.OptOut.csproj"
    deploy_root="${DN42ATLAS_OPTOUT_DEPLOY_ROOT:-/opt/dn42atlas-optout}"
    service="${DN42ATLAS_OPTOUT_SERVICE:-dn42atlas-optout.service}"
    retain="${DN42ATLAS_OPTOUT_RETAIN_PREVIOUS-3}"
    valid_retention "$retain" || die "DN42ATLAS_OPTOUT_RETAIN_PREVIOUS must be a non-negative integer."
    [[ $EUID -ne 0 ]] || die "Do not run this script as root."
    cd -- "$repo_dir"
    [[ -z "$(git status --porcelain)" ]] || die "Repository has uncommitted changes. Refusing to deploy."
    echo "Updating DN42Atlas..."
    git pull --ff-only
    echo "Updating pinned submodules..."
    git submodule sync --recursive
    git submodule update --init --recursive
    if git submodule status --recursive | grep -qE '^[+-U]'; then
        die "A submodule is not at the commit pinned by DN42Atlas."
    fi
    sha="$(git rev-parse --short=12 HEAD)"
    stamp="$(date -u '+%Y%m%dT%H%M%SZ')"
    release_name "$stamp-$sha" || die "Invalid release name."
    mkdir -p -- "$deploy_root"
    deploy_root="$(cd -- "$deploy_root" && pwd -P)"
    RELEASES_DIR="$deploy_root/releases"
    [[ ! -L "$RELEASES_DIR" ]] || die "Releases directory must not be a symlink."
    mkdir -p -- "$RELEASES_DIR"
    CURRENT_LINK="$deploy_root/current"
    release="$RELEASES_DIR/$stamp-$sha"
    TEMP="$RELEASES_DIR/.$stamp-$sha.tmp"
    NEXT_LINK="$deploy_root/.current.$stamp-$sha.$$.new"
    [[ ! -e "$release" && ! -L "$release" ]] || die "Release already exists."
    mkdir -- "$TEMP"
    temp_owned=true
    trap cleanup_deploy EXIT
    echo "Publishing OptOut..."
    dotnet publish "$project" -c Release -o "$TEMP"
    mv -- "$TEMP" "$release"
    temp_owned=false
    echo "Activating release: $release"
    ln -s -- "$release" "$NEXT_LINK"
    link_owned=true
    mv -Tf -- "$NEXT_LINK" "$CURRENT_LINK"
    link_owned=false
    echo "Current release: $(readlink -f -- "$CURRENT_LINK")"
    if systemctl cat "$service" >/dev/null 2>&1; then
        echo "Restarting $service..."
        sudo systemctl restart "$service" || die "$service restart failed; no releases were pruned."
        if ! sudo systemctl is-active --quiet "$service"; then
            sudo systemctl status "$service" --no-pager || true
            die "$service failed to start."
        fi
        echo "$service is running."
    else
        echo "$service is not installed yet; systemd restart skipped."
    fi
    prune_releases "$RELEASES_DIR" "$CURRENT_LINK" "$retain" || die "Release pruning failed; deployment is active but retention needs attention."
    echo "Deployed DN42Atlas OptOut $sha"
    trap - EXIT
}

# Sourcing exposes the small retention functions for shell-level tests without deploying.
if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    main "$@"
fi

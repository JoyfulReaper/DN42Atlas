#!/usr/bin/env bash
set -euo pipefail

REPO_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$REPO_DIR/DN42Atlas.OptOut/DN42Atlas.OptOut.csproj"

DEPLOY_ROOT="${DN42ATLAS_OPTOUT_DEPLOY_ROOT:-/opt/dn42atlas-optout}"
RELEASES_DIR="$DEPLOY_ROOT/releases"
CURRENT_LINK="$DEPLOY_ROOT/current"

SERVICE="${DN42ATLAS_OPTOUT_SERVICE:-dn42atlas-optout.service}"

die()
{
    echo "Error: $*" >&2
    exit 1
}

if [[ $EUID -eq 0 ]]; then
    die "Do not run this script as root."
fi

cd "$REPO_DIR"

if [[ -n "$(git status --porcelain)" ]]; then
    die "Repository has uncommitted changes. Refusing to deploy."
fi

echo "Updating DN42Atlas..."
git pull --ff-only

echo "Updating pinned submodules..."
git submodule sync --recursive
git submodule update --init --recursive

if git submodule status --recursive | grep -qE '^[+-U]'; then
    die "A submodule is not at the commit pinned by DN42Atlas."
fi

SHA="$(git rev-parse --short=12 HEAD)"
STAMP="$(date -u '+%Y%m%dT%H%M%SZ')"

RELEASE="$RELEASES_DIR/${STAMP}-${SHA}"
TEMP="$RELEASES_DIR/.${STAMP}-${SHA}.tmp"

mkdir -p "$RELEASES_DIR"
rm -rf "$TEMP"

echo "Publishing OptOut..."
dotnet publish \
    "$PROJECT" \
    -c Release \
    -o "$TEMP"

mv "$TEMP" "$RELEASE"

echo "Activating release:"
echo "  $RELEASE"

NEXT_LINK="$DEPLOY_ROOT/.current.new"

rm -f "$NEXT_LINK"
ln -s "$RELEASE" "$NEXT_LINK"
mv -Tf "$NEXT_LINK" "$CURRENT_LINK"

echo
echo "Current release:"
readlink -f "$CURRENT_LINK"

if systemctl cat "$SERVICE" >/dev/null 2>&1; then
    echo
    echo "Restarting $SERVICE..."
    sudo systemctl restart "$SERVICE"

    if ! sudo systemctl is-active --quiet "$SERVICE"; then
        sudo systemctl status "$SERVICE" --no-pager
        die "$SERVICE failed to start."
    fi

    echo "$SERVICE is running."
else
    echo
    echo "$SERVICE is not installed yet."
    echo "Release published successfully; systemd restart skipped."
fi

echo
echo "Deployed DN42Atlas OptOut $SHA"

#!/bin/sh
# Installs the BYOA agent wallet for one user from the package folder this script is in.
#
#   ./install.sh [--start-at-login]
#
# No root, no system folders: the programs go to  ~/.local/share/AgentWallet/versions/<version>  ($XDG_DATA_HOME is honoured),
# and the link  ~/.local/share/AgentWallet/current  points at them in one rename. The wallet's own registration then writes the
# browser host manifests, the extension copy, the desktop entry for bringyourownagent:// links and the icons, and tells the desktop
# about them. Running it again with a newer package is an update: the old version stays in versions/.
#
# The script deletes nothing. The wallet's data folder (settings, keys, activity) is never touched.

set -eu

VERSION="{VERSION}"                 # replaced when the package is built

die() { echo "install.sh: $*" >&2; exit 1; }
say() { echo "install.sh: $*"; }

usage() {
  echo "usage: ./install.sh [--start-at-login]"
  echo "  --start-at-login  also start the wallet when you log in (the same tick box is in its settings)"
}

START_AT_LOGIN=0
for argument in "$@"; do
  case "$argument" in
    --start-at-login) START_AT_LOGIN=1 ;;
    -h|--help) usage; exit 0 ;;
    *) usage >&2; die "unknown argument: $argument" ;;
  esac
done

case "$VERSION" in
  *"{"*) die "this install.sh was not prepared by make-linux-package.sh; run the one from the package" ;;
esac
case "$VERSION" in *[!0-9A-Za-z.-]*) die "the version in this package is not usable: $VERSION" ;; esac

[ "$(id -u)" -ne 0 ] || die "run this as yourself, not as root: the wallet is installed for one user only"
[ -n "${HOME:-}" ] || die "HOME is not set"
[ -d "$HOME" ] || die "HOME does not point at a folder: $HOME"

HERE=$(cd "$(dirname "$0")" && pwd)
DATA="${XDG_DATA_HOME:-$HOME/.local/share}"
ROOT="$DATA/AgentWallet"
DEST="$ROOT/versions/$VERSION"

[ -f "$HERE/app/Byoa.Wallet" ] || die "this folder is not a package: app/Byoa.Wallet is missing"
[ -f "$HERE/app/byoa-host" ] || die "this folder is not a package: app/byoa-host is missing"
[ -f "$HERE/app/byoa-mcp" ] || die "this folder is not a package: app/byoa-mcp is missing"
[ -f "$HERE/extension/manifest.json" ] || die "this folder is not a package: extension/manifest.json is missing"

if [ -d "$DEST" ]; then
  say "version $VERSION is already in $DEST; its files are written again (the settings are not touched)"
fi

say "installing version $VERSION in $DEST"
mkdir -p "$ROOT/versions" "$DEST"
cp -R "$HERE/app/." "$DEST/"
mkdir -p "$DEST/extension"
cp -R "$HERE/extension/." "$DEST/extension/"
chmod 755 "$DEST/Byoa.Wallet" "$DEST/byoa-host" "$DEST/byoa-mcp"

# current -> the new version in one rename: a reader sees the old version or the new one, never a missing link.
PENDING="$ROOT/current.new"
if [ -e "$PENDING" ] || [ -L "$PENDING" ]; then PENDING="$ROOT/current.new.$$"; fi
ln -s "$DEST" "$PENDING"
mv -T "$PENDING" "$ROOT/current"
say "current points at $DEST"

if [ "$START_AT_LOGIN" -eq 1 ]; then
  "$ROOT/current/Byoa.Wallet" --register --start-at-login \
    || die "the wallet is installed in $DEST, but its desktop registration failed; run: $ROOT/current/Byoa.Wallet --register"
else
  "$ROOT/current/Byoa.Wallet" --register \
    || die "the wallet is installed in $DEST, but its desktop registration failed; run: $ROOT/current/Byoa.Wallet --register"
fi

say "the extension for \"Load unpacked\" in chrome://extensions: $DATA/BYOA/extension"
say "start the wallet from the applications menu (Agent wallet) or with: $ROOT/current/Byoa.Wallet"
say "installed version $VERSION; the older versions in $ROOT/versions stay"
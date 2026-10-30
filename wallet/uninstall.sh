#!/bin/sh
# Removes what the BYOA agent wallet registered for this user and deletes no files.
#
#   ./uninstall.sh [--dry-run]
#
# It removes only registrations of this installation: the browser host manifests whose path points into
# ~/.local/share/AgentWallet, the desktop entry for bringyourownagent:// links, the wallet's lines in the MIME defaults, and the
# autostart entry. The programs, every installed version, the extension copy, the icons and all your data (settings, keys,
# activity) stay where they are; the script prints the folders so that you can remove them yourself if you want them gone.
#
# --dry-run prints what it would remove and changes nothing. The same can be done from the settings window by turning
# "Start at login" off; the browser manifests are written again by the wallet at its next start.

set -eu

DRY=0
for argument in "$@"; do
  case "$argument" in
    --dry-run) DRY=1 ;;
    -h|--help) echo "usage: ./uninstall.sh [--dry-run]"; exit 0 ;;
    *) echo "uninstall.sh: unknown argument: $argument" >&2; exit 2 ;;
  esac
done

die() { echo "uninstall.sh: $*" >&2; exit 1; }
say() { echo "uninstall.sh: $*"; }

[ -n "${HOME:-}" ] || die "HOME is not set"
DATA="${XDG_DATA_HOME:-$HOME/.local/share}"
CONFIG="${XDG_CONFIG_HOME:-$HOME/.config}"
ROOT="$DATA/AgentWallet"
DESKTOP_ID="org.bringyourownagent.wallet.desktop"
HOST_MANIFEST="org.bringyourownagent.wallet.json"

remove_file() {
  file="$1"
  if [ "$DRY" -eq 1 ]; then
    say "would remove $file"
  else
    rm -- "$file"
    say "removed $file"
  fi
}

# A native host manifest is the wallet's when its "path" points into this installation.
remove_manifest() {
  file="$1"
  [ -f "$file" ] || return 0
  path=$(sed -n 's/^[[:space:]]*"path"[[:space:]]*:[[:space:]]*"\(.*\)".*$/\1/p' "$file" | head -1)
  case "$path" in
    "$ROOT"/*) remove_file "$file" ;;
  esac
}

for browser in google-chrome google-chrome-beta google-chrome-unstable chromium microsoft-edge BraveSoftware/Brave-Browser vivaldi opera; do
  remove_manifest "$CONFIG/$browser/NativeMessagingHosts/$HOST_MANIFEST"
done
remove_manifest "$HOME/snap/chromium/common/chromium/NativeMessagingHosts/$HOST_MANIFEST"
remove_manifest "$HOME/.var/app/com.google.Chrome/config/google-chrome/NativeMessagingHosts/$HOST_MANIFEST"

entry="$DATA/applications/$DESKTOP_ID"
if [ -f "$entry" ] && grep -Fq "Exec=\"$ROOT/" "$entry"; then
  remove_file "$entry"
fi

# The MIME default was written by "xdg-mime default"; no xdg-mime call unsets it, so the wallet's lines go from the files it edits.
cleared=0
for file in "$CONFIG/mimeapps.list" "$DATA/applications/mimeapps.list"; do
  [ -f "$file" ] || continue
  grep -Fq "$DESKTOP_ID" "$file" || continue
  cleared=1
  if [ "$DRY" -eq 1 ]; then
    say "would remove the wallet's lines from $file"
  else
    sed -i "/$DESKTOP_ID/d" "$file"
    say "removed the wallet's lines from $file"
  fi
done
[ "$cleared" -eq 1 ] || say "no MIME default of the wallet was found"

autostart="$CONFIG/autostart/$DESKTOP_ID"
if [ -f "$autostart" ] && grep -Fq "$ROOT/" "$autostart"; then
  remove_file "$autostart"
fi

if [ "$DRY" -eq 0 ]; then
  if command -v update-desktop-database > /dev/null 2>&1; then
    update-desktop-database "$DATA/applications" > /dev/null 2>&1 || true
  fi
  if command -v xdg-mime > /dev/null 2>&1; then
    say "the default for bringyourownagent:// is now: $(xdg-mime query default x-scheme-handler/bringyourownagent 2> /dev/null || true)"
  fi
fi

say "nothing was deleted except the registrations above; these stay for you to remove yourself:"
say "  the program and every version:    $ROOT"
say "  your settings, keys and activity: $DATA/BYOA/wallet"
say "  the copy of the extension:        $DATA/BYOA/extension"
say "  the icons:                        $DATA/icons/hicolor"
say "The key in the Secret Service (or secrets.key in the data folder) is left where it is."
say "The extension itself is removed in chrome://extensions."
[ "$DRY" -eq 1 ] || say "done"
[ "$DRY" -eq 0 ] || say "dry run: nothing was changed"
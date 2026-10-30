#!/bin/bash
# Builds the ready-to-use package of the BYOA agent wallet for Linux x64 desktops: byoa-wallet-<version>-linux-x64.tar.gz.
#
#   wallet/packaging/make-linux-package.sh --out DIR [--version 0.1.0]
#
# Run it on a Linux machine with the .NET 10 SDK (it needs dotnet, tar, gzip, sha256sum, file, python3, sed, find and sort).
#
# 1. Publishes the tray app (desktop/Byoa.Wallet.Desktop), the native messaging host (host/Byoa.Wallet.Host) and the MCP bridge
#    (mcp/Byoa.Wallet.Mcp), Release, self-contained for linux-x64 (the .NET runtime is included: the desktop needs no .NET), as a
#    folder of files (not a single file, not trimmed), into one folder under --out.
# 2. Stages byoa-wallet-<version>-linux-x64:
#      app/                     the programs and the runtime
#      extension/               the browser extension, for "Load unpacked"
#      install.sh, uninstall.sh per user, no root; they remove no files
#      README.txt, LICENSE, NOTICE, THIRD-PARTY-NOTICES.txt, licenses/
# 3. Archives it with sorted names, fixed owner/group and a fixed time, then writes the SHA-256 and latest.json beside it:
#      byoa-wallet-<version>-linux-x64.tar.gz
#      byoa-wallet-<version>-linux-x64.tar.gz.sha256
#      latest.json              {"version","file","sha256","size"} for the wallet's own updater
#
# The script deletes, moves and renames nothing: --out must be a folder that does not exist yet.

set -euo pipefail

RID="linux-x64"

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WALLET="$(cd "$HERE/.." && pwd)"
REPO="$(cd "$WALLET/.." && pwd)"

OUT=""
VERSION=""

die() { echo "make-linux-package: $*" >&2; exit 1; }
say() { echo "make-linux-package: $*"; }

while [ $# -gt 0 ]; do
  case "$1" in
    --out) OUT="${2:?--out needs a folder}"; shift 2 ;;
    --version) VERSION="${2:?--version needs a value}"; shift 2 ;;
    *) die "unknown argument: $1" ;;
  esac
done

[ "$(uname -s)" = "Linux" ] || die "run this script on Linux"
for tool in dotnet tar gzip sha256sum file python3 sed find sort awk cp mkdir chmod; do
  command -v "$tool" > /dev/null 2>&1 || die "needed tool not found: $tool"
done
[ -n "$OUT" ] || die "--out is required"
if [ -z "$VERSION" ]; then
  VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$WALLET/Directory.Build.props" | head -1)"
  [ -n "$VERSION" ] || die "no <Version> in wallet/Directory.Build.props; pass --version"
fi
case "$VERSION" in [0-9]*.[0-9]*.[0-9]*) ;; *) die "version '$VERSION' is not like 0.1.0" ;; esac
case "$VERSION" in *[!0-9A-Za-z.-]*) die "version '$VERSION' has characters a file name should not have" ;; esac

[ ! -e "$OUT" ] || die "$OUT exists: this script deletes nothing, so choose a folder that does not exist"
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"
NAME="byoa-wallet-$VERSION-linux-x64"
STAGE="$OUT/$NAME"
PUBLISH="$OUT/publish"
ARTIFACTS="$OUT/artifacts"

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 AVALONIA_TELEMETRY_OPTOUT=1

# ---- 1. publish ---------------------------------------------------------------------------------------------------------------------
mkdir -p "$PUBLISH/app"
for project in desktop/Byoa.Wallet.Desktop/Byoa.Wallet.Desktop.csproj host/Byoa.Wallet.Host/Byoa.Wallet.Host.csproj mcp/Byoa.Wallet.Mcp/Byoa.Wallet.Mcp.csproj; do
  say "publishing $project (Release, self-contained, $RID)"
  dotnet publish "$WALLET/$project" -c Release -r "$RID" --self-contained true \
    -p:PublishTrimmed=false -p:PublishSingleFile=false -p:DebugType=none -p:DebugSymbols=false \
    -p:Version="$VERSION" \
    --artifacts-path "$ARTIFACTS" -o "$PUBLISH/app" --nologo -v quiet
done
for file in Byoa.Wallet byoa-host byoa-mcp libcoreclr.so libSkiaSharp.so libHarfBuzzSharp.so; do
  [ -e "$PUBLISH/app/$file" ] || die "$file is missing in $PUBLISH/app"
done

# ---- 2. the package folder ----------------------------------------------------------------------------------------------------------
say "staging $STAGE"
mkdir -p "$STAGE/app" "$STAGE/extension"
for f in "$PUBLISH/app"/*; do
  case "$f" in
    *.pdb) ;;
    *) cp -Rp "$f" "$STAGE/app/" ;;
  esac
done
cp -Rp "$WALLET/extension/." "$STAGE/extension/"
python3 "$HERE/copy_package_licenses.py" --app "$PUBLISH/app" --artifacts "$ARTIFACTS" --out "$STAGE/licenses" \
  --fonts-license "$WALLET/desktop/Byoa.Wallet.Desktop/Assets/Fonts/OFL.txt"
sed "s/{VERSION}/$VERSION/g" "$HERE/README.linux.txt" > "$STAGE/README.txt"
sed "s/{VERSION}/$VERSION/g" "$HERE/THIRD-PARTY-NOTICES.linux.txt" > "$STAGE/THIRD-PARTY-NOTICES.txt"
sed "s/{VERSION}/$VERSION/g" "$WALLET/install.sh" > "$STAGE/install.sh"
cp "$WALLET/uninstall.sh" "$STAGE/uninstall.sh"
cp "$REPO/LICENSE" "$STAGE/LICENSE"
cp "$REPO/NOTICE" "$STAGE/NOTICE"

# The same modes on every build, so that the archive is reproducible: folders 755, files 644, programs and libraries executable.
find "$STAGE" -type d -exec chmod 755 {} +
find "$STAGE" -type f -exec chmod 644 {} +
chmod 755 "$STAGE/install.sh" "$STAGE/uninstall.sh" "$STAGE/app/Byoa.Wallet" "$STAGE/app/byoa-host" "$STAGE/app/byoa-mcp"
find "$STAGE/app" -type f \( -name '*.so' -o -name '*.so.*' -o -name createdump \) -exec chmod 755 {} +
chmod 755 "$STAGE/app/Byoa.Wallet" "$STAGE/app/byoa-host" "$STAGE/app/byoa-mcp"

# ---- checks of the staged package ----------------------------------------------------------------------------------------------------
PROBLEMS=0
bad() { echo "make-linux-package: CHECK FAILED: $*" >&2; PROBLEMS=$((PROBLEMS + 1)); }
for program in Byoa.Wallet byoa-host byoa-mcp; do
  [ -f "$STAGE/app/$program" ] || bad "$program is missing"
  [ -x "$STAGE/app/$program" ] || bad "$program is not executable"
done
[ -f "$STAGE/app/Byoa.Wallet.runtimeconfig.json" ] || bad "Byoa.Wallet.runtimeconfig.json is missing"
case "$(file -b "$STAGE/app/Byoa.Wallet" | sed -n 1p)" in
  *ELF*64-bit*x86-64*) ;;
  *) bad "Byoa.Wallet is not an x86-64 ELF program" ;;
esac
grep -q '"includedFrameworks"' "$STAGE/app/Byoa.Wallet.runtimeconfig.json" || bad "the app is not self-contained (no includedFrameworks in its runtimeconfig)"
[ -f "$STAGE/app/libcoreclr.so" ] || bad "libcoreclr.so is missing: the .NET runtime is not in the package"
[ -f "$STAGE/extension/manifest.json" ] || bad "the extension is missing"
[ -f "$STAGE/README.txt" ] || bad "README.txt is missing"
[ -f "$STAGE/THIRD-PARTY-NOTICES.txt" ] || bad "THIRD-PARTY-NOTICES.txt is missing"
[ -f "$STAGE/LICENSE" ] || bad "LICENSE is missing"
[ -f "$STAGE/NOTICE" ] || bad "NOTICE is missing"
[ -x "$STAGE/install.sh" ] || bad "install.sh is not executable"
[ -x "$STAGE/uninstall.sh" ] || bad "uninstall.sh is not executable"
[ "$(find "$STAGE/app" -name '*.pdb' | wc -l | tr -d ' ')" = "0" ] || bad "the package holds .pdb files"
[ "$(find "$STAGE/app" -name '*.dylib' | wc -l | tr -d ' ')" = "0" ] || bad "the package holds macOS libraries"
[ "$(find "$STAGE/licenses" -type f | wc -l | tr -d ' ')" -gt 0 ] || bad "the licenses folder is empty"
grep -q '^VERSION="' "$STAGE/install.sh" || bad "install.sh has no version"
grep -q "$VERSION" "$STAGE/install.sh" || bad "install.sh does not carry version $VERSION"
[ "$PROBLEMS" -eq 0 ] || die "$PROBLEMS check(s) failed; the package folder is left for inspection: $STAGE"
say "the staged package is fine"

# ---- 3. the archive ------------------------------------------------------------------------------------------------------------------
TARBALL="$OUT/$NAME.tar.gz"
[ ! -e "$TARBALL" ] || die "$TARBALL already exists"
say "archiving (sorted names, owner 0, fixed time)"
tar --sort=name --format=gnu --owner=0 --group=0 --numeric-owner --mtime="@${SOURCE_DATE_EPOCH:-0}" \
  -C "$OUT" -cf - "$NAME" | gzip -n -9 > "$TARBALL"
HASH="$(sha256sum "$TARBALL" | awk '{print $1}')"
SIZE="$(wc -c < "$TARBALL" | tr -d ' ')"
printf '%s  %s\n' "$HASH" "$NAME.tar.gz" > "$TARBALL.sha256"
cat > "$OUT/latest.json" <<JSON
{"version":"$VERSION","file":"$NAME.tar.gz","sha256":"$HASH","size":$SIZE}
JSON
say "package: $TARBALL ($(du -h "$TARBALL" | awk '{print $1}'))"
say "SHA-256: $HASH ($SIZE bytes)"
say "feed: $OUT/latest.json"
say "done: $STAGE"
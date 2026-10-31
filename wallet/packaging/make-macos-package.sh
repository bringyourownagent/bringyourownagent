#!/bin/bash
# Builds the ready-to-use package of the BYOA agent wallet for macOS (Apple silicon): byoa-wallet-<version>-mac-arm64.zip.
#
#   wallet/packaging/make-macos-package.sh --out DIR [--version 0.1.0] [--sign none|adhoc|identity] [--notarize]
#
# Run it on a Mac with the .NET 10 SDK (it needs dotnet, plutil, otool, codesign, ditto, file, shasum and python3).
#
# 1. Publishes the tray app (desktop/Byoa.Wallet.Desktop), the native messaging host (host/Byoa.Wallet.Host) and the MCP bridge
#    (mcp/Byoa.Wallet.Mcp), Release, self-contained for osx-arm64 (the .NET runtime is included: the Mac needs no .NET) and as single
#    files, not trimmed, into one folder under --out. Single files, because code signing treats every file of Contents/MacOS as code: a
#    folder of managed .dll files cannot be sealed there. Avalonia's native libraries stay beside the wallet program.
# 2. Makes "Agent Wallet.app": the programs and the runtime in Contents/MacOS, the icon and the browser extension in
#    Contents/Resources, an Info.plist that registers the bringyourownagent:// links and makes the wallet a menu-bar program.
# 3. Signs it (--sign) and has Apple notarize it (--notarize), then zips the package folder byoa-wallet-<version>-mac-arm64:
#      Agent Wallet.app   the wallet
#      README.txt, LICENSE, NOTICE, THIRD-PARTY-NOTICES.txt, licenses/
#
#   --sign none      nothing is signed (macOS refuses to open it from the internet; fine for a test on the Mac that built it)
#   --sign adhoc     "codesign -s -": runs on the Mac that built it, no hardened runtime
#   --sign identity  the Developer ID identity named by BYOA_SIGN_IDENTITY (default "Developer ID Application"), hardened runtime, the
#                    entitlements of wallet/packaging/macos/wallet.entitlements, a secure timestamp. BYOA_KEYCHAIN names a keychain
#                    FILE to take the identity from. From a plain ssh session codesign cannot use a keychain identity
#                    (errSecInternalComponent): run this script inside the desktop session of the user (launchctl asuser).
#   --notarize       (with --sign identity) sends the app to Apple (xcrun notarytool submit --wait, the credentials profile named by
#                    BYOA_NOTARY_PROFILE, keychain BYOA_KEYCHAIN), staples the ticket and checks it with spctl.
#
# The script deletes, moves and renames nothing: --out must be a folder that does not exist yet.

set -euo pipefail

APP_NAME="Agent Wallet"
BUNDLE_ID="org.bringyourownagent.wallet"
FLOOR_MACOS="12.0"
RID="osx-arm64"

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WALLET="$(cd "$HERE/.." && pwd)"
REPO="$(cd "$WALLET/.." && pwd)"
MACOS_DIR="$HERE/macos"

OUT=""
VERSION=""
SIGN="none"
NOTARIZE=0

die() { echo "make-macos-package: $*" >&2; exit 1; }
say() { echo "make-macos-package: $*"; }

while [ $# -gt 0 ]; do
  case "$1" in
    --out) OUT="${2:?--out needs a folder}"; shift 2 ;;
    --version) VERSION="${2:?--version needs a value}"; shift 2 ;;
    --sign) SIGN="${2:?--sign needs none|adhoc|identity}"; shift 2 ;;
    --notarize) NOTARIZE=1; shift ;;
    *) die "unknown argument: $1" ;;
  esac
done

[ "$(uname -s)" = "Darwin" ] || die "run this script on a Mac"
[ "$(uname -m)" = "arm64" ] || say "note: this Mac is not Apple silicon; the package is built for $RID all the same"
for tool in dotnet plutil otool codesign ditto file shasum awk sort cp mkdir chmod python3 find; do
  command -v "$tool" > /dev/null 2>&1 || die "needed tool not found: $tool"
done
[ -n "$OUT" ] || die "--out is required"
case "$SIGN" in none|adhoc|identity) ;; *) die "--sign must be none, adhoc or identity" ;; esac
[ "$NOTARIZE" -eq 0 ] || [ "$SIGN" = "identity" ] || die "--notarize needs --sign identity"
if [ -z "$VERSION" ]; then
  VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$WALLET/Directory.Build.props" | head -1)"
  [ -n "$VERSION" ] || die "no <Version> in wallet/Directory.Build.props; pass --version"
fi
case "$VERSION" in [0-9]*.[0-9]*.[0-9]*) ;; *) die "version '$VERSION' is not like 0.1.0" ;; esac
plutil -lint "$MACOS_DIR/wallet.entitlements" > /dev/null || die "the entitlements file is not valid"

[ ! -e "$OUT" ] || die "$OUT exists: this script deletes nothing, so choose a folder that does not exist"
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"
NAME="byoa-wallet-$VERSION-mac-arm64"
STAGE="$OUT/$NAME"
APP="$STAGE/$APP_NAME.app"
MACOS="$APP/Contents/MacOS"
RESOURCES="$APP/Contents/Resources"
PUBLISH="$OUT/publish"
ARTIFACTS="$OUT/artifacts"

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 AVALONIA_TELEMETRY_OPTOUT=1

# ---- 1. publish ---------------------------------------------------------------------------------------------------------------------
mkdir -p "$PUBLISH/app"
for project in desktop/Byoa.Wallet.Desktop/Byoa.Wallet.Desktop.csproj host/Byoa.Wallet.Host/Byoa.Wallet.Host.csproj mcp/Byoa.Wallet.Mcp/Byoa.Wallet.Mcp.csproj; do
  say "publishing $project (Release, self-contained, $RID)"
  dotnet publish "$WALLET/$project" -c Release -r "$RID" --self-contained true \
    -p:PublishTrimmed=false -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=false -p:IncludeNativeLibrariesForSelfExtract=false \
    -p:DebugType=none -p:DebugSymbols=false -p:Version="$VERSION" \
    --artifacts-path "$ARTIFACTS" -o "$PUBLISH/app" --nologo -v quiet
done
for file in Byoa.Wallet byoa-host byoa-mcp libAvaloniaNative.dylib libSkiaSharp.dylib libHarfBuzzSharp.dylib; do
  [ -e "$PUBLISH/app/$file" ] || die "$file is missing in $PUBLISH/app"
done

# ---- 2. the bundle ------------------------------------------------------------------------------------------------------------------
say "assembling $APP"
mkdir -p "$MACOS" "$RESOURCES"
for f in "$PUBLISH/app"/*; do
  case "$f" in
    *.pdb) ;;
    */icons) ;;      # the PNG icons beside the program are for the Linux desktop entries; the app's icon here is Byoa.icns, and a PNG in Contents/MacOS would be taken for code
    *) cp -Rp "$f" "$MACOS/" ;;
  esac
done
chmod +x "$MACOS/Byoa.Wallet" "$MACOS/byoa-host" "$MACOS/byoa-mcp"
cp "$MACOS_DIR/Byoa.icns" "$RESOURCES/Byoa.icns"
mkdir -p "$RESOURCES/extension"
cp -Rp "$WALLET/extension/." "$RESOURCES/extension/"
printf 'APPL????' > "$APP/Contents/PkgInfo"

# the minimum macOS is the highest "minos" of the arm64 slices, never below the floor
ver_max() { printf '%s\n%s\n' "$1" "$2" | sort -t. -k1,1n -k2,2n | tail -1; }
MAX_MINOS="0.0"
while IFS= read -r f; do
  case "$(file -b "$f" | sed -n 1p)" in
    *Mach-O*arm64*) ;;
    *Mach-O*) die "a Mach-O file without an arm64 slice: $f" ;;
    *) continue ;;
  esac
  m="$(otool -arch arm64 -l "$f" 2>/dev/null | awk '
      /^ *cmd LC_BUILD_VERSION/ { b = 1; next }
      /^ *cmd LC_VERSION_MIN_MACOSX/ { v = 1; next }
      b && /^ *minos / { print $2; exit }
      v && /^ *version / { print $2; exit }' || true)"
  if [ -n "$m" ]; then
    m="$(printf '%s' "$m" | cut -d. -f1-2)"
    case "$m" in *.*) ;; *) m="$m.0" ;; esac
    MAX_MINOS="$(ver_max "$m" "$MAX_MINOS")"
  fi
done < <(find "$MACOS" -type f)
MIN_MACOS="$(ver_max "$MAX_MINOS" "$FLOOR_MACOS")"
say "highest minimum macOS of the native files: $MAX_MINOS; declared LSMinimumSystemVersion: $MIN_MACOS"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key>
  <string>en</string>
  <key>CFBundleExecutable</key>
  <string>Byoa.Wallet</string>
  <key>CFBundleIconFile</key>
  <string>Byoa</string>
  <key>CFBundleIdentifier</key>
  <string>$BUNDLE_ID</string>
  <key>CFBundleInfoDictionaryVersion</key>
  <string>6.0</string>
  <key>CFBundleName</key>
  <string>$APP_NAME</string>
  <key>CFBundleDisplayName</key>
  <string>$APP_NAME</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>$VERSION</string>
  <key>CFBundleVersion</key>
  <string>$VERSION</string>
  <key>CFBundleSignature</key>
  <string>????</string>
  <key>CFBundleURLTypes</key>
  <array>
    <dict>
      <key>CFBundleURLName</key>
      <string>Bring Your Own Agent link</string>
      <key>CFBundleTypeRole</key>
      <string>Viewer</string>
      <key>CFBundleURLSchemes</key>
      <array>
        <string>bringyourownagent</string>
      </array>
    </dict>
  </array>
  <key>LSApplicationCategoryType</key>
  <string>public.app-category.utilities</string>
  <key>LSMinimumSystemVersion</key>
  <string>$MIN_MACOS</string>
  <key>LSMultipleInstancesProhibited</key>
  <true/>
  <key>LSUIElement</key>
  <true/>
  <key>NSHighResolutionCapable</key>
  <true/>
  <key>NSPrincipalClass</key>
  <string>NSApplication</string>
  <key>NSSupportsAutomaticTermination</key>
  <false/>
  <key>NSSupportsSuddenTermination</key>
  <false/>
  <key>NSLocalNetworkUsageDescription</key>
  <string>The wallet passes requests to the AI programs you run on this computer or on your network (the Llama app, LM Studio, Ollama).</string>
  <key>NSHumanReadableCopyright</key>
  <string>Apache License 2.0. BYOA agent wallet $VERSION</string>
</dict>
</plist>
PLIST
plutil -lint "$APP/Contents/Info.plist" > /dev/null || die "plutil -lint says Info.plist is not valid"

# ---- the notices, outside the bundle (a folder with a dot in its name inside a bundle is taken by codesign for a nested bundle) -----------
python3 "$HERE/copy_package_licenses.py" --artifacts "$ARTIFACTS" --out "$STAGE/licenses" --fonts-license "$WALLET/desktop/Byoa.Wallet.Desktop/Assets/Fonts/OFL.txt"
sed "s/{VERSION}/$VERSION/g" "$MACOS_DIR/README.txt" > "$STAGE/README.txt"
sed "s/{VERSION}/$VERSION/g" "$MACOS_DIR/THIRD-PARTY-NOTICES.txt" > "$STAGE/THIRD-PARTY-NOTICES.txt"
cp "$REPO/LICENSE" "$STAGE/LICENSE"
cp "$REPO/NOTICE" "$STAGE/NOTICE"

# ---- checks of the staged bundle -----------------------------------------------------------------------------------------------------
PROBLEMS=0
bad() { echo "make-macos-package: CHECK FAILED: $*" >&2; PROBLEMS=$((PROBLEMS + 1)); }
plist_get() { plutil -extract "$1" raw -o - "$APP/Contents/Info.plist" 2> /dev/null || true; }
expect_plist() { [ "$(plist_get "$1")" = "$2" ] || bad "Info.plist $1 is '$(plist_get "$1")', expected '$2'"; }
expect_plist CFBundleIdentifier "$BUNDLE_ID"
expect_plist CFBundleExecutable "Byoa.Wallet"
expect_plist CFBundleShortVersionString "$VERSION"
expect_plist LSUIElement true
expect_plist LSMinimumSystemVersion "$MIN_MACOS"
expect_plist CFBundleURLTypes.0.CFBundleURLSchemes.0 bringyourownagent
for exe in Byoa.Wallet byoa-host byoa-mcp; do
  case "$(file -b "$MACOS/$exe" | sed -n 1p)" in *"Mach-O"*executable*arm64*) ;; *) bad "$exe is not an arm64 Mach-O program" ;; esac
done
for lib in libAvaloniaNative.dylib libSkiaSharp.dylib libHarfBuzzSharp.dylib; do
  [ -f "$MACOS/$lib" ] || bad "missing: $lib"
done
[ -f "$RESOURCES/extension/manifest.json" ] || bad "the extension is missing in Resources"
[ "$(find "$APP" -name '*.pdb' | wc -l | tr -d ' ')" = "0" ] || bad "the bundle holds .pdb files"
[ "$(find "$APP" -type d -name '*.*' ! -name '*.app' | wc -l | tr -d ' ')" = "0" ] || bad "a folder with a dot in its name inside the bundle (codesign takes it for a nested bundle)"
[ "$PROBLEMS" -eq 0 ] || die "$PROBLEMS check(s) failed; the bundle is left for inspection: $APP"
say "the staged bundle is fine"

# Every Mach-O file that has to be signed: the libraries first, then the three programs, then the bundle seals the rest.
LIBS=()
PROGRAMS=("$MACOS/byoa-host" "$MACOS/byoa-mcp" "$MACOS/Byoa.Wallet")
while IFS= read -r f; do
  case "$f" in "$MACOS/Byoa.Wallet"|"$MACOS/byoa-host"|"$MACOS/byoa-mcp") continue ;; esac
  case "$(file -b "$f" | sed -n 1p)" in
    *"Mach-O"*) LIBS+=("$f") ;;
  esac
done < <(find "$MACOS" -type f | sort)
[ "${#LIBS[@]}" -ge 3 ] || die "expected at least 3 libraries in the bundle, found ${#LIBS[@]}"
# Nothing but code may sit in Contents/MacOS: code signing takes every file there for code and refuses what is not signed
OTHER=""
while IFS= read -r f; do
  case "$(file -b "$f" | sed -n 1p)" in
    *Mach-O*) ;;
    *) OTHER="$OTHER $f" ;;
  esac
done < <(find "$MACOS" -type f)
[ -z "$OTHER" ] || die "files in Contents/MacOS that are not Mach-O code:$OTHER"

# ---- 3. signing ---------------------------------------------------------------------------------------------------------------------
IDENTITY="${BYOA_SIGN_IDENTITY:-Developer ID Application}"
KEYCHAIN_ARGS=()
if [ -n "${BYOA_KEYCHAIN:-}" ]; then KEYCHAIN_ARGS=(--keychain "$BYOA_KEYCHAIN"); fi

case "$SIGN" in
  none)
    say "UNSIGNED (--sign none)"
    ;;
  adhoc)
    say "signing ad hoc (no hardened runtime): good for this Mac only"
    for f in "${LIBS[@]}"; do codesign --force --sign - "$f"; done
    for f in "${PROGRAMS[@]}"; do codesign --force --sign - --entitlements "$MACOS_DIR/wallet.entitlements" "$f"; done
    codesign --force --sign - --entitlements "$MACOS_DIR/wallet.entitlements" "$APP"
    codesign --verify --deep --strict --verbose=2 "$APP"
    ;;
  identity)
    say "signing with the identity '$IDENTITY' (hardened runtime, secure timestamp)"
    for f in "${LIBS[@]}"; do
      codesign --force --timestamp --options runtime ${KEYCHAIN_ARGS[@]+"${KEYCHAIN_ARGS[@]}"} --sign "$IDENTITY" "$f"
    done
    for f in "${PROGRAMS[@]}"; do
      codesign --force --timestamp --options runtime --entitlements "$MACOS_DIR/wallet.entitlements" ${KEYCHAIN_ARGS[@]+"${KEYCHAIN_ARGS[@]}"} --sign "$IDENTITY" "$f"
    done
    codesign --force --timestamp --options runtime --entitlements "$MACOS_DIR/wallet.entitlements" ${KEYCHAIN_ARGS[@]+"${KEYCHAIN_ARGS[@]}"} --sign "$IDENTITY" "$APP"
    for f in "${LIBS[@]}" "${PROGRAMS[@]}"; do codesign --verify --strict "$f"; done
    codesign --verify --deep --strict --verbose=2 "$APP"
    codesign -dv --verbose=2 "$APP" 2>&1 | grep -E "Identifier|Authority|TeamIdentifier|flags|Timestamp|Runtime" || true
    ;;
esac

# ---- notarization -------------------------------------------------------------------------------------------------------------------
if [ "$NOTARIZE" -eq 1 ]; then
  : "${BYOA_NOTARY_PROFILE:?set BYOA_NOTARY_PROFILE (a profile stored with xcrun notarytool store-credentials)}"
  NOTARY_KC=()
  if [ -n "${BYOA_KEYCHAIN:-}" ]; then NOTARY_KC=(--keychain "$BYOA_KEYCHAIN"); fi
  SUBMIT_ZIP="$OUT/$NAME-for-notary.zip"
  [ ! -e "$SUBMIT_ZIP" ] || die "$SUBMIT_ZIP already exists"
  ditto -c -k --keepParent "$APP" "$SUBMIT_ZIP"
  say "sending the app to Apple (notarytool submit --wait)"
  RESULT="$OUT/notary-result.txt"
  [ ! -e "$RESULT" ] || die "$RESULT already exists"
  xcrun notarytool submit "$SUBMIT_ZIP" --keychain-profile "$BYOA_NOTARY_PROFILE" ${NOTARY_KC[@]+"${NOTARY_KC[@]}"} --wait 2>&1 | tee "$RESULT"
  if ! grep -q "status: Accepted" "$RESULT"; then
    SUB_ID="$(sed -n 's/^ *id: *//p' "$RESULT" | head -1)"
    [ -z "$SUB_ID" ] || xcrun notarytool log "$SUB_ID" --keychain-profile "$BYOA_NOTARY_PROFILE" ${NOTARY_KC[@]+"${NOTARY_KC[@]}"} 2>&1 | head -60 || true
    die "Apple did not accept the app (see $RESULT); the signed, not notarized app is $APP"
  fi
  say "Apple accepted the app; stapling"
  xcrun stapler staple "$APP"
  xcrun stapler validate "$APP"
  spctl --assess --type execute --verbose=2 "$APP"
fi

# ---- the archive --------------------------------------------------------------------------------------------------------------------
ZIP="$OUT/$NAME.zip"
[ ! -e "$ZIP" ] || die "$ZIP already exists"
ditto -c -k --sequesterRsrc --keepParent "$STAGE" "$ZIP"
HASH="$(shasum -a 256 "$ZIP" | awk '{print $1}')"
printf '%s  %s\n' "$HASH" "$NAME.zip" > "$ZIP.sha256"
say "package: $ZIP ($(du -h "$ZIP" | awk '{print $1}')), SHA-256 $HASH"
say "done: $APP ($(du -sh "$APP" | awk '{print $1}'))"

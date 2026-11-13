#!/usr/bin/env bash
set -euo pipefail

version="${1:?usage: build-macos-release.sh VERSION [OUTPUT_DIR] [RID]}"
output_root="${2:-artifacts/macos}"
rid="${3:-osx-arm64}"

case "$rid" in
  osx-x64|osx-arm64) ;;
  *) echo "Unsupported macOS RID: $rid" >&2; exit 2 ;;
esac

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$repo_root/scripts/dotnet-sdk.sh"
require_repo_dotnet_sdk "$repo_root"
output_root="$(mkdir -p "$output_root" && cd "$output_root" && pwd)"
work_root="$(mktemp -d)"
trap 'rm -rf "$work_root"' EXIT
publish_root="$work_root/publish"
app_root="$work_root/Kkindle.app"

# Keep prerelease suffixes in the archive name, but use Apple-compatible
# numeric values inside Info.plist.
short_version="${version%%-*}"
build_version="$short_version"
if [[ ! "$short_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "Version must contain three numeric components: $version" >&2
  exit 2
fi

cd "$repo_root"
dotnet publish "src/Kkindle.Desktop.MacOS/Kkindle.Desktop.MacOS.csproj" \
  -c Release -r "$rid" --self-contained true \
  -p:Version="$version" -p:PublishSingleFile=false \
  -o "$publish_root"

mkdir -p "$app_root/Contents/MacOS" "$app_root/Contents/Resources"
cp -a "$publish_root/." "$app_root/Contents/MacOS/"
cp "$repo_root/LICENSE" "$app_root/Contents/Resources/LICENSE"
bash "$repo_root/scripts/macos-app-icon.sh" "$app_root/Contents/Resources/Kkindle.icns"
chmod 0755 "$app_root/Contents/MacOS/Kkindle"

cat > "$app_root/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDisplayName</key><string>Kkindle</string>
  <key>CFBundleExecutable</key><string>Kkindle</string>
  <key>CFBundleIdentifier</key><string>io.github.kingstacker.kkindle</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>CFBundleName</key><string>Kkindle</string>
  <key>CFBundleIconFile</key><string>Kkindle.icns</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$short_version</string>
  <key>CFBundleVersion</key><string>$build_version</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
EOF

# Ad-hoc signing keeps locally built bundles internally consistent. Set
# APPLE_SIGNING_IDENTITY to a Developer ID Application identity for a
# distributable hardened-runtime signature. If APPLE_NOTARY_PROFILE names a
# notarytool keychain profile, the finished bundle is also notarized/stapled.
signing_identity="${APPLE_SIGNING_IDENTITY:--}"
require_notarization="${APPLE_REQUIRE_NOTARIZATION:-0}"
notary_key_path="${APPLE_NOTARY_KEY_PATH:-}"
notary_key_id="${APPLE_NOTARY_KEY_ID:-}"
notary_issuer="${APPLE_NOTARY_ISSUER:-}"
notary_profile="${APPLE_NOTARY_PROFILE:-}"
if [[ "$require_notarization" == "1" ]]; then
  if [[ "$signing_identity" == "-" ]]; then
    echo "APPLE_SIGNING_IDENTITY is required for a distributable macOS build." >&2
    exit 2
  fi
  if [[ -z "$notary_profile" && ( -z "$notary_key_path" || -z "$notary_key_id" || -z "$notary_issuer" ) ]]; then
    echo "Notarization credentials are required; set APPLE_NOTARY_* or APPLE_NOTARY_PROFILE." >&2
    exit 2
  fi
fi
if [[ "$signing_identity" == "-" ]]; then
  codesign --force --deep --sign - "$app_root"
else
  codesign --force --deep --options runtime --timestamp \
    --sign "$signing_identity" "$app_root"
fi
codesign --verify --deep --strict "$app_root"

if [[ -n "$notary_key_path" ]]; then
  notarization_zip="$work_root/Kkindle-notarization.zip"
  ditto -c -k --keepParent "$app_root" "$notarization_zip"
  xcrun notarytool submit "$notarization_zip" \
    --key "$notary_key_path" --key-id "$notary_key_id" --issuer "$notary_issuer" --wait
  xcrun stapler staple "$app_root"
  xcrun stapler validate "$app_root"
elif [[ -n "$notary_profile" ]]; then
  notarization_zip="$work_root/Kkindle-notarization.zip"
  ditto -c -k --keepParent "$app_root" "$notarization_zip"
  xcrun notarytool submit "$notarization_zip" \
    --keychain-profile "$notary_profile" --wait
  xcrun stapler staple "$app_root"
  xcrun stapler validate "$app_root"
fi

if [[ "$signing_identity" == "-" ]]; then
  cp "$repo_root/docs/macos-adhoc-install.md" "$work_root/MACOS-README.md"
  tar -C "$work_root" -czf "$output_root/Kkindle-$version-$rid.tar.gz" \
    Kkindle.app MACOS-README.md
else
  tar -C "$work_root" -czf "$output_root/Kkindle-$version-$rid.tar.gz" Kkindle.app
fi

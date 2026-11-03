#!/usr/bin/env bash
set -euo pipefail

version="${1:?usage: build-macos-release.sh VERSION [OUTPUT_DIR] [RID]}"
output_root="${2:-artifacts/macos}"
rid="${3:-osx-arm64}"

case "$rid" in
  osx-x64|osx-arm64|osx-universal) ;;
  *) echo "Unsupported macOS RID: $rid" >&2; exit 2 ;;
esac

# A self-contained .NET publish embeds architecture-specific native code and
# ReadyToRun framework assemblies, so the two RIDs cannot be lipo-merged into
# one set of files. osx-universal therefore keeps both complete payloads in
# the bundle behind a universal launcher that execs the matching one.
if [[ "$rid" == "osx-universal" ]]; then
  payload_rids=(osx-x64 osx-arm64)
  universal=1
else
  payload_rids=("$rid")
  universal=0
fi

payload_arch() {
  if [[ "$1" == "osx-x64" ]]; then echo x86_64; else echo arm64; fi
}

# libmtp is built from source per target architecture. An Intel host can
# cross-compile the arm64 libraries (and vice versa), so the package never
# depends on the build host matching the target.

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$repo_root/scripts/dotnet-sdk.sh"
require_repo_dotnet_sdk "$repo_root"
output_root="$(mkdir -p "$output_root" && cd "$output_root" && pwd)"
work_root="$(mktemp -d)"
trap 'rm -rf "$work_root"' EXIT
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
app_macos="$app_root/Contents/MacOS"
mkdir -p "$app_macos" "$app_root/Contents/Resources"

for payload_rid in "${payload_rids[@]}"; do
  arch="$(payload_arch "$payload_rid")"
  if (( universal )); then
    payload_dir="$app_macos/$arch"
  else
    payload_dir="$app_macos"
  fi
  mkdir -p "$payload_dir"

  publish_root="$work_root/publish/$payload_rid"
  dotnet publish "src/Kkindle.Desktop.MacOS/Kkindle.Desktop.MacOS.csproj" \
    -c Release -r "$payload_rid" --self-contained true \
    -p:Version="$version" -p:PublishSingleFile=false \
    -o "$publish_root"
  cp -a "$publish_root/." "$payload_dir/"

  # The MCP server is a separate executable. Ship a self-contained build in
  # the payload's mcp directory, the location Settings > MCP probes.
  mcp_publish_root="$work_root/mcp-publish/$payload_rid"
  dotnet publish "src/Kkindle.McpServer/Kkindle.McpServer.csproj" \
    -c Release -f net10.0 -r "$payload_rid" --self-contained true \
    -p:Version="$version" -p:PublishSingleFile=false \
    -o "$mcp_publish_root"
  mkdir -p "$payload_dir/mcp"
  cp -a "$mcp_publish_root/." "$payload_dir/mcp/"
  if [[ ! -f "$payload_dir/mcp/Kkindle.McpServer" ]]; then
    echo "Published output does not contain the MCP server executable." >&2
    exit 2
  fi
  chmod 0755 "$payload_dir/mcp/Kkindle.McpServer"
  chmod 0755 "$payload_dir/Kkindle"

  # Newer Kindles only speak MTP, which macOS cannot mount. Bundle libmtp
  # (the same stack calibre uses on macOS) next to the payload executable so
  # the managed resolver finds it with @loader_path.
  mtp_deps="$repo_root/artifacts/macos-deps/$arch"
  bash "$repo_root/scripts/build-macos-mtp-deps.sh" "$mtp_deps" "$arch"
  cp "$mtp_deps/prefix/lib/libmtp.9.dylib" "$mtp_deps/prefix/lib/libusb-1.0.0.dylib" \
    "$payload_dir/"
done

if (( universal )); then
  # One fat executable picks the payload matching the architecture it was
  # started as. exec keeps the bundle identity, so the nested app still
  # resolves Contents/... paths and the update helper can replace Kkindle.app.
  launcher_source="$work_root/launcher.c"
  cat > "$launcher_source" <<'LAUNCHER'
#include <limits.h>
#include <mach-o/dyld.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

int main(int argc, char **argv)
{
    char executable[PATH_MAX];
    uint32_t size = sizeof(executable);
    if (_NSGetExecutablePath(executable, &size) != 0) return 127;

    char resolved[PATH_MAX];
    if (realpath(executable, resolved) == NULL) return 127;
    char *slash = strrchr(resolved, '/');
    if (slash == NULL) return 127;
    *slash = '\0';

#if defined(__arm64__) || defined(__aarch64__)
    const char *arch = "arm64";
#else
    const char *arch = "x86_64";
#endif
    char target[PATH_MAX];
    int written = snprintf(target, sizeof(target), "%s/%s/Kkindle", resolved, arch);
    if (written < 0 || (size_t)written >= sizeof(target)) return 127;

    execv(target, argv);
    perror("Kkindle launcher");
    return 127;
}
LAUNCHER
  for arch in x86_64 arm64; do
    clang -arch "$arch" -mmacosx-version-min=12.0 -O2 \
      -o "$work_root/launcher-$arch" "$launcher_source"
  done
  lipo -create -output "$app_macos/Kkindle" \
    "$work_root/launcher-x86_64" "$work_root/launcher-arm64"
  chmod 0755 "$app_macos/Kkindle"
fi

cp "$repo_root/LICENSE" "$app_root/Contents/Resources/LICENSE"
cp "$repo_root/src/Kkindle.App/Assets/Icons/Kkindle.ico" "$app_root/Contents/Resources/Kkindle.ico"

# macOS uses .icns for the bundle/Dock icon. Build one from the rounded
# macOS-style PNG so the Finder and Dock show the Kkindle icon instead of the
# generic app icon.
icon_source="$repo_root/src/Kkindle.App/Assets/Icons/kkindle-dock.png"
if [[ ! -f "$icon_source" ]]; then
  echo "Missing app icon source: $icon_source" >&2
  exit 2
fi
iconset="$work_root/Kkindle.iconset"
mkdir -p "$iconset"
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" "$icon_source" --out "$iconset/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z "$double" "$double" "$icon_source" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$app_root/Contents/Resources/Kkindle.icns"

# The bundled MTP libraries and their licences are copied with each payload.
mtp_deps="$repo_root/artifacts/macos-deps/$(payload_arch "${payload_rids[0]}")"
mkdir -p "$app_root/Contents/Resources/ThirdParty"
cp "$mtp_deps/prefix/share/licenses/libmtp-COPYING" \
  "$app_root/Contents/Resources/ThirdParty/libmtp-COPYING"
cp "$mtp_deps/prefix/share/licenses/libusb-COPYING" \
  "$app_root/Contents/Resources/ThirdParty/libusb-COPYING"

cat > "$app_root/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDisplayName</key><string>Kkindle</string>
  <key>CFBundleExecutable</key><string>Kkindle</string>
  <key>CFBundleIdentifier</key><string>io.github.kingstacker.kkindle</string>
  <key>CFBundleIconFile</key><string>Kkindle.icns</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>CFBundleDevelopmentRegion</key><string>en</string>
  <key>CFBundleLocalizations</key><array><string>en</string><string>zh-Hans</string></array>
  <key>CFBundleName</key><string>Kkindle</string>
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
mcp_executables=()
for payload_rid in "${payload_rids[@]}"; do
  if (( universal )); then
    mcp_executables+=("$app_macos/$(payload_arch "$payload_rid")/mcp/Kkindle.McpServer")
  else
    mcp_executables+=("$app_macos/mcp/Kkindle.McpServer")
  fi
done

if [[ "$signing_identity" == "-" ]]; then
  for executable in "${mcp_executables[@]}"; do
    codesign --force --sign - "$executable"
  done
  codesign --force --deep --sign - "$app_root"
else
  for executable in "${mcp_executables[@]}"; do
    codesign --force --options runtime --timestamp \
      --sign "$signing_identity" "$executable"
  done
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

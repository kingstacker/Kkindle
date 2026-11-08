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
mtp_source_bundle="$app_root/Contents/Resources/CorrespondingSource/MTP"
mkdir -p "$app_macos" "$app_root/Contents/Resources" "$mtp_source_bundle"

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
  mtp_source="$mtp_deps/prefix/share/kkindle/corresponding-source/MTP"
  dotnet run --file "$repo_root/scripts/verify-third-party.cs" -- \
    --mtp-source "$mtp_source" --mtp-architecture "$arch" --mtp-binaries "$payload_dir"
  # Universal payloads share exact source archives and script, but each keeps
  # its own build metadata. Reject a mismatched common source before merging.
  for material in "$mtp_source"/*; do
    name="$(basename "$material")"
    [[ "$name" == SHA256SUMS.txt ]] && continue
    if [[ -f "$mtp_source_bundle/$name" ]] && ! cmp -s "$material" "$mtp_source_bundle/$name"; then
      echo "Mismatched MTP corresponding-source material: $name" >&2
      exit 2
    fi
    cp "$material" "$mtp_source_bundle/$name"
  done
done

(
  cd "$mtp_source_bundle"
  shasum -a 256 libusb-*.tar.bz2 libmtp-*.tar.gz libusb-COPYING libmtp-COPYING \
    build-macos-mtp-deps.sh README.md BUILD-METADATA-*.txt > SHA256SUMS.txt
)
dotnet run --file "$repo_root/scripts/verify-third-party.cs" -- --mtp-source "$mtp_source_bundle"

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
cp "$repo_root/THIRD-PARTY-NOTICES.md" "$app_root/Contents/Resources/THIRD-PARTY-NOTICES.md"
# Keep the notice index's relative links readable from Resources as well as
# from each executable payload. These directories contain license materials.
notice_payload="$app_macos"
if (( universal )); then
  notice_payload="$app_macos/$(payload_arch "${payload_rids[0]}")"
fi
mkdir -p "$app_root/Contents/Resources/ThirdParty" \
  "$app_root/Contents/Resources/Assets/Fonts" "$app_root/Contents/Resources/Assets/Icons"
cp -a "$notice_payload/ThirdParty/." "$app_root/Contents/Resources/ThirdParty/"
cp "$notice_payload/Assets/Fonts/"*-LICENSE.txt "$app_root/Contents/Resources/Assets/Fonts/"
cp "$notice_payload/Assets/Icons/Lucide-LICENSE.txt" "$app_root/Contents/Resources/Assets/Icons/"
cp "$repo_root/src/Kkindle.App/Assets/Icons/Kkindle.ico" "$app_root/Contents/Resources/Kkindle.ico"

# Compile the layered Icon Composer artwork for Liquid Glass appearances and
# a complete .icns fallback for older macOS releases. Both must be generated
# together so their name and geometry match. This requires Xcode 26 or newer.
icon_source="$repo_root/scripts/macos/Kkindle.icon"
icon_output="$work_root/compiled-icon"
if [[ ! -f "$icon_source/icon.json" ]]; then
  echo "Missing Icon Composer source: $icon_source" >&2
  exit 2
fi
mkdir -p "$icon_output"
xcrun actool --compile "$icon_output" \
  --platform macosx --target-device mac --minimum-deployment-target 12.0 \
  --app-icon Kkindle --standalone-icon-behavior all \
  --output-partial-info-plist "$icon_output/partial.plist" \
  --warnings --notices --errors "$icon_source"
if [[ ! -s "$icon_output/Assets.car" || ! -s "$icon_output/Kkindle.icns" ]]; then
  echo "Icon compilation did not produce both Assets.car and Kkindle.icns; use Xcode 26 or newer." >&2
  exit 2
fi
cp "$icon_output/Assets.car" "$app_root/Contents/Resources/Assets.car"
cp "$icon_output/Kkindle.icns" "$app_root/Contents/Resources/Kkindle.icns"

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
  <key>CFBundleIconFile</key><string>Kkindle</string>
  <key>CFBundleIconName</key><string>Kkindle</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>CFBundleDevelopmentRegion</key><string>en</string>
  <key>CFBundleLocalizations</key><array><string>en</string><string>zh-Hans</string></array>
  <key>CFBundleName</key><string>Kkindle</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$short_version</string>
  <key>CFBundleVersion</key><string>$build_version</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>CFBundleDocumentTypes</key>
  <array>
    <dict>
      <key>CFBundleTypeName</key><string>EPUB</string>
      <key>CFBundleTypeRole</key><string>Viewer</string>
      <key>LSHandlerRank</key><string>Alternate</string>
      <key>LSItemContentTypes</key><array><string>org.idpf.epub-container</string></array>
      <key>CFBundleTypeExtensions</key><array><string>epub</string></array>
    </dict>
    <dict>
      <key>CFBundleTypeName</key><string>PDF</string>
      <key>CFBundleTypeRole</key><string>Viewer</string>
      <key>LSHandlerRank</key><string>Alternate</string>
      <key>LSItemContentTypes</key><array><string>com.adobe.pdf</string></array>
      <key>CFBundleTypeExtensions</key><array><string>pdf</string></array>
    </dict>
    <dict>
      <key>CFBundleTypeName</key><string>Kindle eBook</string>
      <key>CFBundleTypeRole</key><string>Viewer</string>
      <key>LSHandlerRank</key><string>Alternate</string>
      <key>LSItemContentTypes</key><array>
        <string>io.github.kingstacker.kkindle.mobi</string>
        <string>io.github.kingstacker.kkindle.azw</string>
        <string>io.github.kingstacker.kkindle.azw3</string>
      </array>
      <key>CFBundleTypeExtensions</key><array><string>mobi</string><string>prc</string><string>azw</string><string>azw3</string></array>
    </dict>
    <dict>
      <key>CFBundleTypeName</key><string>Plain Text</string>
      <key>CFBundleTypeRole</key><string>Viewer</string>
      <key>LSHandlerRank</key><string>Alternate</string>
      <key>LSItemContentTypes</key><array><string>public.plain-text</string></array>
      <key>CFBundleTypeExtensions</key><array><string>txt</string></array>
    </dict>
  </array>
  <key>UTExportedTypeDeclarations</key>
  <array>
    <dict>
      <key>UTTypeIdentifier</key><string>io.github.kingstacker.kkindle.mobi</string>
      <key>UTTypeDescription</key><string>Mobipocket eBook</string>
      <key>UTTypeConformsTo</key><array><string>public.data</string></array>
      <key>UTTypeTagSpecification</key><dict>
        <key>public.filename-extension</key><array><string>mobi</string><string>prc</string></array>
        <key>public.mime-type</key><string>application/x-mobipocket-ebook</string>
      </dict>
    </dict>
    <dict>
      <key>UTTypeIdentifier</key><string>io.github.kingstacker.kkindle.azw</string>
      <key>UTTypeDescription</key><string>Kindle eBook</string>
      <key>UTTypeConformsTo</key><array><string>public.data</string></array>
      <key>UTTypeTagSpecification</key><dict>
        <key>public.filename-extension</key><array><string>azw</string></array>
      </dict>
    </dict>
    <dict>
      <key>UTTypeIdentifier</key><string>io.github.kingstacker.kkindle.azw3</string>
      <key>UTTypeDescription</key><string>Kindle eBook (AZW3)</string>
      <key>UTTypeConformsTo</key><array><string>public.data</string></array>
      <key>UTTypeTagSpecification</key><dict>
        <key>public.filename-extension</key><array><string>azw3</string></array>
      </dict>
    </dict>
  </array>
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
entitlements_file="$repo_root/scripts/macos/Kkindle.entitlements"
if [[ ! -f "$entitlements_file" ]]; then
  echo "Missing entitlements file: $entitlements_file" >&2
  exit 2
fi

# Sign nested Mach-O images from the inside out, then the bundle. `--deep` is
# deprecated for distribution and only signs nested code with the top-level
# options; the hardened runtime needs entitlements on the real executables
# (the universal launcher, both payload apphosts, and the MCP server).
sign_args=(--force)
if [[ "$signing_identity" != "-" ]]; then
  sign_args+=(--options runtime --timestamp)
fi
while IFS= read -r -d '' binary; do
  [[ "$binary" == "$app_macos/Kkindle" ]] && continue
  # Original notices and corresponding source are data resources. The outer
  # bundle signature seals them; signing them individually would add needless
  # signature metadata to the originals and source archives.
  case "$binary" in
    */ThirdParty/*|*/CorrespondingSource/*|*/LICENSE|*/THIRD-PARTY-NOTICES.md|*/Assets/Fonts/*-LICENSE.txt|*/Assets/Icons/*-LICENSE.txt) continue ;;
  esac
  file_type="$(file -b "$binary")"
  if [[ "$file_type" == *Mach-O* ]]; then
    # Native code needs the hardened runtime (for Developer ID builds), a
    # secure timestamp, and entitlements when it is an executable.
    binary_args=("${sign_args[@]}" --sign "$signing_identity")
    if [[ "$file_type" == *executable* ]]; then
      binary_args+=(--entitlements "$entitlements_file")
    fi
  else
    # Managed assemblies, PDBs and other payload files are generic code
    # objects for `codesign --deep`. Signing them keeps deep verification and
    # notarization checks consistent with the previous --deep behavior, but
    # they do not need the hardened runtime or a timestamp round-trip.
    binary_args=(--force --sign "$signing_identity")
  fi
  codesign "${binary_args[@]}" "$binary"
done < <(find "$app_root" -type f -print0)

codesign "${sign_args[@]}" \
  --entitlements "$entitlements_file" \
  --sign "$signing_identity" "$app_root"
codesign --verify --deep --strict "$app_root"
plutil -lint "$app_root/Contents/Info.plist" >/dev/null
dotnet run --file "$repo_root/scripts/verify-third-party.cs" -- --mtp-source "$mtp_source_bundle"

# Build a drag-to-Applications disk image. The Applications entry is a
# filesystem symlink, which is what makes Finder copy the bundle on drop; the
# Finder pass only adds the background image and icon positions. Styling is
# best effort: a CI session without Finder automation permission still gets a
# fully functional (if plain) DMG.
create_dmg() {
  local dmg_path="$1"
  local staging="$work_root/dmg-staging"
  local rw_image="$work_root/Kkindle-rw.dmg"
  local mountpoint volume_name attach_output background
  background="$repo_root/scripts/macos/dmg-background.png"
  rm -rf "$staging" "$rw_image" "$dmg_path"
  mkdir -p "$staging"
  cp -a "$app_root" "$staging/Kkindle.app"
  ln -s /Applications "$staging/Applications"
  if [[ -f "$background" ]]; then
    mkdir -p "$staging/.background"
    cp "$background" "$staging/.background/background.png"
  fi

  hdiutil create -quiet -volname "Kkindle" -srcfolder "$staging" \
    -fs HFS+ -format UDRW -ov "$rw_image"
  # Finder addresses the volume by its displayed (mount point) name. Mounting
  # at a custom directory makes Finder show that directory name instead of
  # the volume label, so let hdiutil pick /Volumes/Kkindle and pass the actual
  # name to AppleScript. Finder also needs the volume to be browsable, so no
  # -nobrowse here.
  attach_output="$(hdiutil attach -noautoopen "$rw_image")"
  mountpoint="$(printf '%s\n' "$attach_output" |
    awk -F '\t' '/\/Volumes\// { print $NF; exit }')"
  if [[ -z "$mountpoint" ]]; then
    echo "Unable to mount the staged DMG at /Volumes." >&2
    return 1
  fi
  volume_name="$(basename "$mountpoint")"
  sleep 1

  if [[ -f "$background" && "${KKINDLE_DMG_STYLE:-1}" != "0" ]]; then
    osascript - "$volume_name" <<'APPLESCRIPT' &
on run argv
tell application "Finder"
  tell disk (item 1 of argv)
    open
    delay 1
    set current view of container window to icon view
    set toolbar visible of container window to false
    set statusbar visible of container window to false
    set the bounds of container window to {100, 100, 760, 540}
    set viewOptions to the icon view options of container window
    set arrangement of viewOptions to not arranged
    set icon size of viewOptions to 128
    try
      set background picture of viewOptions to file ".background:background.png"
    end try
    set position of item "Kkindle.app" of container window to {180, 210}
    set position of item "Applications" of container window to {480, 210}
    update without registering applications
    delay 1
    close
  end tell
end tell
end run
APPLESCRIPT
    local style_pid=$!
    local waited=0
    while kill -0 "$style_pid" 2>/dev/null && (( waited < 60 )); do
      sleep 0.5
      waited=$((waited + 1))
    done
    if kill -0 "$style_pid" 2>/dev/null; then
      kill "$style_pid" 2>/dev/null || true
      echo "Finder DMG layout timed out; keeping the default layout." >&2
    else
      wait "$style_pid" || echo "Finder DMG layout failed; keeping the default layout." >&2
    fi
  fi

  # Finder creates .fseventsd while the read-write image is mounted.
  rm -rf "$mountpoint/.fseventsd" 2>/dev/null || true
  sync
  if ! hdiutil detach -quiet "$mountpoint"; then
    hdiutil detach -force -quiet "$mountpoint" || true
  fi
  hdiutil convert -quiet "$rw_image" -format UDZO \
    -imagekey zlib-level=9 -o "$dmg_path"
  rm -f "$rw_image"
}

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

dmg_path="$output_root/Kkindle-$version-$rid.dmg"
create_dmg "$dmg_path"

if [[ "$signing_identity" != "-" ]]; then
  codesign --force --timestamp --sign "$signing_identity" "$dmg_path"
  codesign --verify --strict "$dmg_path"
fi

# A DMG is a separate notarization artifact. Submitting it after the app has
# already been stapled keeps both the copy-in-place bundle and the disk image
# itself valid offline after the user drags Kkindle to Applications.
if [[ -n "$notary_key_path" ]]; then
  xcrun notarytool submit "$dmg_path" \
    --key "$notary_key_path" --key-id "$notary_key_id" --issuer "$notary_issuer" --wait
  xcrun stapler staple "$dmg_path"
  xcrun stapler validate "$dmg_path"
elif [[ -n "$notary_profile" ]]; then
  xcrun notarytool submit "$dmg_path" \
    --keychain-profile "$notary_profile" --wait
  xcrun stapler staple "$dmg_path"
  xcrun stapler validate "$dmg_path"
fi

if [[ "$signing_identity" == "-" ]]; then
  cp "$repo_root/docs/macos-adhoc-install.md" "$work_root/MACOS-README.md"
  tar -C "$work_root" -czf "$output_root/Kkindle-$version-$rid.tar.gz" \
    Kkindle.app MACOS-README.md
else
  tar -C "$work_root" -czf "$output_root/Kkindle-$version-$rid.tar.gz" Kkindle.app
fi

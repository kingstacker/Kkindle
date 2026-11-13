#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
if [[ -x "$repo_root/.tools/dotnet/dotnet" ]]; then
  export DOTNET_ROOT="$repo_root/.tools/dotnet"
  export PATH="$DOTNET_ROOT:$PATH"
fi
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$repo_root/.dotnet-home}"
export NUGET_PACKAGES="${NUGET_PACKAGES:-$repo_root/.nuget/packages}"
source "$repo_root/scripts/dotnet-sdk.sh"
require_repo_dotnet_sdk "$repo_root"

case "$(uname -m)" in
  arm64) rid=osx-arm64 ;;
  x86_64) rid=osx-x64 ;;
  *) echo "Unsupported macOS architecture" >&2; exit 2 ;;
esac

app_root="$repo_root/artifacts/Kkindle.app"
publish_root="$repo_root/artifacts/macos-debug-publish"
if pgrep -f "$app_root/Contents/MacOS/Kkindle" >/dev/null; then
  echo "Quit Kkindle before rebuilding its app bundle." >&2
  exit 2
fi

dotnet publish src/Kkindle.Desktop.MacOS/Kkindle.Desktop.MacOS.csproj \
  -c Debug -r "$rid" --self-contained true -p:PublishSingleFile=false -o "$publish_root"

mkdir -p "$app_root/Contents/Resources"
# Replace the symlink used by older local development bundles. Never follow
# it when copying the self-contained output back into the app.
if [[ -L "$app_root/Contents/MacOS" ]]; then
  unlink "$app_root/Contents/MacOS"
fi
mkdir -p "$app_root/Contents/MacOS"
cp -a "$publish_root/." "$app_root/Contents/MacOS/"
cp LICENSE "$app_root/Contents/Resources/LICENSE"
bash scripts/macos-app-icon.sh "$app_root/Contents/Resources/Kkindle.icns"

short_version="$(sed -nE 's/.*<Version>([0-9]+\.[0-9]+\.[0-9]+).*/\1/p' Directory.Build.props)"
cat > "$app_root/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleDisplayName</key><string>Kkindle</string>
  <key>CFBundleExecutable</key><string>Kkindle</string>
  <key>CFBundleIdentifier</key><string>io.github.kingstacker.kkindle</string>
  <key>CFBundleName</key><string>Kkindle</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleIconFile</key><string>Kkindle.icns</string>
  <key>CFBundleShortVersionString</key><string>$short_version</string>
  <key>CFBundleVersion</key><string>$short_version</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict></plist>
PLIST
codesign --force --deep --sign - "$app_root"
codesign --verify --deep --strict "$app_root"
echo "Built: $app_root"

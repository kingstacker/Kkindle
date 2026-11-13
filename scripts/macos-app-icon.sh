#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="${1:?usage: macos-app-icon.sh OUTPUT.icns}"
work_root="$(mktemp -d)"
trap 'rm -rf "$work_root"' EXIT
iconset="$work_root/Kkindle.iconset"
mkdir -p "$iconset"
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" "$repo_root/src/Kkindle.App/Assets/Icons/kkindle.png" \
    --out "$iconset/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z "$double" "$double" "$repo_root/src/Kkindle.App/Assets/Icons/kkindle.png" \
    --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$output"

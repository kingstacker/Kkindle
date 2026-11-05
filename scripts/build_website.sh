#!/usr/bin/env bash
# Regenerate the previewable source site and a deployable artifact.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

output="${1:-artifacts/website}"

python3 scripts/generate_website_manual.py
python3 scripts/refresh_website_release.py --allow-stale
python3 scripts/generate_website_changelog.py --in-place
python3 scripts/generate_website_changelog.py --output-dir "$output"

echo
echo "Source site updated: website/"
echo "Deployable artifact: $output/"
echo "Preview: python3 -m http.server -d $output 8765"

#!/usr/bin/env bash
# Packages a plugin folder into a .kkplugin archive (a plain zip).
#
#   scripts/package-plugin.sh plugins/book-translation
#   scripts/package-plugin.sh plugins/book-translation dist/
#
# The manifest must sit at the archive root, so zipping happens inside the
# plugin folder and excludes everything that is not part of the plugin.
set -euo pipefail

plugin_dir="${1:-}"
output_dir="${2:-.}"

if [[ -z "$plugin_dir" || ! -f "$plugin_dir/manifest.json" ]]; then
  echo "usage: $0 <plugin-dir with manifest.json> [output-dir]" >&2
  exit 2
fi

plugin_dir="${plugin_dir%/}"
name="$(basename "$plugin_dir")"
mkdir -p "$output_dir"
output="$(cd "$output_dir" && pwd)/${name}.kkplugin"
rm -f "$output"

cd "$plugin_dir"
zip -q -r -X "$output" . \
  -x '*.DS_Store' '*/.DS_Store' '.git/*' 'node_modules/*' '*.kkplugin' 'dist/*'

echo "packaged: $output"

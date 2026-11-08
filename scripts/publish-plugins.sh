#!/usr/bin/env bash
# Packs, signs and indexes the official plugins into website/plugins/, which
# build_website.sh deploys as https://kkindle.stacker.beauty/plugins/.
#
#   scripts/publish-plugins.sh                 # every folder under plugins/
#   scripts/publish-plugins.sh book-translation
#
# Signing key: $KKINDLE_PLUGIN_SIGNING_KEY (a PEM file path), defaulting to
# ~/.config/kkindle/plugin-signing/official-p256.pem. Its public half is
# PluginSignature.OfficialKeys["Kkindle"]; never commit the private key.
# In CI, write the secret to a temp file and point the variable at it.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

key="${KKINDLE_PLUGIN_SIGNING_KEY:-$HOME/.config/kkindle/plugin-signing/official-p256.pem}"
if [[ ! -f "$key" ]]; then
  echo "signing key not found: $key (set KKINDLE_PLUGIN_SIGNING_KEY)" >&2
  exit 1
fi

out="website/plugins"
mkdir -p "$out"
dotnet build tools/Kkindle.PluginTool -c Release -nologo -v q >/dev/null
tool=(dotnet tools/Kkindle.PluginTool/bin/Release/net10.0/kkplugin.dll)

if [[ $# -gt 0 ]]; then names=("$@"); else names=($(ls plugins)); fi
for name in "${names[@]}"; do
  [[ -f "plugins/$name/manifest.json" ]] || continue
  "${tool[@]}" pack "plugins/$name" -o "$out" --key "$key" --publisher Kkindle
done

"${tool[@]}" index "$out" --base-url "https://kkindle.stacker.beauty/plugins/" -o "$out/index.json"

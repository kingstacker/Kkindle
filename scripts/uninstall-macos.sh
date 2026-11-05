#!/usr/bin/env bash
# Removes Kkindle.app and the data Kkindle stores outside the bundle.
#
# Usage:
#   uninstall-macos.sh [--keep-data] [/Applications/Kkindle.app]
#
# macOS has no uninstaller for drag-to-trash applications, so the library,
# settings, backups, caches, saved state and the Keychain key would otherwise
# stay behind forever. Keep the data when you plan to reinstall later.
set -euo pipefail

keep_data=0
if [[ "${1:-}" == "--keep-data" ]]; then
  keep_data=1
  shift
fi

app="${1:-/Applications/Kkindle.app}"
case "$app" in
  */Kkindle.app) ;;
  *)
    echo "Refusing to remove an unexpected path: $app" >&2
    exit 2
    ;;
esac

data_root="$HOME/Library/Application Support/Kkindle"
bundle_id="io.github.kingstacker.kkindle"

# Ask the app to quit so it flushes the database and S3 state. The window
# close button only hides the app, so pkill is the fallback.
if pgrep -x Kkindle >/dev/null 2>&1; then
  /usr/bin/osascript -e 'tell application "Kkindle" to quit' >/dev/null 2>&1 || true
  for _ in $(seq 1 20); do
    pgrep -x Kkindle >/dev/null 2>&1 || break
    sleep 0.25
  done
  pkill -x Kkindle >/dev/null 2>&1 || true
fi

rm -rf "$app"
echo "Removed $app"

if (( keep_data )); then
  echo "Kept user data in $data_root"
  exit 0
fi

rm -rf "$data_root"
rm -f "$HOME/Library/Preferences/$bundle_id.plist"
rm -rf "$HOME/Library/Saved Application State/$bundle_id.savedState"
rm -rf "$HOME/Library/Caches/$bundle_id"
rm -rf "$HOME/Library/Logs/Kkindle"
rm -rf "$HOME/Library/Logs/DiagnosticReports/Kkindle"*

# The AES key for stored passwords lives in the login Keychain. Remove every
# account entry for the service; ignore "not found".
for _ in $(seq 1 20); do
  if ! /usr/bin/security delete-generic-password -s "Kkindle.SecretProtectionKey" >/dev/null 2>&1; then
    break
  fi
done

echo "Removed Kkindle user data and Keychain entries."

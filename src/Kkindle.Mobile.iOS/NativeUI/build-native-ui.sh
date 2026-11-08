#!/bin/zsh
set -euo pipefail

# Compile Swift into the existing .NET iOS executable. No second data store or
# network server is needed for the native frontend.
native_rid="$1"
native_output="$2"
native_configuration="$3"
native_source_dir="${0:A:h}"

case "$native_rid" in
  ios-arm64) native_sdk=iphoneos; native_target=arm64-apple-ios15.0 ;;
  iossimulator-arm64) native_sdk=iphonesimulator; native_target=arm64-apple-ios15.0-simulator ;;
  iossimulator-x64) native_sdk=iphonesimulator; native_target=x86_64-apple-ios15.0-simulator ;;
  *) print -u2 "Unsupported Swift frontend runtime: $native_rid"; exit 1 ;;
esac

mkdir -p "$native_output"
native_sdk_path="$(xcrun --sdk "$native_sdk" --show-sdk-path)"
native_flags=(-O)
if [[ "$native_configuration" == Debug ]]; then
  native_flags=(-Onone -g)
fi

xcrun --sdk "$native_sdk" swiftc \
  -sdk "$native_sdk_path" -target "$native_target" -swift-version 5 \
  -parse-as-library -emit-library -static -module-name KkindleNativeUI \
  -module-cache-path "$native_output/module-cache" \
  "${native_flags[@]}" "$native_source_dir"/*.swift \
  -o "$native_output/libKkindleNativeUI.a"

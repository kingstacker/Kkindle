#!/usr/bin/env bash
# Build the native MTP stack used by the macOS head.
#
# macOS ships no MTP implementation, so Kkindle bundles libmtp (the same
# library calibre uses on macOS) and its libusb dependency. Both are LGPL-2.1
# and are linked as separate dynamic libraries, which keeps them replaceable.
#
# Usage: build-macos-mtp-deps.sh [OUTPUT_DIR]
# The result is OUTPUT_DIR/prefix/lib/{libmtp.9.dylib,libusb-1.0.0.dylib}.
set -euo pipefail

deps_root="${1:-artifacts/macos-deps}"
libusb_version="1.0.27"
libmtp_version="1.1.21"

deps_root="$(mkdir -p "$deps_root" && cd "$deps_root" && pwd)"
prefix="$deps_root/prefix"
libdir="$prefix/lib"

if [[ -f "$libdir/libmtp.9.dylib" && -f "$libdir/libusb-1.0.0.dylib" ]]; then
  echo "macOS MTP dependencies already built in $libdir"
  exit 0
fi

work_root="$(mktemp -d)"
trap 'rm -rf "$work_root"' EXIT

echo "Downloading libusb $libusb_version and libmtp $libmtp_version"
curl -fsSL -o "$work_root/libusb.tar.bz2" \
  "https://github.com/libusb/libusb/releases/download/v$libusb_version/libusb-$libusb_version.tar.bz2"
curl -fsSL -o "$work_root/libmtp.tar.gz" \
  "https://downloads.sourceforge.net/project/libmtp/libmtp/$libmtp_version/libmtp-$libmtp_version.tar.gz"

export MACOSX_DEPLOYMENT_TARGET="${MACOSX_DEPLOYMENT_TARGET:-12.0}"

echo "Building libusb"
tar -C "$work_root" -xjf "$work_root/libusb.tar.bz2"
(
  cd "$work_root/libusb-$libusb_version"
  ./configure --prefix="$prefix" --disable-static --enable-shared
  make -j"$(sysctl -n hw.ncpu)"
  make install
)

echo "Building libmtp"
tar -C "$work_root" -xzf "$work_root/libmtp.tar.gz"
(
  cd "$work_root/libmtp-$libmtp_version"
  # pkg-config is not required on a stock macOS install; point the configure
  # checks straight at the libusb we just built.
  LIBUSB_CFLAGS="-I$prefix/include/libusb-1.0" \
  LIBUSB_LIBS="-L$libdir -lusb-1.0" \
    ./configure --prefix="$prefix" --disable-static --enable-shared
  make -j"$(sysctl -n hw.ncpu)"
  make install
)

mkdir -p "$prefix/share/licenses"
cp "$work_root/libusb-$libusb_version/COPYING" "$prefix/share/licenses/libusb-COPYING"
cp "$work_root/libmtp-$libmtp_version/COPYING" "$prefix/share/licenses/libmtp-COPYING"

echo "Fixing install names for app bundle loading"
install_name_tool -id "@loader_path/libusb-1.0.0.dylib" "$libdir/libusb-1.0.0.dylib"
install_name_tool -id "@loader_path/libmtp.9.dylib" "$libdir/libmtp.9.dylib"
install_name_tool -change "$libdir/libusb-1.0.0.dylib" "@loader_path/libusb-1.0.0.dylib" \
  "$libdir/libmtp.9.dylib"

echo "Built macOS MTP dependencies:"
otool -L "$libdir/libmtp.9.dylib" | sed -n '2,5p'

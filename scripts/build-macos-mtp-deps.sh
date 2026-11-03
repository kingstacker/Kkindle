#!/usr/bin/env bash
# Build the native MTP stack used by the macOS head.
#
# macOS ships no MTP implementation, so Kkindle bundles libmtp (the same
# library calibre uses on macOS) and its libusb dependency. Both are LGPL-2.1
# and are linked as separate dynamic libraries, which keeps them replaceable.
#
# Usage: build-macos-mtp-deps.sh [OUTPUT_DIR] [ARCH]
# ARCH is arm64 or x86_64 and defaults to the host architecture. When ARCH
# differs from the host, the libraries are cross-compiled with clang against
# the universal macOS SDK, so an Intel Mac can still produce the arm64
# libraries an osx-arm64 package needs (and vice versa).
# The result is OUTPUT_DIR/prefix/lib/{libmtp.9.dylib,libusb-1.0.0.dylib}.
set -euo pipefail

libusb_version="1.0.27"
libmtp_version="1.1.21"

arch="${2:-$(uname -m)}"
case "$arch" in
  arm64|x86_64) ;;
  *) echo "Unsupported architecture: $arch" >&2; exit 2 ;;
esac

deps_root="${1:-artifacts/macos-deps/$arch}"
deps_root="$(mkdir -p "$deps_root" && cd "$deps_root" && pwd)"
prefix="$deps_root/prefix"
libdir="$prefix/lib"

dylib_arch() {
  lipo -archs "$1" 2>/dev/null | grep -qw "$arch"
}

if [[ -f "$libdir/libmtp.9.dylib" && -f "$libdir/libusb-1.0.0.dylib" ]] \
  && dylib_arch "$libdir/libmtp.9.dylib" \
  && dylib_arch "$libdir/libusb-1.0.0.dylib"; then
  echo "macOS MTP dependencies for $arch already built in $libdir"
  exit 0
fi
rm -rf "$prefix"

work_root="$(mktemp -d)"
trap 'rm -rf "$work_root"' EXIT

echo "Downloading libusb $libusb_version and libmtp $libmtp_version"
curl -fsSL --retry 3 --retry-delay 5 -o "$work_root/libusb.tar.bz2" \
  "https://github.com/libusb/libusb/releases/download/v$libusb_version/libusb-$libusb_version.tar.bz2" \
  || curl -fsSL --retry 3 --retry-delay 5 -o "$work_root/libusb.tar.bz2" \
  "https://downloads.sourceforge.net/project/libusb/libusb-1.0/libusb-$libusb_version/libusb-$libusb_version.tar.bz2"
curl -fsSL --retry 3 --retry-delay 5 -o "$work_root/libmtp.tar.gz" \
  "https://downloads.sourceforge.net/project/libmtp/libmtp/$libmtp_version/libmtp-$libmtp_version.tar.gz"

export MACOSX_DEPLOYMENT_TARGET="${MACOSX_DEPLOYMENT_TARGET:-12.0}"

host_arch="$(uname -m)"
configure_args=(--prefix="$prefix" --disable-static --enable-shared)
if [[ "$arch" != "$host_arch" ]]; then
  # The macOS SDK carries slices for both architectures, so the host compiler
  # can target the other one. Cross mode keeps configure from running test
  # binaries it cannot execute.
  echo "Cross-compiling $arch libraries on $host_arch"
  export CC="clang -arch $arch"
  export CXX="clang++ -arch $arch"
  # libmtp ships an older config.sub that knows aarch64 but not arm64.
  target_triple=aarch64-apple-darwin
  [[ "$arch" == "x86_64" ]] && target_triple=x86_64-apple-darwin
  configure_args+=(--host="$target_triple" --build="$host_arch-apple-darwin")
fi

echo "Building libusb"
tar -C "$work_root" -xjf "$work_root/libusb.tar.bz2"
(
  cd "$work_root/libusb-$libusb_version"
  ./configure "${configure_args[@]}"
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
    ./configure "${configure_args[@]}"
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

echo "Built macOS MTP dependencies for $arch:"
lipo -archs "$libdir/libmtp.9.dylib"
otool -L "$libdir/libmtp.9.dylib" | sed -n '2,5p'

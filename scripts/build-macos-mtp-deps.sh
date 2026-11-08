#!/usr/bin/env bash
# Build replaceable LGPL MTP dylibs and retain their exact corresponding source.
# Usage: build-macos-mtp-deps.sh [OUTPUT_DIR] [arm64|x86_64] [SOURCE_DIR]
# SOURCE_DIR supplies the original archives locally, without any downloads.
set -euo pipefail

libusb_version="1.0.27"
libmtp_version="1.1.21"
libusb_sha256="ffaa41d741a8a3bee244ac8e54a72ea05bf2879663c098c82fc5757853441575"
libmtp_sha256="c4ffa5ab8c8f48c91b0047f2e253c101c418d5696a5ed65c839922a4280872a7"

arch="${2:-$(uname -m)}"
case "$arch" in
  arm64|x86_64) ;;
  *) echo "Unsupported architecture: $arch" >&2; exit 2 ;;
esac
if [[ "$(uname -s)" != Darwin ]]; then
  echo "This builder requires the macOS SDK and native macOS tools." >&2
  exit 2
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
script_file="$script_dir/$(basename "${BASH_SOURCE[0]}")"
readme_file="$script_dir/../docs/macos-mtp-corresponding-source.md"
[[ -f "$readme_file" ]] || readme_file="$script_dir/README.md"
if [[ ! -f "$readme_file" ]]; then
  echo "Missing corresponding-source rebuilding/replacement instructions." >&2
  exit 2
fi

deps_root="${1:-artifacts/macos-deps/$arch}"
deps_root="$(mkdir -p "$deps_root" && cd "$deps_root" && pwd)"
prefix="$deps_root/prefix"
libdir="$prefix/lib"
archive_cache="$deps_root/downloads"
source_pack="$prefix/share/kkindle/corresponding-source/MTP"
mkdir -p "$archive_cache"

checksum() { shasum -a 256 "$1" | awk '{print $1}'; }
require_checksum() {
  if [[ ! -f "$1" || "$(checksum "$1")" != "$2" ]]; then
    echo "Missing or mismatched source archive: $(basename "$1")" >&2
    exit 2
  fi
}

source_dir="${3:-}"
if [[ -n "$source_dir" ]]; then
  source_dir="$(cd "$source_dir" && pwd)"
  # Preserve inputs before invalidating prefix, even when SOURCE_DIR is the
  # corresponding-source directory from an earlier build of this same prefix.
  for archive in "libusb-$libusb_version.tar.bz2" "libmtp-$libmtp_version.tar.gz"; do
    if [[ "$source_dir/$archive" != "$archive_cache/$archive" ]]; then
      cp "$source_dir/$archive" "$archive_cache/$archive"
    fi
  done
fi

download_archive() {
  local archive="$1" expected="$2"
  shift 2
  if [[ -f "$archive_cache/$archive" ]]; then
    require_checksum "$archive_cache/$archive" "$expected"
    return
  fi
  if [[ -n "$source_dir" ]]; then
    echo "Supplied source directory does not contain $archive" >&2
    exit 2
  fi
  local url
  for url in "$@"; do
    if curl -fsSL --connect-timeout 15 --max-time 120 --retry 2 --retry-delay 2 \
      -o "$archive_cache/$archive.partial" "$url"; then
      require_checksum "$archive_cache/$archive.partial" "$expected"
      mv "$archive_cache/$archive.partial" "$archive_cache/$archive"
      return
    fi
  done
  rm -f "$archive_cache/$archive.partial"
  echo "Unable to obtain original source archive: $archive" >&2
  exit 2
}

download_archive "libusb-$libusb_version.tar.bz2" "$libusb_sha256" \
  "https://github.com/libusb/libusb/releases/download/v$libusb_version/libusb-$libusb_version.tar.bz2" \
  "https://downloads.sourceforge.net/project/libusb.mirror/v$libusb_version/libusb-$libusb_version.tar.bz2" \
  "https://downloads.sourceforge.net/project/libusb/libusb-1.0/libusb-$libusb_version/libusb-$libusb_version.tar.bz2"
download_archive "libmtp-$libmtp_version.tar.gz" "$libmtp_sha256" \
  "https://downloads.sourceforge.net/project/libmtp/libmtp/$libmtp_version/libmtp-$libmtp_version.tar.gz"

export MACOSX_DEPLOYMENT_TARGET="${MACOSX_DEPLOYMENT_TARGET:-12.0}"
export CC="clang -arch $arch"
export CXX="clang++ -arch $arch"
host_arch="$(uname -m)"
compiler_version="$(clang --version | head -n 1)"
sdk_version="$(xcrun --sdk macosx --show-sdk-version)"
script_hash="$(checksum "$script_file")"
readme_hash="$(checksum "$readme_file")"
build_stamp="$(printf '%s\n' "$arch" "$host_arch" "$libusb_sha256" "$libmtp_sha256" \
  "$script_hash" "$readme_hash" "$MACOSX_DEPLOYMENT_TARGET" "$compiler_version" "$sdk_version" \
  | shasum -a 256 | awk '{print $1}')"

dylib_arch() { lipo -archs "$1" 2>/dev/null | grep -qw "$arch"; }
cache_valid() {
  [[ -f "$deps_root/build-stamp.txt" && "$(cat "$deps_root/build-stamp.txt")" == "$build_stamp" ]] || return 1
  local file
  for file in "libusb-$libusb_version.tar.bz2" "libmtp-$libmtp_version.tar.gz" \
    libusb-COPYING libmtp-COPYING build-macos-mtp-deps.sh README.md \
    "BUILD-METADATA-$arch.txt" SHA256SUMS.txt; do
    [[ -f "$source_pack/$file" ]] || return 1
  done
  (cd "$source_pack" && shasum -a 256 -c SHA256SUMS.txt >/dev/null 2>&1) || return 1
  [[ "$(checksum "$source_pack/build-macos-mtp-deps.sh")" == "$script_hash" ]] || return 1
  for file in libusb-1.0.0.dylib libmtp.9.dylib; do
    [[ -f "$libdir/$file" ]] && dylib_arch "$libdir/$file" || return 1
    local expected
    expected="$(sed -n "s/^$file.sha256=//p" "$source_pack/BUILD-METADATA-$arch.txt")"
    [[ "$(checksum "$libdir/$file")" == "$expected" ]] || return 1
  done
}

if cache_valid; then
  echo "macOS MTP dependencies and matching source materials for $arch already built in $libdir"
  exit 0
fi
# Legacy/unrecorded caches cannot establish which source produced their dylibs.
work_root="$(mktemp -d)"
trap 'rm -rf "$work_root"' EXIT
# A user may run the shipped script from inside an old prefix. Preserve the
# exact script/instructions before removing that prefix for a rebuild.
cp "$script_file" "$work_root/build-macos-mtp-deps.sh"
cp "$readme_file" "$work_root/README.md"
rm -rf "$prefix"

configure_args=(--prefix="$prefix" --disable-static --enable-shared)
target_triple="$host_arch-apple-darwin"
if [[ "$arch" != "$host_arch" ]]; then
  echo "Cross-compiling $arch libraries on $host_arch"
  target_triple=aarch64-apple-darwin
  [[ "$arch" == "x86_64" ]] && target_triple=x86_64-apple-darwin
  # The original config.sub accepts aarch64; no upstream source patch is needed.
  configure_args+=(--host="$target_triple" --build="$host_arch-apple-darwin")
fi

echo "Building libusb $libusb_version for $arch"
tar -C "$work_root" -xjf "$archive_cache/libusb-$libusb_version.tar.bz2"
(
  cd "$work_root/libusb-$libusb_version"
  ./configure "${configure_args[@]}"
  make -j"$(sysctl -n hw.ncpu)"
  make install
)

echo "Building libmtp $libmtp_version for $arch"
tar -C "$work_root" -xzf "$archive_cache/libmtp-$libmtp_version.tar.gz"
(
  cd "$work_root/libmtp-$libmtp_version"
  LIBUSB_CFLAGS="-I$prefix/include/libusb-1.0" \
  LIBUSB_LIBS="-L$libdir -lusb-1.0" ./configure "${configure_args[@]}"
  make -j"$(sysctl -n hw.ncpu)"
  make install
)

mkdir -p "$prefix/share/licenses" "$source_pack"
cp "$work_root/libusb-$libusb_version/COPYING" "$prefix/share/licenses/libusb-COPYING"
cp "$work_root/libmtp-$libmtp_version/COPYING" "$prefix/share/licenses/libmtp-COPYING"

echo "Fixing install names for app bundle loading"
install_name_tool -id "@loader_path/libusb-1.0.0.dylib" "$libdir/libusb-1.0.0.dylib"
install_name_tool -id "@loader_path/libmtp.9.dylib" "$libdir/libmtp.9.dylib"
install_name_tool -change "$libdir/libusb-1.0.0.dylib" "@loader_path/libusb-1.0.0.dylib" \
  "$libdir/libmtp.9.dylib"

cp "$archive_cache/libusb-$libusb_version.tar.bz2" "$archive_cache/libmtp-$libmtp_version.tar.gz" "$source_pack/"
cp "$prefix/share/licenses/libusb-COPYING" "$prefix/share/licenses/libmtp-COPYING" "$source_pack/"
cp "$work_root/build-macos-mtp-deps.sh" "$source_pack/build-macos-mtp-deps.sh"
cp "$work_root/README.md" "$source_pack/README.md"
cat > "$source_pack/BUILD-METADATA-$arch.txt" <<EOF
architecture=$arch
build_architecture=$host_arch
libusb.version=$libusb_version
libmtp.version=$libmtp_version
libusb.archive.sha256=$libusb_sha256
libmtp.archive.sha256=$libmtp_sha256
build_script.sha256=$script_hash
libusb-1.0.0.dylib.sha256=$(checksum "$libdir/libusb-1.0.0.dylib")
libmtp.9.dylib.sha256=$(checksum "$libdir/libmtp.9.dylib")
compiler=$compiler_version
sdk.version=$sdk_version
deployment_target=$MACOSX_DEPLOYMENT_TARGET
CC=clang -arch $arch
CXX=clang++ -arch $arch
configure=--prefix=<OUTPUT_DIR>/prefix --disable-static --enable-shared
configure.target=$target_triple
configure.build=$host_arch-apple-darwin
libmtp.LIBUSB_CFLAGS=-I<OUTPUT_DIR>/prefix/include/libusb-1.0
libmtp.LIBUSB_LIBS=-L<OUTPUT_DIR>/prefix/lib -lusb-1.0
source_patches=none
binary_changes=install_name_tool IDs and libmtp dependency use @loader_path
EOF
(
  cd "$source_pack"
  shasum -a 256 "libusb-$libusb_version.tar.bz2" "libmtp-$libmtp_version.tar.gz" \
    libusb-COPYING libmtp-COPYING build-macos-mtp-deps.sh README.md "BUILD-METADATA-$arch.txt" > SHA256SUMS.txt
  shasum -a 256 -c SHA256SUMS.txt
)
printf '%s\n' "$build_stamp" > "$deps_root/build-stamp.txt"
echo "Built macOS MTP dependencies and corresponding source for $arch:"
lipo -archs "$libdir/libmtp.9.dylib"
otool -L "$libdir/libmtp.9.dylib" | sed -n '2,5p'

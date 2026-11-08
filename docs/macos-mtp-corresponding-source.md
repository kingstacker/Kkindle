# macOS libmtp / libusb corresponding source

Kkindle includes libmtp 1.1.21 and libusb 1.0.27 as separate dynamic libraries.
These libraries retain their LGPL licenses, copyrights and notices; see
`libmtp-COPYING`, `libusb-COPYING` and the individual files in the source archives.
The LGPL permits modification and replacement of these libraries. Reverse
engineering for debugging modifications to these libraries is permitted.

Every macOS distribution made by `scripts/build-macos-release.sh` includes this
directory at `Kkindle.app/Contents/Resources/CorrespondingSource/MTP/`:

- `libmtp-1.1.21.tar.gz` and `libusb-1.0.27.tar.bz2`: the complete, unmodified
  upstream release archives actually used to build the bundled libraries.
- `libmtp-COPYING` and `libusb-COPYING`: original license texts from those archives.
- `build-macos-mtp-deps.sh`: the exact build script used by Kkindle.
- `BUILD-METADATA-<architecture>.txt`: versions, source and unsigned-library
  checksums, compiler/SDK version, deployment target and build configuration.
  Universal packages contain metadata for both `x86_64` and `arm64`.
- `SHA256SUMS.txt`: checksums for all these source, license and build materials.
- This `README.md` with rebuilding and replacement instructions.

The source archives are not patched by the Kkindle build. Configure receives
`--host=aarch64-apple-darwin` for arm64 cross builds, instead of changing
`config.sub`. After compilation, `install_name_tool` changes the dylib IDs and
the libmtp-to-libusb dependency to `@loader_path/...`. The shipped script records
and reproduces those changes. App signing can change the dylib bytes after the
unsigned-library checksums are recorded; it does not change these source files.

## Rebuild from the supplied archives

Use macOS with Xcode Command Line Tools (clang, make, SDK, lipo,
install_name_tool), Bash, curl, tar and shasum. The exact toolchain version used
for the distributed build is recorded in its metadata. A Developer ID, private
signing key, notarization credentials and a network connection are not required
to rebuild using the supplied archives.

Copy this directory to a writable location and run:

```bash
cd /path/to/copied/MTP
shasum -a 256 -c SHA256SUMS.txt
bash ./build-macos-mtp-deps.sh ./rebuilt x86_64 .
# On Apple Silicon, or to cross-compile an arm64 replacement:
bash ./build-macos-mtp-deps.sh ./rebuilt-arm64 arm64 .
```

The third argument supplies the archives locally and disables downloading.
The result is `rebuilt/prefix/lib/libmtp.9.dylib` and
`rebuilt/prefix/lib/libusb-1.0.0.dylib`. Use the deployment target recorded in the
metadata if it differs from the script's default, for example:

```bash
MACOSX_DEPLOYMENT_TARGET=12.0 bash ./build-macos-mtp-deps.sh ./rebuilt x86_64 .
```

To make your own changes, unpack the supplied source archives, modify the
library sources, and use the configure/make/install commands and install-name
changes in the supplied script. You can also adapt the script and its archive
checksums to your own source archives. Keep the libraries' compatible ABI and
their `libmtp.9.dylib` / `libusb-1.0.0.dylib` names. The checksums supplied by
Kkindle identify its original build inputs; they do not prohibit modified builds.

## Replace the libraries in your local app copy

Quit Kkindle and make a writable copy of the application. A single-architecture
package loads both dylibs from `Kkindle.app/Contents/MacOS/`. A universal package
loads them from `Contents/MacOS/x86_64/` or `Contents/MacOS/arm64/`, according to
the launched payload. Replace both libraries in the matching directory with
your rebuilt files. The managed resolver uses this directory; no server
authorization or library checksum restriction prevents a compatible replacement.

Changing signed code invalidates Apple's existing application signature. For
local use, remove the old signatures and sign your copy ad hoc. Preserve the
runtime entitlements of the original executables when doing so:

```bash
cp -R /Applications/Kkindle.app ./Kkindle-modified.app
codesign -d --entitlements :- ./Kkindle-modified.app/Contents/MacOS/Kkindle \
  > ./Kkindle-entitlements.plist
# For a universal bundle, obtain the runtime entitlements from the matching
# payload executable, e.g. Contents/MacOS/arm64/Kkindle, instead of the launcher.
cp ./rebuilt/prefix/lib/libmtp.9.dylib \
   ./rebuilt/prefix/lib/libusb-1.0.0.dylib ./Kkindle-modified.app/Contents/MacOS/
codesign --force --sign - ./Kkindle-modified.app/Contents/MacOS/libusb-1.0.0.dylib
codesign --force --sign - ./Kkindle-modified.app/Contents/MacOS/libmtp.9.dylib
codesign --force --deep --sign - --entitlements ./Kkindle-entitlements.plist \
  ./Kkindle-modified.app
codesign --verify --deep --strict ./Kkindle-modified.app
```

Adapt the dylib destinations for a universal bundle. These commands are for a
user's local modified copy; distributing such a copy requires compliance with
the applicable licenses and Apple's distribution/signing rules. The original
publisher's private signing keys are not needed or included.

## Upstream sources and release maintenance

- libusb: https://github.com/libusb/libusb/releases/tag/v1.0.27
- libmtp: https://sourceforge.net/projects/libmtp/files/libmtp/1.1.21/

Maintainers must keep the exact matching source materials with every macOS
package. Updating either library requires updating its version and pinned
archive checksum in the builder, rebuilding, and reviewing the original license
files. Old build caches without complete matching materials are rebuilt. The
release script verifies the source checksums before packaging and leaves these
documents/archives byte-for-byte intact during code signing.

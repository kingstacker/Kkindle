# Linux and macOS

Kkindle uses one portable Avalonia UI with separate Windows, Linux and macOS
desktop heads. Windows retains WPD support for MTP-only Kindle devices. Linux
supports Kindles exposed as mounted USB storage. macOS supports mounted storage
and MTP devices through the bundled libmtp stack.

## User data and secrets

- Linux data: `$XDG_DATA_HOME/Kkindle`, falling back to
  `~/.local/share/Kkindle`; root configuration uses `$XDG_CONFIG_HOME/Kkindle`
  or `~/.config/Kkindle`.
- macOS data and root configuration:
  `~/Library/Application Support/Kkindle`.
- Linux stores its wrapping key in Secret Service using `secret-tool`.
- macOS stores its wrapping key in the login Keychain.
- Windows continues to use DPAPI and its existing data layout.

## macOS feature parity

The macOS head matches the Windows build beyond device access:

- A second launch activates the running instance over a Unix domain socket
  (lock file in `~/Library/Application Support/Kkindle`). A window parked in the
  menu bar is shown again, and files passed to the second launch are imported.
- The native menu bar replaces Avalonia's default "About Avalonia" menu with
  About, Settings (Cmd+,), Import (Cmd+I), Close Window (Cmd+W), window,
  shortcut-help and manual entries; it follows the selected UI language.
- Finder "Open With" works for EPUB, PDF, MOBI/PRC/AZW/AZW3 and TXT. Documents
  opened while Kkindle is already running are queued until the library is
  ready, then imported through the same dialog as a manual import.
- USB attach/detach events arrive through IOKit and refresh the device list.
- Unhandled exceptions are written to `data/logs/crash.log`. Only loose
  Debug executables also write `kkindle-crash.log` next to the binary; a file
  added inside `Kkindle.app` would break the bundle's code signature.
- In-app updates install from the `Kkindle-{version}-osx-{arm64|x64}.tar.gz`
  release asset. A detached helper waits for the app to exit, rejects archive
  members outside `Kkindle.app`, verifies the downloaded bundle's code
  signature, stops the bundled MCP server, replaces `Kkindle.app`, verifies the
  installed signature again, reopens it, and restores the previous bundle if
  anything fails. The option is only offered when the bundle directory is
  writable and the app is not running from App Translocation.
- Send to Kindle streams queued files into the Amazon page as real `File`
  objects through the shared webview bridge, so the platform no longer depends
  on the Windows-only WebView2 file-input API.
- MTP Kindles gain annotation database read/write with local and device-side
  backups plus SHA-256 verification, bookshelf thumbnails, and `.sdr` sidecar
  cleanup. The account annotation database is read before `My Clippings.txt`.
- Custom shortcuts accept Command (`Meta`) as well as Control, and the TTS
  cache uses `~/Library/Caches/Kkindle/tts`.
- The MCP server is published self-contained into `Contents/MacOS/mcp` and uses
  libmtp plus mounted storage for device tools.
- `scripts/uninstall-macos.sh` removes the bundle, library, settings, backups,
  caches and Keychain entry; pass `--keep-data` to remove only the app. See
  [macos-uninstall.md](macos-uninstall.md).

## Build and package

Install the exact .NET SDK version pinned by the repository root `global.json`
before building. Release scripts run from the repository root and refuse other
SDK versions so Linux, macOS and Windows packages are produced by the same SDK.

```sh
dotnet --version # must print 10.0.400
dotnet build src/Kkindle.Desktop.Linux/Kkindle.Desktop.Linux.csproj -c Release
dotnet build src/Kkindle.Desktop.MacOS/Kkindle.Desktop.MacOS.csproj -c Release
bash scripts/build-linux-release.sh 1.1.3 artifacts/linux linux-x64
bash scripts/build-macos-release.sh 1.1.3 artifacts/macos osx-arm64
```

macOS release packaging also requires Xcode 26 or newer to compile the layered
Icon Composer artwork. Select it with `xcode-select` or `DEVELOPER_DIR` when
multiple Xcode versions are installed.

Each macOS build produces both `Kkindle-{version}-{rid}.dmg` and the
`Kkindle-{version}-{rid}.tar.gz` used by the in-app updater. The DMG contains
`Kkindle.app` and an `Applications` symlink, so the standard drag-to-Applications
install works. Its Finder window uses the generated
`scripts/macos/dmg-background.png`; the Finder layout pass is best effort with a
timeout, and builds without Finder automation permission still produce a
functional DMG. Set `KKINDLE_DMG_STYLE=0` to skip the layout pass entirely.

`build-macos-release.sh` builds libmtp and libusb for the package's architecture
into `artifacts/macos-deps/<arch>/`. When the target differs from the build
host, `build-macos-mtp-deps.sh` cross-compiles them with clang against the
universal macOS SDK, so either package can be produced from one Mac. The
Release and Development Build workflows still use native `macos-15`
(`osx-arm64`) and `macos-15-intel` (`osx-x64`) runners to keep CI fast.

Pass `osx-universal` as the RID for one bundle that runs natively on both
architectures. .NET self-contained output embeds RID-specific native libraries
and ReadyToRun assemblies, so a single set of files cannot be lipo-merged;
instead `Contents/MacOS/Kkindle` is a universal launcher and the complete
`osx-x64` and `osx-arm64` payloads live in `Contents/MacOS/x86_64` and
`Contents/MacOS/arm64`. In-app updates still fetch the matching
single-architecture package, which replaces the universal bundle.

The Linux package is self-contained for .NET but still depends on the native
webview, font, Secret Service and desktop libraries declared by the `.deb`.
The reader follows Avalonia's Linux `NativeWebView` path and prefers WPE
WebKit when `libWPEWebKit-2.0.so.1` is installed. Ubuntu 22.04 does not package
that WPE 2.0 ABI, so the `.deb` remains installable there through the
WebKitGTK 4.1 fallback.

Read-aloud uses the same Microsoft Edge TTS service on all three desktop heads.
On first use Kkindle installs `edge-tts` into its own data directory: Windows
and Linux use pipx, while macOS creates an application-local Python venv and
exposes the command through the shared runtime bin directory. macOS locates
Python 3 in `PATH` or the usual Homebrew and python.org install locations;
without Python 3 the UI shows the `xcode-select --install` / `brew install
python3` instructions. Playback runs in-process through AVFoundation's
`AVAudioPlayer`, so pausing never suspends a child process (a stopped child
used to stall every other process launch in the app).

The Settings > Diagnostics page performs the same local checks at runtime and
shows the detected WebView library, writable data paths, bundled PDF parser,
Calibre, TTS and Kindle service. PDFium native assets ship for Windows, Linux
and macOS. PDF pages and first-page covers use the same renderer; its text
geometry drives selection, underlines, highlights and editable comments.
Continuous scrolling, single-page and two-page modes share that page map.
View rotation is saved with reading progress; selection, annotations and outline
destinations rotate with the page. Paper, chrome and neutral ink share the reader
theme surface, while saturated colors in figures are preserved. Classic keeps the
original colors.
The reader renders clipped BGRA regions at the current display scale directly
from PDFium, avoiding PNG round-trips and enlarged low-resolution page images.
Its bounded page cache is independent of the full document's scroll extent.
The current page becomes usable before outlines and the text-only search index
finish loading in the background. Closing a book cancels that background work.
Embedded outlines retain their hierarchy and page destinations. PDFs without
outlines have a page list. Text PDFs also support local search, AI context and
TTS. Scanned pages support viewing, page navigation, bookmarks and page notes;
OCR is not included. PDF page notes can be anchored to arbitrary normalized page
positions. PDF zoom and the position within a page are restored without changing
EPUB typography preferences.

The `Development Build` GitHub Actions workflow runs only when manually
dispatched. It chooses the next development version based on the latest stable
release (or a manually supplied base version), builds Windows, Linux and macOS
packages, and publishes a GitHub pre-release tagged with the development
version. The workflow also keeps the per-platform artifacts for seven days.

Calibre is not bundled in any Windows, Linux or macOS archive. It is optional and is
discovered from the application directory, standard install locations or
`PATH`, or the user can select `ebook-convert` in Settings. On Debian/Ubuntu,
the `.deb` lists `calibre` only as a suggested package, so installing Kkindle
does not automatically install it.

The Settings page also offers explicit user-initiated installation buttons.
Windows downloads the official signed MSI and launches Windows Installer;
Linux runs calibre's official isolated installer into `~/calibre-bin` without
root; macOS verifies the official DMG and application signature before placing
`calibre.app` in `~/Applications`. KFX Input is downloaded from the project's
mirror when available (calibre's official plugin index otherwise), validated
as a plugin ZIP and installed with the detected `calibre-customize`. These
downloads never become part of a Kkindle release artifact.

Local macOS builds and GitHub Releases use ad-hoc signing when Apple
distribution credentials are not configured. The archive includes
`MACOS-README.md` with the Gatekeeper override steps required for that build.
Development Build pre-releases are always ad-hoc signed; only the stable
Release workflow imports distribution credentials.

For normal Gatekeeper-approved distribution, configure these repository
secrets:
`APPLE_CERTIFICATE_P12_BASE64`, `APPLE_CERTIFICATE_PASSWORD`,
`APPLE_NOTARY_KEY_BASE64`, `APPLE_NOTARY_KEY_ID`, and `APPLE_NOTARY_ISSUER`.
`APPLE_SIGNING_IDENTITY` and `APPLE_KEYCHAIN_PASSWORD` are optional; the
workflow discovers the Developer ID identity and generates a temporary
keychain password when they are omitted. The release job imports the
certificate, signs with the hardened runtime, notarizes, staples and validates
the app, then signs, notarizes and staples the DMG as well. Supplying only part
of the required credentials fails the release instead of silently falling back
to ad-hoc signing. Step-by-step certificate and API-key setup, including local
notarization without CI, is in [macos-notarization.md](macos-notarization.md).

`scripts/macos/Kkindle.entitlements` grants the hardened runtime's JIT,
unsigned-executable-memory and library-validation exceptions required by the
.NET runtime and the bundled libmtp/libusb. The build script signs nested
Mach-O images from the inside out and applies entitlements to the real
executables; it no longer relies on the deprecated `--deep` signing shortcut.
Both macOS workflows run the portable test projects and launch the packaged
bundle for a 20-second smoke test.

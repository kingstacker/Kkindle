# Linux and macOS

Kkindle uses one portable Avalonia UI with separate Windows, Linux and macOS
desktop heads. Windows retains WPD support for MTP-only Kindle devices. Linux
and macOS support Kindles exposed as mounted USB storage; MTP devices are not
currently enumerated on those systems.

## User data and secrets

- Linux data: `$XDG_DATA_HOME/Kkindle`, falling back to
  `~/.local/share/Kkindle`; root configuration uses `$XDG_CONFIG_HOME/Kkindle`
  or `~/.config/Kkindle`.
- macOS data and root configuration:
  `~/Library/Application Support/Kkindle`.
- Linux stores its wrapping key in Secret Service using `secret-tool`.
- macOS stores its wrapping key in the login Keychain.
- Windows continues to use DPAPI and its existing data layout.

## macOS desktop behavior

macOS uses the system title bar, traffic lights, rounded window frame, shadow
and resize controls in the library, reader and editor windows. The page extends
under the transparent native title bar, with no separate grey strip or window
title; content leaves room for the traffic lights. The menu-bar
status icon is a monochrome template image; AppKit supplies its light/dark
appearance independently of the full-color Dock icon.

Drag the top header, page title or empty space alongside it to move the window
immediately. Buttons and inputs in that area retain their normal actions;
reading, book selection and fullscreen do not start window moves. Double-click
the header to zoom or restore the window.

Use Command-F to search, Command-I to import, Command-click to select multiple
books, Command-, for settings, and Control-Command-F for native fullscreen.
The View menu also exposes Zen mode while a reader is open. Zen enters a native
fullscreen Space and restores the reader panels when it exits, including when
fullscreen is left through the green traffic light.

The red traffic light and Command-W save and hide the main window. Click the
Dock icon or choose Open Kkindle from its status menu to reopen it. Command-Q
exits after saving the reading session and pending settings.

For a local Debug app with its own .NET runtime, quit Kkindle, then run:

```sh
bash scripts/build-macos-debug.sh
open artifacts/Kkindle.app
```

The builder uses the pinned SDK from PATH or `.tools/dotnet`, and generates a
macOS `.icns` Dock icon and an ad-hoc signed app bundle.

## Build and package

Install the exact .NET SDK version pinned by the repository root `global.json`
before building. Release scripts run from the repository root and refuse other
SDK versions so Linux, macOS and Windows packages are produced by the same SDK.

```sh
dotnet --version # must print 10.0.400
dotnet build src/Kkindle.Desktop.Linux/Kkindle.Desktop.Linux.csproj -c Release
dotnet build src/Kkindle.Desktop.MacOS/Kkindle.Desktop.MacOS.csproj -c Release
bash scripts/build-linux-release.sh 0.5.2 artifacts/linux linux-x64
bash scripts/build-macos-release.sh 0.5.2 artifacts/macos osx-arm64
```

The Linux package is self-contained for .NET but still depends on the native
webview, font, Secret Service and desktop libraries declared by the `.deb`.
The reader follows Avalonia's Linux `NativeWebView` path and prefers WPE
WebKit when `libWPEWebKit-2.0.so.1` is installed. Ubuntu 22.04 does not package
that WPE 2.0 ABI, so the `.deb` remains installable there through the
WebKitGTK 4.1 fallback.

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

The `Development Build` GitHub Actions workflow can be run manually or by
pushing the `dev`, `develop`, or `dev/**` branches. It appends the Actions run
number to a base version such as `1.0.6-dev`, builds Windows installer and
three-platform packages, and publishes a GitHub pre-release tagged with the
development version. The workflow also keeps the per-platform artifacts for
seven days. Development macOS packages always use ad-hoc signing.

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

For normal Gatekeeper-approved distribution, configure these repository
secrets:
`APPLE_CERTIFICATE_P12_BASE64`, `APPLE_CERTIFICATE_PASSWORD`,
`APPLE_NOTARY_KEY_BASE64`, `APPLE_NOTARY_KEY_ID`, and `APPLE_NOTARY_ISSUER`.
`APPLE_SIGNING_IDENTITY` and `APPLE_KEYCHAIN_PASSWORD` are optional; the
workflow discovers the Developer ID identity and generates a temporary
keychain password when they are omitted. The release job imports the
certificate, signs with the hardened runtime, notarizes, staples and validates
the app before uploading it. Supplying only part of the required credentials
fails the release instead of silently falling back to ad-hoc signing.

# Kkindle

[![Release](https://github.com/kingstacker/Kkindle/actions/workflows/release.yml/badge.svg)](https://github.com/kingstacker/Kkindle/actions/workflows/release.yml) [![Latest release](https://img.shields.io/github/v/release/kingstacker/Kkindle)](https://github.com/kingstacker/Kkindle/releases/latest)

[Official website](https://kkindle.stacker.beauty) · [Chinese user manual](https://kkindle.stacker.beauty/manual/)

[简体中文](README.zh-CN.md) · **English**

Kkindle is a quiet, cross-platform ebook and Kindle manager built with Avalonia. It combines a local library, reading, annotations, AI assistance, format conversion, and Kindle transfer in one desktop app.

![Kkindle library](docs/images/主界面.png?v=20261004)

## Platform status

| Platform | Status | Notes |
| --- | --- | --- |
| Windows | ✅ Tested | Stable; minor fixes in progress |
| macOS | 🟡 Initial testing | Packages available; fixes and testing in progress |
| iOS | 🚧 In development | Initial version in development |
| Android | 🚧 In development | Initial version in development |
| Linux | ⏳ Pending testing | Full testing not yet done |

## Features

- **Library** — Import EPUB, PDF, MOBI, AZW3, and TXT; TXT encoding and chapter/volume headings are detected automatically and converted to EPUB without Calibre. Manage metadata, covers, tags, collections, search, and reading status. When a book with the same title is imported, choose to add the format, keep it separately, or skip; deleted content goes to a recoverable trash first.
- **Kreader** — Open EPUB, PDF, and DRM-free AZW3 in the app; AZW3 uses the built-in KF8 reader without Calibre. Paginated or scrolling reading, table of contents, bookmarks, search, annotations, typography settings, reading progress, and read-aloud on Windows, Linux, and macOS (Linux requires mpv, ffplay, VLC, or GStreamer; macOS uses the built-in afplay).
- **PDF reading** — First-page library covers, continuous scrolling, single-page and two-page reading, fit-width/page controls, sharp zoom and 90° rotation. Supports selectable text, six underline/highlight styles, editable comments, and page annotations at arbitrary PDF positions through the page-note icon or right-click. Pages appear before full-book text indexing finishes; only visible regions are rendered at the screen's pixel density. Search, bookmarks and reading position use the same local reading data as EPUB. Scanned pages support viewing and page notes; OCR is not included.
- **AI assistant** — Ask questions about the current book, summarize chapters, explain selections, and choose from discovered models through DeepSeek, OpenAI, or compatible endpoints. Requests use relevant local excerpts only.
- **Kindle** — Detect USB/WPD/MTP devices, transfer books, manage fonts and dictionaries, and import `My Clippings.txt`.
- **Tools and sync** — Calibre conversion, local backups, encrypted credentials, and optional S3 / WebDAV synchronization.

## Download

Download the latest Windows, Linux, or macOS package from the [official site](https://kkindle.stacker.beauty/#downloads); it automatically selects the latest stable release.

Backup download: [GitHub Releases](https://github.com/kingstacker/Kkindle/releases).

Calibre is required separately for format conversion. Official GitHub releases provide Windows, Linux, and macOS packages. macOS builds support macOS 12 or later on Intel and Apple Silicon; when Apple distribution credentials are not configured, the archive is ad-hoc signed and includes `MACOS-README.md` with the Gatekeeper steps.

The **Settings → Diagnostics** page checks the data directory, PDF parser, WebView, Calibre, TTS, and Kindle services, and is the first place to look for cross-platform issues. The library trash can restore a deleted book or a single format, and permanently purge items after confirmation; trash contents are included in local backups.

## Cloud sync

Choose **S3** or **WebDAV** in **Settings → Data & sync → Cloud sync**, enter the connection details, and select **Save settings**. Both providers retain their own addresses and credentials. Manual, startup, exit, and scheduled sync use the saved selection.

For WebDAV, enter the URL of an existing directory, such as `https://dav.example.com/dav/`, and credentials if required. Some services require an app password. Kkindle creates a sync subdirectory named `kkindle` by default; change it in Advanced options. The connection test verifies read/write access and removes its test file. The server must support standard PROPFIND, MKCOL, GET, PUT, MOVE, and DELETE operations. Completed uploads are published with MOVE so interrupted transfers do not replace existing snapshots.

The sync subdirectory, encryption, scheduling, and transfer options are shared between providers. Devices syncing the same directory need the same encryption key; use a new subdirectory when changing keys. Connection profile exports include only the selected provider's credentials. Existing `.kkindle-s3.json` profiles remain supported; WebDAV profiles use `.kkindle-webdav.json`. Local backups exclude connection passwords and encryption keys.

## Run from source

Requires the exact [.NET SDK 10.0.400](https://dotnet.microsoft.com/download/dotnet/10.0) pinned by `global.json`.

Windows (PowerShell):

```powershell
dotnet restore Kkindle.sln -p:Platform=x64
dotnet build Kkindle.sln -p:Platform=x64
dotnet test Kkindle.sln --no-build -p:Platform=x64
# Run the slow regression suite explicitly before a release
dotnet test Kkindle.sln --no-build -p:Platform=x64 -p:RunSlowTests=true
dotnet run --project src\Kkindle.Desktop.Windows\Kkindle.Desktop.Windows.csproj -p:Platform=x64
```

macOS:

```bash
dotnet build src/Kkindle.Desktop.MacOS/Kkindle.Desktop.MacOS.csproj
dotnet test tests/Kkindle.Tests/Kkindle.Tests.csproj
dotnet run --project src/Kkindle.Desktop.MacOS/Kkindle.Desktop.MacOS.csproj
```

Linux uses the same commands with `Kkindle.Desktop.Linux` in place of `Kkindle.Desktop.MacOS`.

Build a macOS app bundle for either architecture:

```bash
# Apple Silicon
bash scripts/build-macos-release.sh 1.1.3 artifacts/macos osx-arm64
# Intel
bash scripts/build-macos-release.sh 1.1.3 artifacts/macos osx-x64
# Universal (one package for both architectures)
bash scripts/build-macos-release.sh 1.1.3 artifacts/macos osx-universal
```

Every command works on either architecture: the bundled libmtp and libusb are built from source for the target (cross-compiled with the macOS SDK when it differs from the host), so an Intel Mac can produce the arm64 package and vice versa. CI still uses native `macos-15` and `macos-15-intel` runners to keep builds fast.

`osx-universal` produces a single `Kkindle.app` that starts natively on both Intel and Apple Silicon. A self-contained .NET publish contains architecture-specific native and ReadyToRun assemblies that cannot be combined into one file set, so the bundle carries both complete payloads under `Contents/MacOS/x86_64` and `Contents/MacOS/arm64` and a universal launcher execs the matching one. Expect roughly twice the download size of a single-architecture package.

The default test run skips expensive UI, rendering, and GC-stress cases while keeping core, platform, device, and a small UI smoke set. The skipped tests remain in the repository and can be enabled with `RunSlowTests=true`.

Linux also requires WebKitGTK and Secret Service. See [cross-platform notes](docs/cross-platform.md) for platform packages and signing details. Mobile heads live in `Kkindle.Mobile.sln`; see [mobile notes](docs/mobile-skeleton.md) for workloads and simulator commands.

## Referenced technologies

- **Feature references and tools** — [Calibre](https://github.com/kovidgoyal/calibre) and [KFX Input](https://www.mobileread.com/forums/showthread.php?t=291290).
- **Runtime** — [Avalonia](https://github.com/AvaloniaUI/Avalonia), [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet), [EF Core](https://github.com/dotnet/efcore), and [PdfPig](https://github.com/UglyToad/PdfPig).
- **Testing** — [xUnit](https://github.com/xunit/xunit) and [VSTest](https://github.com/microsoft/vstest).

## Acknowledgments

- Community: [LINUX DO](https://linux.do/?tl=en)
- Public-service site: any

## License

[GPL-3.0 License](LICENSE). Third-party components and bundled fonts retain their own licenses.

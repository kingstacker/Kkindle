# Kkindle

[![Release](https://github.com/kingstacker/Kkindle/actions/workflows/release.yml/badge.svg)](https://github.com/kingstacker/Kkindle/actions/workflows/release.yml)
[![GitHub release](https://img.shields.io/github/v/release/kingstacker/Kkindle?display_name=tag)](https://github.com/kingstacker/Kkindle/releases/latest)

[简体中文](README.zh-CN.md) · **English**

Kkindle is a personal ebook and Kindle device manager for Windows, Linux, and macOS. Built with Avalonia, it brings your local library, format conversion, reading, annotations, AI-assisted reading, and Kindle transfers together in a quiet gray-and-paper interface.

On first launch, a two-step welcome wizard asks for the interface language and your primary Kindle model. The language defaults to Simplified Chinese for Chinese system locales and to English for other system locales. Both choices can be changed later from Settings or the device card.

## Platform status

| Platform | Core testing | Platform validation | Current status |
| --- | --- | --- | --- |
| Windows 11 x64 | Complete: Release build, automated tests, and Windows installer smoke tests | Tested on a physical Windows 11 machine | Core features usable and release-ready |
| Linux x64 | Complete: Release build, shared platform tests, and Linux package launch smoke test | Tested on a physical Debian 13 machine | Core features usable and release-ready |
| macOS Intel/Apple Silicon | Complete: Release build and signing/package validation flow | Physical macOS testing is still pending | Packages can be generated; hardware validation pending |

The current macOS packages are not advertised as physically validated. Before regular use, verify startup, the reader, and Kindle USB mounting on the target macOS version.

## Screenshots

### Desktop library

![Kkindle desktop library](docs/images/主界面.png)

### Gallery mode

![Kkindle gallery mode](docs/images/画廊模式.png)

### Desktop library context menu

![Kkindle desktop library context menu](docs/images/电脑书库右键菜单.png)

### Douban metadata matching

![Kkindle Douban metadata matching](docs/images/豆瓣匹配.png)

### Kindle library management

![Kkindle Kindle library management](docs/images/Kindle书库管理.png)

### Kindle fonts and dictionaries

![Kkindle Kindle font and dictionary management](docs/images/Kindle字体词典管理.png)

### Notes and annotations

![Kkindle notes and annotations](docs/images/批注管理.png)

### Kreader

![Kreader paginated reading view](docs/images/Kreader主界面.png)

### Kreader Zen mode

![Kreader Zen mode with the minimal table of contents on the left](docs/images/禅模式.png)

### AI reading assistant

![Kreader AI reading assistant](docs/images/AI对话.png)

### Z-Library online library

![Z-Library online library](docs/images/Z-library在线书库.png)

### General settings

![Kkindle general settings](docs/images/基础设置界面.png)

## Features

### First-run wizard and bilingual UI

- The first launch opens a welcome wizard with Simplified Chinese and English interface options.
- Chinese system locales (`zh-*`) default to Simplified Chinese; other system locales default to English.
- The second step lets you choose a Kindle vendor and model and saves the selection as the default device model. A connected device can still keep its own remembered model.
- The language can be changed later in Settings, with the main interface, reader, status messages, and device selectors refreshed immediately.

### Local library management

- Import EPUB, PDF, MOBI, and AZW3 files, including drag-and-drop and Chinese filenames. When importing a folder, choose per book whether to generate missing EPUB/AZW3 formats automatically.
- Parse EPUB titles, authors, descriptions, and covers automatically; deduplicate with SHA-256 and group multiple formats of the same book together.
- Search by title or author, filter by author, tag, format, collection, reading status, or favorite state, and sort in several ways.
- Manage collections, favorites, and unread/reading/finished states; starting and finishing a book updates its state automatically.
- Switch between shelf, list, and gallery views. Single-click opens details; a quick double-click opens the reader without accidentally opening the details panel.
- Edit covers, titles, authors, series, tags, collections, and descriptions. Rectangle selection and multi-selection support batch actions such as sending to Kindle, sending by email, and deleting.
- Match metadata by title through Douban. Candidates show cover, author, publisher, publication date, price, rating, and rating count before you confirm an update.
- Store books, covers, and reading records locally in a dedicated SQLite database.

### Kreader reader

- Read EPUB, PDF, AZW3, and MOBI. AZW3/MOBI files are prepared as temporary EPUB reading copies when opened.
- EPUB supports table of contents, bookmarks, in-page search (`Ctrl+F`), footnote popups, and remembered reading progress. Bookmark summaries use the chapter and text from the bookmarked page.
- Support horizontal paginated/scrolling layouts and single-/two-page display. Vertical writing uses a paginated single-page layout on every platform; on Linux, glyph probes continuously calibrate whole-column page steps so the text fills the reading area without clipping half a CJK character at either edge. Click the left or right third of the page or use the mouse wheel to turn pages; vertical writing mirrors the traditional direction. The next chapter is preloaded to reduce transition waits.
- Choose no animation, fade, horizontal slide, or ripple page transitions. Zen mode provides true full-screen reading (`F11` to enter, `Esc` to exit) with a minimal table of contents.
- PDF supports a local text index, full-text search, page progress, bookmarks, page notes, and AI context retrieval.
- Save typography per book, including font size, line height, text width, margins, and CJK font. New books default to `1.20×` font size, `1.80` line height, `1200 px` text width, and `24 px` side margins.
- Create EPUB highlights and notes, use quick annotation tools, jump back to the source, and export annotations. The annotation list shows chapter, selected text, and note, and refreshes after new annotations are added.
- The built-in AI reading assistant can summarize chapters, explain selections, provide a whole-book overview, and hold a free-form conversation using the current selection and local book index. It supports reasoning-depth and model selection and works with DeepSeek, OpenAI, and compatible endpoints.

### Reading productivity tools

- Import TTF, OTF, WOFF, and WOFF2 fonts and select them in reader and default typography settings.
- Import UTF-8 text dictionaries (`term<Tab>definition` or `term=definition`) and look up a selected word while reading.
- The reading dashboard summarizes started/finished books, total time, average progress, bookmarks, and annotations, and exports CSV.
- The Notes & Annotations view unifies highlights from local books and `My Clippings.txt` from connected Kindles. Filter by source, search full text, locate local source text, and delete individual records.
- Export Records combines local and Kindle reading materials as Markdown or plain text using the current source and search filters.

### Z-Library online library

- Search and download through Z-Library's official eAPI with title/author search, format and language filters, and pagination.
- Import completed downloads into the desktop library, parse metadata and covers, deduplicate, and clean up temporary files automatically.
- Store account credentials (email and password) encrypted with the current user's DPAPI, Secret Service, or Keychain. The API service address is configurable, and credentials are not written to backup packages.
- Show download status in the task list, allow cancellation, and add completed books to the library automatically.

### Format conversion

- Use Calibre to convert between EPUB, AZW3, and PDF; MOBI can also be used as a source. Generated formats are grouped with the original book.
- Export Kindle books to the desktop library from the context menu. KFX can be converted to EPUB with the KFX Input plugin installed by the user in Calibre; DRM bypass is not supported.
- Show live conversion progress. Tasks can be minimized to the background and reopened from the book card.
- Calibre is not bundled in any platform package. Kkindle discovers `ebook-convert` in standard install locations or `PATH`, accepts a manually configured path, and can download Calibre from its official source through Settings.

### Kindle transfer

- Detect Kindles connected as USB disks or through WPD/MTP, and show device capacity, books, and covers. The first-run wizard supports a default model, while each connected device can retain its own model.
- Send books to a device, safely eject, verify transfers, clean up after disconnects, and monitor device insertion/removal. Multi-selection supports batch export to the desktop library or deletion from the device.
- Access only the Kindle `documents` directory and do not modify the device system database.
- Read, import, export, and delete TTF/OTF files in the device `fonts` directory.
- Read, import, export, and delete AZW/AZW3/MOBI/PRC/KFX files in `documents/dictionaries`.
- Font and dictionary operations support both USB disks and WPD/MTP Kindles and stay inside their designated directories. Incomplete transfers are cleaned up on cancellation or disconnect.
- Read highlights and notes from `documents/My Clippings.txt`. Deletion only removes records from that file; it does not modify book sidecars or cloud-synced annotations.
- Send EPUB or PDF files to the Kindle Personal Documents address through SMTP.

### Backup and privacy

- Import or export `.kkindle` backups to migrate the library, covers, and reading records; enable daily backups and retention limits if desired.
- Configure the default open format, Calibre path, AI/network permissions, and data directory in Settings, and migrate the data directory safely through a backup package.
- Encrypt AI API keys in the current user's secure storage.
- API keys and SMTP passwords are not written to backup packages. AI requests send only relevant excerpts, never the entire book.

## Requirements

- Windows 11 x64, a mainstream x64 Linux desktop, or macOS 12 or later (Intel/Apple Silicon).
- Building from source requires the [.NET SDK 10.0.400](https://dotnet.microsoft.com/download/dotnet/10.0) specified by `global.json`; the release scripts reject other SDK versions.
- Linux requires WebKitGTK and Secret Service/`secret-tool`.
- Calibre must be installed separately for format conversion on any platform, or an existing `ebook-convert` path must be configured in Settings.
- Windows supports USB-disk and WPD/MTP Kindles; Linux/macOS currently support Kindles mounted as USB disks.

## Download and installation

Download the latest version from [GitHub Releases](https://github.com/kingstacker/Kkindle/releases):

- `Kkindle-X.Y.Z-win-x64-setup.exe`: recommended installer with Start Menu shortcuts, an optional desktop shortcut, and an uninstaller.
- `Kkindle-X.Y.Z-win-x64-portable.zip`: portable version; extract and run.
- `kkindle_X.Y.Z_amd64.deb` / `arm64.deb`: packages for Ubuntu, Debian, and Linux Mint.
- `Kkindle-X.Y.Z-linux-x64.tar.gz` / `linux-arm64.tar.gz`: portable packages for other Linux distributions.
- `Kkindle-X.Y.Z-osx-arm64.tar.gz` / `osx-x64.tar.gz`: macOS `.app` packages.
- `SHA256SUMS.txt`: SHA-256 checksums for every release package.

All platform packages include the .NET runtime but not Calibre. Kkindle discovers a system Calibre installation or accepts an `ebook-convert` path in Settings. Linux data follows XDG directories; macOS data is stored in `~/Library/Application Support/Kkindle`.

The Windows installer checks GitHub Releases for the latest stable version after startup by default; you can also check manually in Settings > About. After confirmation, it downloads the installer and verifies it against `SHA256SUMS.txt`; Kkindle stays open and marks the update as ready. When you exit, Kkindle asks for confirmation before launching the installer and restarting. The Windows portable build currently opens the Release download page only; in-app installers for Linux and macOS are planned for a later version.

## Run from source

```powershell
dotnet --version  # must print 10.0.400
dotnet restore Kkindle.sln
dotnet build Kkindle.sln -p:Platform=x64
dotnet test Kkindle.sln --no-build -p:Platform=x64
dotnet run --project src\Kkindle.Desktop.Windows\Kkindle.Desktop.Windows.csproj -p:Platform=x64
# Linux: dotnet run --project src/Kkindle.Desktop.Linux/Kkindle.Desktop.Linux.csproj
# macOS: dotnet run --project src/Kkindle.Desktop.MacOS/Kkindle.Desktop.MacOS.csproj
```

## Build release packages locally

Install [Inno Setup 6](https://jrsoftware.org/isinfo.php), then run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build-Release.ps1 `
  -Version 1.0.0
```

The Windows script creates an installer EXE, portable ZIP, and checksums. Use `scripts/build-linux-release.sh` and `scripts/build-macos-release.sh` for Linux and macOS; see [cross-platform notes](docs/cross-platform.md) for the complete commands.

Release outputs are placed in:

```text
artifacts\release\1.0.0\
```

Windows stores application data next to the executable by default; Linux uses XDG data directories, and macOS uses `~/Library/Application Support/Kkindle`. All platforms support moving the data directory from Settings. Automatic backups are stored in the `backups` directory beside the data root.

## Automated GitHub releases

When a `vX.Y.Z` tag is pushed, `.github/workflows/release.yml` builds self-contained packages for Windows, Ubuntu, and macOS, computes unified checksums, generates `update-manifest.json`, and creates a GitHub Release. Tags with a suffix are published as prereleases.

```powershell
git tag v1.0.0
git push origin v1.0.0
```

If a run fails, manually run the Release workflow in GitHub Actions with an existing tag. The workflow replaces files with the same names in that Release and does not create a duplicate version.

## GitHub development builds

While the program is still evolving, there is no need to create a tag or GitHub Release. Run `Development Build` manually in GitHub Actions with a base version such as `0.6.0-dev`; the workflow appends the run number, builds packages for all three platforms, and stores them as Actions artifacts for seven days. Pushing the `develop` or `dev/**` branches also triggers this workflow automatically.

Development builds produce a Windows portable ZIP, Linux installer/portable packages, and a macOS ad-hoc package. They do not create Releases or modify repository tags. The macOS archive includes Gatekeeper opening instructions.

## Verification

The project includes automated tests for the library and legacy database migration, backups, reading progress, typography, format policies, PDF text extraction, dictionaries, fonts, application settings, and related platform behavior:

```powershell
dotnet --version  # must print 10.0.400
dotnet test Kkindle.sln -c Debug -p:Platform=x64
dotnet test Kkindle.sln -c Release -p:Platform=x64
```

## Project structure

```text
src/Kkindle.App             Cross-platform Avalonia UI
src/Kkindle.Core            Domain models, policies, and service contracts
src/Kkindle.Infrastructure  SQLite, device, conversion, backup, and AI services
src/Kkindle.Desktop.*       Windows, Linux, and macOS entry points
src/Kkindle.Platform.*      Platform-specific services
tests/                      Portable, shared-platform, and Windows-platform tests
```

## Referenced technologies and open-source projects

Kkindle's feature design and implementation use or reference the projects below. This section describes technical relationships only; each project's source and components remain subject to their own licenses.

### Feature references and external tools

- [ZlibraryKO/zlibrary.koplugin](https://github.com/ZlibraryKO/zlibrary.koplugin): reference for Z-Library login, search, language and format filters, service-address discovery, and download flows.
- [kovidgoyal/calibre](https://github.com/kovidgoyal/calibre): runs through the separate `ebook-convert` process for reading or converting EPUB, AZW3, MOBI, PDF, and KFX formats.
- [KFX Input](https://www.mobileread.com/forums/showthread.php?t=291290): handles DRM-free KFX files; users install it themselves or from Calibre's official plugin index in Settings.

### Runtime dependencies

- [AvaloniaUI/Avalonia](https://github.com/AvaloniaUI/Avalonia): Windows, Linux, and macOS desktop UI and native WebView foundation.
- [CommunityToolkit/dotnet](https://github.com/CommunityToolkit/dotnet): MVVM infrastructure through `CommunityToolkit.Mvvm`.
- [dotnet/efcore](https://github.com/dotnet/efcore): source project for `Microsoft.Data.Sqlite`, used for local SQLite storage.
- [UglyToad/PdfPig](https://github.com/UglyToad/PdfPig): PDF reading and text extraction.

### Test tools

- [xunit/xunit](https://github.com/xunit/xunit): automated testing framework.
- [xunit/visualstudio.xunit](https://github.com/xunit/visualstudio.xunit): Visual Studio and .NET test adapter for xUnit.
- [microsoft/vstest](https://github.com/microsoft/vstest): test platform used by `Microsoft.NET.Test.Sdk`.

## License

This project is released under the [MIT License](LICENSE). Fonts, Calibre, and other third-party components distributed with the application remain subject to their respective licenses.

## Acknowledgements

- Community: [LINUX DO](https://linux.do/?tl=en)
- Public-interest site: any

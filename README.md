# Kkindle

[![Release](https://github.com/kingstacker/Kkindle/actions/workflows/release.yml/badge.svg)](https://github.com/kingstacker/Kkindle/actions/workflows/release.yml) [![Latest release](https://img.shields.io/github/v/release/kingstacker/Kkindle)](https://github.com/kingstacker/Kkindle/releases/latest)

[简体中文](README.zh-CN.md) · **English**

Kkindle is a quiet, cross-platform ebook and Kindle manager built with Avalonia. It combines a local library, reading, annotations, AI assistance, format conversion, and Kindle transfer in one desktop app.

![Kkindle library](docs/images/主界面.png)

## Features

- **Library** — Import EPUB, PDF, MOBI, and AZW3; manage metadata, covers, tags, collections, search, and reading status.
- **Kreader** — Paginated or scrolling reading, table of contents, bookmarks, search, annotations, typography settings, reading progress, and Windows read-aloud.
- **AI assistant** — Ask questions about the current book, summarize chapters, explain selections, and choose from discovered models through DeepSeek, OpenAI, or compatible endpoints. Requests use relevant local excerpts only.
- **Kindle** — Detect USB/WPD/MTP devices, transfer books, manage fonts and dictionaries, and import `My Clippings.txt`.
- **Tools and sync** — Calibre conversion, Z-Library downloads, local backups, encrypted credentials, and optional S3 synchronization.

## Download

Download the latest Windows, Linux, or macOS package from [GitHub Releases](https://github.com/kingstacker/Kkindle/releases).

Calibre is required separately for format conversion. macOS packages are available, but target-device validation is still in progress.

## Run from source

Requires [.NET SDK 10.0.400](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
dotnet restore Kkindle.sln
dotnet build Kkindle.sln -p:Platform=x64
dotnet test Kkindle.sln --no-build -p:Platform=x64
dotnet run --project src\Kkindle.Desktop.Windows\Kkindle.Desktop.Windows.csproj -p:Platform=x64
```

Linux also requires WebKitGTK and Secret Service. See [cross-platform notes](docs/cross-platform.md) for Linux and macOS commands.

## License

[MIT License](LICENSE). Third-party components and bundled fonts retain their own licenses.

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
- **Tools and sync** — Calibre conversion, Z-Library downloads, local backups, encrypted credentials, and optional S3 / WebDAV synchronization.

## Download

Download the latest Windows, Linux, or macOS package from [GitHub Releases](https://github.com/kingstacker/Kkindle/releases).

Calibre is required separately for format conversion. macOS packages are available, but target-device validation is still in progress.

## Cloud sync

Choose **S3** or **WebDAV** in **Settings → Data & sync → Cloud sync**, enter the connection details, and select **Save settings**. Both providers retain their own addresses and credentials. Manual, startup, exit, and scheduled sync use the saved selection.

For WebDAV, enter the URL of an existing directory, such as `https://dav.example.com/dav/`, and credentials if required. Some services require an app password. Kkindle creates a sync subdirectory named `kkindle` by default; change it in Advanced options. The connection test verifies read/write access and removes its test file. The server must support standard PROPFIND, MKCOL, GET, PUT, MOVE, and DELETE operations. Completed uploads are published with MOVE so interrupted transfers do not replace existing snapshots.

The sync subdirectory, encryption, scheduling, and transfer options are shared between providers. Devices syncing the same directory need the same encryption key; use a new subdirectory when changing keys. Connection profile exports include only the selected provider's credentials. Existing `.kkindle-s3.json` profiles remain supported; WebDAV profiles use `.kkindle-webdav.json`. Local backups exclude connection passwords and encryption keys.

## Run from source

Requires [.NET SDK 10.0.400](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
dotnet restore Kkindle.sln
dotnet build Kkindle.sln -p:Platform=x64
dotnet test Kkindle.sln --no-build -p:Platform=x64
dotnet run --project src\Kkindle.Desktop.Windows\Kkindle.Desktop.Windows.csproj -p:Platform=x64
```

Linux also requires WebKitGTK and Secret Service. See [cross-platform notes](docs/cross-platform.md) for Linux and macOS commands.

## Referenced technologies

- **Feature references and tools** — [zlibrary.koplugin](https://github.com/ZlibraryKO/zlibrary.koplugin), [Calibre](https://github.com/kovidgoyal/calibre), and [KFX Input](https://www.mobileread.com/forums/showthread.php?t=291290).
- **Runtime** — [Avalonia](https://github.com/AvaloniaUI/Avalonia), [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet), [EF Core](https://github.com/dotnet/efcore), and [PdfPig](https://github.com/UglyToad/PdfPig).
- **Testing** — [xUnit](https://github.com/xunit/xunit) and [VSTest](https://github.com/microsoft/vstest).

## Acknowledgments

- Community: [LINUX DO](https://linux.do/?tl=en)
- Public-service site: any

## License

[MIT License](LICENSE). Third-party components and bundled fonts retain their own licenses.

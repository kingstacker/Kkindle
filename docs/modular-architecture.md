# Modular application architecture

## Dependency direction

```text
Desktop.Windows / Desktop.Linux / Desktop.MacOS
    -> Kkindle.App (Avalonia views and desktop lifecycle)
        -> Kkindle.Application (application services and TTS coordination)
            -> Kkindle.Infrastructure (storage, parsing, network clients)
                -> Kkindle.Core (models and platform contracts)
            -> Kkindle.Layout (chapter parsing and text layout models)
        -> Kkindle.Layout (text layout and drawing)
```

`Kkindle.Application` must not reference Avalonia, `Kkindle.App`, or a desktop
platform assembly. Its namespace is `Kkindle.ApplicationServices` so it does
not shadow Avalonia's `Application` type in the existing `Kkindle` namespace.
The architecture regression test enforces this boundary.

## Service modules

`ApplicationModules` is a per-shell composition root, not a global service
locator. `ApplicationModuleOptions` supplies storage, secret protection, explicit
platform conversion and metadata adapters, startup preferences, optional library, cache location,
update adapter, and retrieval logging. Construction does not open the book
library, download models, or start synchronization.

| Module | Responsibility |
| --- | --- |
| Library | Book library, conversion, metadata, preferences, backup, fonts, dictionary |
| Reader | Reading data, durable progress/time recovery, EPUB preparation, format cache, TTS cache/settings |
| AI | Shared API client, translation, embeddings, retrieval, context, model downloads, web search |
| Sync | S3/WebDAV synchronization and its existing wire protocol |
| Integrations | Device metadata/cache, diagnostics, remote library, email, optional updater |

The desktop bootstrap translates `AppServices` into these portable options and
supplies the Calibre converter. Platforms without a converter can supply
`UnavailableBookFormatConverter`; application composition never silently
selects a desktop executable.
`ReaderResourcePolicy` also owns EPUB progress validation, chapter-cache path
boundaries, and fragment decoding, with portable tests for invalid locations.
Normal startup loads preferences before creating the main window. The old
`MainWindow(paths, library, ...)` constructor remains a compatibility adapter
for existing callers and tests; both routes use the same service graph.

Window fields currently alias module services to preserve the large existing
UI implementation. New business orchestration should go into the application
layer rather than expanding these aliases or adding desktop checks there.

## Reader adapters

`IReaderHostFactory` in Core creates an EPUB or PDF surface and decides whether
an existing surface can be reused for a format. `AppServices.FormatReaderHostFactory`
can replace the desktop implementation without modifying main-window creation
or host-slot wiring. The default remains `NativeReaderHost` for EPUB and
`NativePdfReaderHost` for PDF.

EPUB uses two independent surfaces for active reading and preloading; PDF uses
one. Factory failures and singleton factories are rejected before a partial
pair is attached to the window. `IReaderAppearanceHost` is an optional capability
for updating native appearance without depending on concrete control types.

The older `AppServices.ReaderHostFactory` WebView hook is retained for source
compatibility. It does not override the native format-aware reader pipeline.

`IPdfDocumentHost` exposes document information, text indexing and annotation
tool modes, `IPdfNavigationHost` page labels and history, and `IPdfReadingHost`
opening, navigation, display modes and view-state persistence. Saved annotation
application, basic text interaction and in-page search have separate optional capabilities.
The window uses these interfaces for PDF operations without directly naming
the desktop PDF host; a test opens and navigates a PDF with an injected non-native host. PDF target
URI parsing and display-mode enums live in Core. Optional Avalonia-facing
interfaces provide thumbnails, zoom and annotation geometry without requiring
the desktop PDF control. Selection gestures, keyboard routing and parts of the
toolbar still require platform-specific work, so the capability set is not yet complete.

`IPdfDocumentFactory` supplies the document backend used by PDF metadata,
text indexing and the desktop PDF surface. The existing PDFium implementation
remains the desktop default; other platforms can supply a backend through their
metadata composition and reader factory. This interface alone does not provide
an Android PDF implementation or native library.

`ReaderSession` owns the linked cancellation lifetime for both EPUB and PDF.
Replacing or ending a session cancels its work before releasing the token source;
the window still owns view state, navigation gates and progress checkpoints.
`LocalSyncChanges` owns the pending-change watermark and retry backoff. The
window owns the dispatcher timers, status, lifecycle and sync operation itself;
a successful sync only acknowledges changes captured at its start.
`SyncDeletionCoordinator` owns retries when a provider reports deletions. It
asks the host to confirm each new deletion fingerprint, never retries after a
decline or cancellation, and keeps dialogs and status presentation in the shell.
`AutomaticSyncCoordinator` owns readiness, interval and bounded lifecycle
checkpoint decisions; dispatcher timers, status and actual sync execution remain
owned by the shell. The shell and `SyncModule` use `ISyncService`, with S3/WebDAV
as the default; a platform can inject a different implementation without
modifying window construction. Injected sync services are caller-owned.

For EPUB, `IReaderPositionHost`, `IReaderTextSearchHost` and
`IReaderSpeechHost` separate bookmark/progress capture, search and speech
highlighting from the desktop control. `IReaderLocationHost`,
`IReaderLayoutNavigationHost`, `IReaderConfiguredHost`, `IReaderPaginationHost`
and `IReaderPageCountHost` cover semantic navigation, reflow, page turns and
optional page-count estimation.
`ReaderSpeechTextBuilder` builds the
same source-offset-aware speech stream for the visible reader and background
chapter prefetch without constructing a control. The remaining layout,
navigation and input interactions still require further capability boundaries.

`ImportFileStaging` accepts local paths or file-provider streams. Local files
remain untouched; provider streams are copied into unique temporary directories
until the existing path-based import completes. It preserves file extensions,
isolates duplicate names and removes staging after success or failure. The file
picker uses this adapter, but Android document-provider behavior still needs a
real Android shell and device validation.

## TTS and lifetime ownership

`TtsService`, `ReaderTtsDocument`, and `ReaderTtsTextSnapshot` now live in the
application assembly. They receive document/highlight/navigation callbacks and
Core's TTS engine/audio interfaces, not Avalonia controls. Portable tests reference
this assembly directly instead of compiling copies of UI-project source files.

Each main window owns its `ApplicationModules`. It first cancels/drains work and
removes subscriptions, then disposes module clients once. An injected Douban
service follows the existing window-owned behavior. Library instances and
platform adapters are not disposed by the composition root. The shell-owned
`TtsService` retains its existing ownership of its engine and audio player.
Reader surfaces remain shell-owned and must not be shared between host slots.

No native reader library, data schema, sync version, UI layout, or default cache
path changes as part of this extraction. A future platform can provide a private
data root, secret protector, conversion adapter, and audio cache directory.

## Next extraction boundaries

1. Remaining reader session orchestration and PDF/EPUB capability contracts.
2. Library/import and synchronization execution independent of window events.
3. Platform file access and application lifecycle adapters for mobile suspension.
4. Responsive mobile navigation and gesture handling, separate from MainWindow.

Do not label the desktop application as fully mobile-ready until these remaining
boundaries and the Android native dependency graph have been validated.

## Android adaptation inventory

| Area | Current boundary and remaining work |
| --- | --- |
| Application entry and views | Desktop lifetime and `MainWindow` dominate startup and navigation. A mobile single-view shell, responsive layout, back handling and lifecycle/suspension adapters are still needed. |
| Reader | Core and Application expose session, navigation, layout, search and speech capabilities. Existing renderers are desktop Avalonia controls; Android needs compatible rendering, touch gestures and mobile resource limits. |
| Files and storage | `AppPaths` accepts a platform-selected root and file import can stage provider streams. Android must supply private writable paths and validate document-provider access and permissions. Folder, font, backup and other pickers still assume local paths. |
| Sync and recovery | S3/WebDAV protocol and persistence are in Infrastructure. Scheduling, UI status, shutdown and most operation coordination remain in `MainWindow`; Android needs lifecycle-aware scheduling. |
| Platform integrations | Secrets, TTS, conversion, device discovery and update installation have injectable contracts or options. Desktop supplies Calibre explicitly; Android must supply or disable platform-specific implementations. |
| Native dependencies | Infrastructure references desktop PDFium native packages and ONNX/Skia dependencies. Android native assets, ABIs, packaging and actual device behavior have not been established. |

An Android head project now exists in a separate solution (`Kkindle.Mobile.sln`):
`Kkindle.Mobile` holds a platform-neutral single-view shell with library, book
detail, materials and profile pages, and `Kkindle.Mobile.Android` supplies the
private data root, secret protector and document-provider import. See
[mobile-skeleton.md](mobile-skeleton.md) for the layout, the stubs that remain
(notably the reader renderers) and the build prerequisites.

The Android workload is still not installed in the checked development
environment, so none of this constitutes an Android build, APK, emulator test or
device validation.

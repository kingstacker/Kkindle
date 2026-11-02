# Mobile app (Android + iOS)

The mobile head is a separate solution so the desktop release pipeline keeps
building and testing without the mobile workloads installed.

```
Kkindle.sln                 desktop only: Core / Infrastructure / Application / App / Desktop heads
Kkindle.Mobile.sln          mobile: Kkindle.Mobile + Kkindle.Mobile.Android + Kkindle.Mobile.iOS + Kkindle.Mobile.Desktop
```

## Projects

| Project | TFM | Purpose |
| --- | --- | --- |
| `src/Kkindle.Mobile` | `net10.0` | Platform-neutral single-view shell, pages and mobile styles. No Android/iOS APIs. |
| `src/Kkindle.Mobile.Android` | `net10.0-android36.0` | Android head: `AvaloniaMainActivity`, private data root, secret storage, document-provider import. |
| `src/Kkindle.Mobile.iOS` | `net10.0-ios` | iOS head: `AvaloniaAppDelegate`, sandbox data root, secret storage, document picker. |
| `src/Kkindle.Mobile.Desktop` | `net10.0` | Dev-only preview harness that shows the phone shell in an 411x880 window. |

`Kkindle.Mobile` references Core, Application, Infrastructure and Layout, so the
same library, reader-data and metadata services the desktop shell uses are
available unchanged.

## Screens

Captured on the `Kkindle_Pixel` emulator:

| 书库 | 书籍详情 | 阅读页 |
| --- | --- | --- |
| ![书库](images/mobile/书库.png) | ![书籍详情](images/mobile/书籍详情.png) | ![阅读页](images/mobile/阅读页.png) |

| 阅读资料 | 我的 |
| --- | --- |
| ![阅读资料](images/mobile/阅读资料.png) | ![我的](images/mobile/我的.png) |

Captured on the iPhone 17 Pro simulator (iOS 26.5, Liquid Glass theme):

| 书库 |
| --- |
| ![书库](images/mobile/ios-书库.png) |

## Design system

The phone UI is quiet and neutral: hierarchy comes from spacing, the system sans
at regular and medium weights, and soft gray. Solid black or white blocks are
never used for emphasis, so the main action is a soft tint and selection is a
small pill or a raised segment. Book covers keep their own color unless 我的 →
黑白封面 is on; the bundled Song face is for reading text only.

- `Styles/MobileTokens.axaml`: light and dark tokens. Add colors here, never in
  a view.
- `Styles/MobileIcons.axaml` + `Controls/LineIcon`: the 24×24 icon set.
- `Styles/MobileTheme.axaml`: type scale and the button, chip, segment, row,
  cover and floating-card classes.
- Reader paper is 白 / 米 / 黑. 黑 is `ReaderTheme.Ink`, added to the shared enum
  and `ReaderPalette`; the desktop theme picker does not offer it yet.
- `MobilePreferencesStore` holds phone-only choices in
  `mobile-preferences.json`; library data stays in the database.

## iOS head

`Kkindle.Mobile.iOS` mirrors the Android head: `Main` publishes `MobileServices`
before UIKit starts, `AppDelegate` hosts `MobileApp`, `IOSSecretProtector` keeps
its key inside the app sandbox, and `IOSFileImportSource` copies documents
picked through the system picker into the cache. `IOSGlassTabBar` supplies the
native bar described below.

On iOS the shared shell switches to a Liquid Glass theme: translucent tokens and
an ambient gradient with blurred colour fields behind every page. The three
colour fields drift and breathe on their own slow cycles. `MobileApp.UseLiquidGlass()`
copies the glass values into the application's own resources (they outrank the
merged flat tokens), then loads `Styles/MobileLiquidGlassTheme.axaml`. Android
never loads either file.

The tab bar itself uses the **real system material on iOS 26**: `IOSGlassTabBar`
puts a native `UIVisualEffectView` with `UIGlassEffect` on the window above
Avalonia's view, with UIKit buttons inside it. The shell keeps its own bar
transparent so the grid still reserves the same space; taps on the native bar
call back into the shell and the two stay in sync. On iOS 15–25, where
`UIGlassEffect` does not exist, the Avalonia bar remains visible with its
frosted capsule, drifting highlight sweep and press feedback.

`TransformAnimator` registers the matrix interpolator that Avalonia 12 leaves
out, so keyframes can animate `RenderTransform` at all (used by the fallback
backdrop).

Intel Macs can only run Xcode 26, while the current .NET iOS workload targets
Xcode 27. The head therefore pins `ValidateXcodeVersion=false` and
`MtouchLink=SdkOnly`, which keeps the app building without iOS 27-only APIs.

```
dotnet build src/Kkindle.Mobile.iOS/Kkindle.Mobile.iOS.csproj -c Debug \
  -f net10.0-ios -p:RuntimeIdentifier=iossimulator-x64
xcrun simctl boot "iPhone 17 Pro"
open /Applications/Xcode.app/Contents/Developer/Applications/Simulator.app
xcrun simctl install booted src/Kkindle.Mobile.iOS/bin/Debug/net10.0-ios/iossimulator-x64/Kkindle.Mobile.iOS.app
xcrun simctl launch booted com.kingstacker.kkindle
```

iOS has no TTS adapter or system-bar tinting yet; both are optional in
`MobileServices`, so those features stay hidden while the rest of the shell
works.

## What is wired up

- `MobileApp` builds `ApplicationModules` from `MobileServices` (paths, secret
  protector, metadata service, format converter, import source) supplied by the
  platform head before Avalonia starts.
- `MobileShell` owns three tab roots (书库 / 阅读资料 / 我的), a push stack for
  书籍详情 and 阅读页, one shared bottom sheet and a toast. Android's back gesture
  is routed through `TopLevel.BackRequested`: it closes the sheet, then pops the
  stack, before the activity leaves the app.
- `LibraryPage` has two views under the 书库 / 收藏夹 masthead. 书库 has search,
  filters, sorting, grid and list views, a 继续阅读 row and import.
  `CoverCache` decodes covers off the UI thread.
- 收藏夹 shows each collection as a card fronted by `CoverStack`, a fan of its
  three newest covers (empty slots are drawn as faint ghosts). Long press, or
  ⋯ on `CollectionPage`, offers the desktop's actions: rename, clear, merge into
  another collection, and dissolve. `CollectionPage` adds books several at a
  time through `BookPickSheet` and removes one on long press. The automatic
  “未收藏” collection appears last, only while it holds books, and cannot be
  edited. `MobileCollections` holds the shared actions.
- `BookDetailPage` shows metadata and progress, and handles favorite, 加入收藏夹,
  rename, delete and sending to Kindle. `BookEditPage` edits the same fields as
  the desktop detail pane. In developer mode, 从豆瓣获取 searches Douban (with
  covers), lets the reader pick which fields to take, fills the form, and
  writes only on save.
- Developer mode: tap 我的 → 版本 five times (again to leave). As on desktop,
  where clicking the version five times does the same, it lasts for the current
  run and holds features outside the normal product: Douban lookup and
  Z-Library (on desktop also automatic Douban matching on import).
- `ZLibraryPage` searches Z-Library (format filter, covers, paging) and
  `ZLibraryDownloader` downloads into the library like the desktop app: import
  the file, then write the page's details (title, author, publisher, year,
  series, edition, ISBN, pages, language, description, cover) with
  `ZLibraryMetadata.Apply`. Books already in the library are not downloaded
  again. The account shares the desktop's protected settings file.
- `ReaderPage` is paginated. The chrome stays hidden while reading, but the
  system bars stay visible and take the paper color: hiding them reflows the
  page on every toggle and raises Android's full-screen confirmation. Side taps
  and swipes turn pages, a center tap toggles the chrome, and a long press hands
  the following drag to the host as a text selection for highlights, notes and
  copying. Gestures are classified in tunnel handlers so the shared hosts stay
  unchanged. Links, footnotes and PDF zoom (a zoomed page pans under the finger)
  are supported, for EPUB and PDF alike, plus whole-book search, reading aloud
  with the system TTS engine, an AI assistant over the visible page, and
  translation (AI when configured, otherwise Bing). Pinches are recognized in the
  reader's own tunnel handlers because the PDF surface takes each finger.
- Progress, bookmarks and annotations store chapters relative to the book, like
  the desktop app, because the rows sync between devices.
- `MaterialsPage` groups bookmarks, highlights and notes by book and opens the
  reader at the tapped location.
- `ReadingDataPage` is the desktop reading dashboard laid out for a phone:
  totals, stat cards, a tappable daily chart, recent and most-read books with
  covers, a stacked progress bar and medals with progress rings.
  `ReadingMedalCatalog` in Core is the one definition of medals and streaks.
- `SyncSettingsPage` edits the settings file the desktop app uses, and
  `MobileSync` is the single path for manual and automatic runs. Automatic sync
  runs on launch, when returning to the foreground and after leaving the reader.
  Large deletions need confirmation on the sync page and background runs never
  approve them; smaller deletions sync directly, as on desktop. Synced books
  arrive as metadata and their files download when first opened.
- `AiSettingsPage` and `KindleSettingsPage` edit the desktop's protected AI and
  Kindle e-mail settings. `BackgroundSyncJob` syncs hourly while the app is closed.
- The Android head uses .NET's managed HTTP handler (`UseNativeHttpHandler`
  false) because Android's own stack rejects WebDAV `PROPFIND`, and allows
  cleartext HTTP so self-hosted servers on the local network work.
- In the reader, edge drags are excluded from Android's back gesture so a
  selection starting at the margin does not leave the book.
- `AndroidSecretProtector` seals its key with the Android Keystore and migrates
  the plain key file older builds wrote.
- Reader tables are created lazily by `MobileShell.EnsureReaderDataAsync()`
  because the mobile shell has no startup phase that runs the schema pass the
  desktop `MainWindow` performs.

## What is not there yet

- Kindle device management over USB and MOBI/AZW3 *conversion*, which rely on
  desktop-only USB access and Calibre. Reading MOBI/AZW3 works.
- AI retrieval across the whole book: the phone sends the visible page only,
  because local embeddings (ONNX) are not shipped.

## Running the desktop preview

```
dotnet build src/Kkindle.Mobile.Desktop/Kkindle.Mobile.Desktop.csproj
src/Kkindle.Mobile.Desktop/bin/Debug/net10.0/Kkindle.Mobile.Desktop.exe
```

Preview data (library database, covers, keys) lives in
`%LOCALAPPDATA%\Kkindle\mobile-preview` so it never touches the desktop
library. To review the screens with real books, seed it from the command line:

```
src/Kkindle.Mobile.Desktop/bin/Debug/net10.0/Kkindle.Mobile.Desktop.exe --seed <book1> [<book2> ...]
```

`--sync <endpoint> <bucket> <accessKey> <secretKey>` makes the preview act as a
second device for one S3 sync, without saving the credentials. `--seed` runs the same import path the library page uses and exits. It uses the
same `PreviewSecretProtector` and `UnavailableBookFormatConverter` as the
preview, so MOBI/AZW3 imports will report a failure there.

To review a design change, walk every screen in light and dark and save PNGs:

```
src/Kkindle.Mobile.Desktop/bin/Debug/net10.0/Kkindle.Mobile.Desktop.exe --shots <directory>
```

The walk also writes `gestures.txt`, a pass/fail log of reader taps and swipes
driven through real routed pointer events. It opens every seeded book and
toggles a bookmark, so it changes the preview library's reading data.

## Building for Android

The Android head is not part of `Kkindle.sln`; build it from `Kkindle.Mobile.sln`
(Visual Studio) or directly:

```
dotnet workload install android          # needs an elevated shell, once
dotnet build src/Kkindle.Mobile.Android/Kkindle.Mobile.Android.csproj -f net10.0-android36.0
dotnet publish src/Kkindle.Mobile.Android/Kkindle.Mobile.Android.csproj -f net10.0-android36.0 -c Release
```

Release builds are fully AOT compiled: cold start on the emulator dropped from
about 6 s to about 3 s for roughly 10 MB of APK size. The ONNX native runtime is
excluded because the phone app has no embedding features.

If `ANDROID_HOME` points at a directory that does not exist, the SDK may have been
installed by a packaged app, whose `%LOCALAPPDATA%` writes are redirected under
`%LOCALAPPDATA%\Packages\<app>\LocalCache\Local`. Pass the real location with
`-p:AndroidSdkDirectory=<path>`.

Prerequisites the head assumes:

- JDK 17 and the Android SDK with platform API 36 (Android Studio default
  location is fine).
- `RuntimeIdentifiers` is `android-arm64;android-x64` — arm64 for devices,
  x64 for the emulator.
- The manifest currently only requests `INTERNET` and `ACCESS_NETWORK_STATE`.
  Document-provider access goes through `ACTION_OPEN_DOCUMENT`, which needs no
  storage permission, and picked files are copied into app-private storage
  because the library stores local paths.

## Verification checklist

Verified on the `Kkindle_Pixel` emulator (Android 15, x86_64, Release build):

- install over an existing library, launch, adaptive icon and splash screen
- `ACTION_OPEN_DOCUMENT` multi-select import of real EPUB and PDF files
- page turns by real touch, tables of contents, links, typography and paper
- highlights, notes, bookmarks and jumping back to them from 阅读资料
- PDF zoom and panning
- back key from the reader, sheets, the keyboard and the tab roots
- S3 connection, two-way sync with a second device, deletions and on-demand
  book download, against a local S3 mock; WebDAV sync against a local server;
  the hourly background job, forced while the app was closed
- whole-book search, reading aloud (pause, resume, stop, page and chapter
  follow), AI questions and translation against a local OpenAI-compatible mock
- PDF selection, highlights and notes; pinch zoom (desktop preview, two
  synthetic touch points; adb cannot pinch)
- sending to Kindle through a local SMTP server, with the book attached
- Douban search with covers, field selection and saving, against Douban itself
- Z-Library login, search, format filter, download with details written, and
  duplicate detection, against a local mock of its API (no real account was
  available)
- 收藏夹: create, add several books, remove by long press, rename, merge and
  dissolve, from both the library's long press and the collection page
- Keystore migration keeping stored credentials readable

Still needs a real device:

- arm64 behavior and performance; the APK contains both ABIs but only x86_64 ran
- a real S3 provider over HTTPS
- the large-deletion confirmation, which no test reached
- app suspension while a sync is running

The desktop preview cannot catch back-key, keyboard or system bar problems, so
treat `--shots` as a fast first pass and confirm mobile changes on the emulator.

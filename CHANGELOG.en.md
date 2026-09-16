# Changelog

All release changes are recorded here. The release workflow uses the matching
version section as the release notes, and the website uses this file alongside
the Chinese changelog when the language is English.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## 1.0.0 (2026-09-15)

### Added


### Fixed

- Adjusted the hit areas of the library location and sync status icons so enlarged icons no longer interfere with book-card selection.
- Fixed the contrast of selected menu text and arrows when a menu is open.

## 0.9.1 (2026-09-14)

### Added

- Cloud sync can now sync only book metadata and covers, or download full books during sync; book text remains available for on-demand download.
- Book cards now show book sync status and library location icons; each can be shown or hidden separately in Settings.

### Fixed

- Fixed missing files when sending cloud books by Kindle email, adding pinyin, or translating an entire book before the book text had been downloaded.
- Fixed interrupted first sync incorrectly marking books whose text was not downloaded as synced.
- Duplicate imports now show a clear message and keep the existing book files.

## 0.8.2 (2026-09-08)

### Fixed

- Fixed the left baseline of switch rows not aligning with regular settings rows.
- Fixed the pressed state of settings foldout headers causing a rectangular scale bounce.

### Improved

- The settings scrollbar is now shown while scrolling and hidden automatically after scrolling stops.
- Improved the expand and collapse transitions of settings panels to reduce the visual separation between content and card boundaries.
- Update packages now move directly into the exit and install confirmation flow after downloading.

## 0.8.0 (2026-09-06)

### Added

- Expanded the reading assistant, reading materials, Kindle resource management, and sync capabilities.
- Added long EPUB reading caches and chapter indexing to reduce repeated parsing when opening large books.

### Fixed

- Fixed special Chinese characters stored as small images in EPUB files being treated as standalone image blocks and placed on separate lines.
- Fixed multiple edge cases in chapter preparation, search, annotations, footnotes, and material export in the EPUB reader.

### Improved

- Parallelized EPUB chapter preparation, resource cleanup, and table-of-contents processing for books with many chapters.
- Improved the library, reader, device resource transfer, and local data cache workflows.

## 0.7.8 (2026-09-04)

### Fixed

- Fixed clicking read aloud on an image page failing to jump to the next page with text.
- Fixed ordinary numbered note markers in the body only supporting hover on the first group; repeated note regions in one chapter are now supported.
- Fixed consecutive chapter titles being split across pages or lines; titles now stay together as “Chapter X Title”.
- Fixed an extra vertical rule appearing in quote block layout.

### Improved

- Improved read-aloud state, the four spectrum animations, and the floating controls with automatic hiding.
- Improved text filtering for read aloud by removing footnote numbers before speech.

## 0.7.7 (2026-09-02)

### Improved

- Improved the handoff between Kreader page-turn animations and chapter changes to reduce jumps and flicker.
- Improved table-of-contents scrolling and current-chapter positioning for more stable navigation in long tables of contents.

## 0.7.6 (2026-09-01)

### Added

- The reader table of contents now follows the original EPUB hierarchy with indentation, expansion, and collapse, and automatically expands the branch containing the current chapter when a book opens.

### Fixed

- Fixed precedence, duplicate entries, and lost hierarchy when complex EPUB files contain NCX, EPUB 3 navigation pages, and guide entries together.
- Fixed chapter links with fragments, query parameters, or parent-relative paths failing to locate the chapter.
- Fixed malformed XHTML or ZIP structures in some EPUB chapters preventing an entire book from opening; chapters that cannot be repaired now fall back to text.

### Improved

- Improved EPUB table-of-contents cleanup, chapter title generation, and ordering to reduce cover pages, footnotes, and duplicate book titles in navigation.
- Updated the reading-content cache format so old caches are rebuilt automatically when processing rules change.

## 0.7.5 (2026-09-01)

### Fixed

- Fixed font and dictionary caches not refreshing after Kindle resources were imported, deleted, or disconnected, which left stale files in the UI.
- Fixed unstable Windows WPD resource transfers that could open the system copy dialog.
- Fixed long dictionary popups being impossible to view completely or return from to the reader.
- Fixed exporting books from a Kindle showing unclear errors when Calibre was missing or a single-book conversion failed.

### Improved

- Improved the progress, retry, and device-state feedback for Kindle resource transfers.
- Improved device resource cache invalidation, refresh, and persistence to reduce repeated scans and stale data.
- Improved Kindle resource management and reading-material export feedback so disconnected-device states are clearer.

## 0.7.4 (2026-08-31)

### Fixed

- Fixed keywords being missing from full-book search result summaries.
- Fixed clicking a result with multiple hits in one chapter always jumping to the first hit.
- Fixed location drift caused by differences between search results, body highlights, and whitespace in the original EPUB.
- Fixed duplicate EPUB table-of-contents entries, footnotes being added to the table of contents, and incorrect hierarchy display.

### Improved

- The EPUB table of contents now shows only the cover, table-of-contents pages, and chapters, while nested entries can be shown directly.
- Improved automatic table-of-contents scrollbar hiding and ordering of full-book search results by reading order.

## 0.7.2 (2026-08-31)

### Fixed

- Fixed the first launch showing the main window before the onboarding wizard; the wizard now opens directly on first entry.
- Fixed wizard descriptions, form fields, and the current selection not sharing one left alignment.
- Fixed the upgrade package completing with a silent exit and opening the new version's main window immediately.

### Improved

- Upgrade packages now persist an “update ready” state and show an explicit installation confirmation when the app exits.
- Settings are loaded and the language is applied before startup so the first onboarding screen uses the system language.

## 0.7.1 (2026-08-31)

### Fixed

- Fixed computer-library filters being truncated in English and unified the widths and spacing of filter and sort controls.
- Fixed the selected sidebar dot not appearing round enough.

### Improved


## 0.7.0 (2026-08-30)

### Added

- Added a native custom-drawn reading engine (HarfBuzz + Skia) for EPUB layout, pagination, and drawing, replacing the WebView rendering pipeline with better vertical and horizontal layout quality and performance.
- Added horizontal scrolling with continuous scrolling, smooth wheel and arrow-key movement, automatic continuation at chapter boundaries, draggable scrollbar positioning, and saved progress.
- Added a horizontal two-page mode with cross-page stepping and correct selection, annotations, search, and footnote mapping.
- Added inverted black-and-white search-hit highlighting for full-book search and in-page Ctrl+F search.
- Restored bookmark corner markers and click handling in the native EPUB engine; Ctrl+B remains supported.
- Added in-page and full-book search highlighting so a jumped-to match is easy to find.

### Fixed

- Fixed a crash caused by scrollbar synchronization triggering invalidation from the rendering channel.
- Fixed the global paragraph-indentation switch not taking effect for the current chapter; disabling it now removes indentation immediately while preserving the reading position.
- Fixed full-book search results appearing not to navigate; the body now marks the matching keyword.
- Fixed scroll mode incorrectly drawing pages side by side.

### Improved

- Continued improving the cost of chapter changes and the page-turn animation pipeline; the animation player is now fully native.

## 0.6.0

- Stability fixes for the vertical-reading layout; global paragraph indentation and vertical-writing toggles are now unified global preferences.
- Improved Douban metadata matching, cover handling, and library icons.

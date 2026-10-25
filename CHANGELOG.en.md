# Changelog

All release changes are recorded here. The release workflow uses the matching
version section as the release notes, and the website uses this file alongside
the Chinese changelog when the language is English.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## 1.1.1 (2026-10-25)

### Added

- Automatically search the web when book excerpts are insufficient, external facts need verification, or current information is required, with a fallback search provider and clickable source links.
- Adjust the table-of-contents font size independently in Layout; default body text to 140% and maximum content width to 1200px.

### Improved

- Prioritize the current reading position and selection in AI answers, supplement with model knowledge when needed, and distinguish book citations from external sources.
- Increase AI answer text to size 16, add disclosure triangles to reasoning, and show the selected sidebar tab in bold.
- Clarify that Quick Overview samples across chapters, separate semantic model readiness from the retrieval method, and refine streaming and reasoning layout.
- Automatically open task history for update downloads, show progress, prevent duplicate requests, and retain cancellation and failure results.

### Fixed

- Allow supplementary explanations when retrieved excerpts are unrelated, and preserve request context when retrying.
- Clearly report unavailable or empty web searches without claiming that verification succeeded.
- Restore the update button state promptly after requests finish.

## 1.1.0 (2026-10-25)

### Added

- Improved native PDF reading, table-of-contents and page navigation, with annotation cards that follow scrolling and zooming, and selectable, deletable highlights.
- Save reading translations as annotations and improve line-wrap and paragraph handling for selected PDF text.

### Improved

- Redesigned reading settings with consistent capsule switches, font selection and icon actions, showing layout and PDF annotation options appropriate to each format.
- Added an Updates group to About & Updates with consistent headings, full-row interactions and hover feedback.
- Unified conversion progress, results and cancellation in task history, and refined device feedback and task popups.
- Preserve local dictionary HTML styling and refine AI conversations, translation actions and popup dismissal.

### Fixed

- Bound update checks with overall and per-request timeouts for prompt feedback when the network is unavailable.
- Restore PDF cover extraction in Kindle device libraries and retry PDF metadata skipped by older caches.
- Fix PDF opening from long paths, reading state and same-page table-of-contents navigation.

## 1.0.14 (2026-10-22)

### Added

- Cross-book notes, batch management, duplicate detection, and smart shelves.
- Unified background task history with live hover details for stages, books, and progress.

### Improved

- Consistent page titles, search fields, and action placement; tighter reading statistics and sidebar layouts.
- Compact library filters with connected dropdown outlines and clearer English and Chinese defaults.
- Faster large libraries, device scans, reader layout, dictionary lookup, and incremental synchronization.
- Refined settings, reading themes, sync details, and keyboard shortcuts.

### Fixed

- PDF reading state restoration, title and file imports, device book operations, and update installation.
- Improved sync compatibility and preservation of deleted-book metadata.

## 1.0.13 (2026-10-13)

### Improved

- Use an animated Z-Library details sidebar that closes when clicking the selected book again or a blank area; refine search, pagination, and sending buttons.
- Check the computer library before downloading duplicate books and enrich imported books with online metadata and covers while preserving existing manual edits.

### Fixed

- Fix the first book deletion after connecting a device requiring a second attempt.
- Preserve pagination state and search criteria after Z-Library request failures, with retries for temporary network errors.

## 1.0.12 (2026-10-12)

### Added

- Open DRM-free legacy MOBI/PRC books directly without Calibre conversion.
- Improve Kindle dictionary import with progress, results, and reimport after removal.
- Optionally synchronize annotation deletion when removing books; annotations are preserved by default.

### Improved

- Use compact local dictionary and Wikipedia tabs, with lookup popups positioned relative to selected text.
- Unify icon buttons for reading materials, trash, format conversion, and model management; refine font import and reading data reset layouts.
- Keep settings descriptions visible below headings and refine synchronization feedback and reading material filters.

### Fixed

- Prevent footnote anchors inside headings from forcing premature page breaks and leaving large blank areas.
- Update annotation retention and missing-format cleanup tests to cover the current deletion behavior.

## 1.0.11 (2026-10-11)

### Added

- Added first-run onboarding to configure runtime dependencies and install or skip optional components.
- Added reading medal progress for time spent reading, books finished, and annotations.
- Added a Simplified Chinese user guide covering installation, the library, reading, Kindle, backup and sync, settings, and troubleshooting, with key screenshots.

### Improved

- Refined Reading Materials search, source filters, and book alignment, and removed the redundant “Open Douban Source” button from the candidate picker.
- Refined the About & Updates layout, placed the update check action on the right, and improved version and developer information spacing.
- Improved reader heading detection and search underline presentation, and clarified sync status feedback.

### Fixed

- Prevented Chinese footnotes from appearing in EPUB tables of contents and enabled double-click navigation to footnote definitions.
- Restored import compatibility for older MOBI/PRC Kindle dictionaries.

## 1.0.10 (2026-10-08)

### Improved

- Unified removal of individual books, selected books, and the last format file, with options to keep local files or move them to the trash.
- Simplified generated title covers by removing the water ripple decoration.

### Fixed

- Improved TXT chapter detection for numbered prefixes and chapter titles containing commas.
- Fixed exceptions when checking covers in damaged or invalid EPUB files.
- Book removal can preserve local files, with clearer messages about their location and recovery from the trash.

## 1.0.9 (2026-10-05)

### Added

- Import TXT files with automatic UTF-8, Unicode, and GB18030 detection, chapter and volume recognition, and EPUB conversion without Calibre.
- Read DRM-free AZW3 files directly using the built-in AZW3/KF8 parser, without Calibre.
- Automatically generate title covers for books without cover images.
- Added reading medals with progress toward reading time, finished books, and annotation milestones.

### Improved

- Simplified book details and format conversion controls, and clarified file menu actions.
- Updated Chinese and English documentation for TXT import and native AZW3 reading.

### Fixed

- Improved import, reading cache, and synchronization metadata handling for the new formats.

## 1.0.8 (2026-10-04)

### Improved

- Refined reader menus, typography, zen window controls, and theme controls.
- Improved reading dashboard navigation, book detail panel animation, onboarding, and reader startup.
- Development updates prefer the self-hosted mirror.
- Refreshed documentation and interface screenshots.

### Fixed

- Fixed overlapping text during page transitions.
- Preserved nested table of contents entries and simplified book titles.

## 1.0.7 (2026-10-02)

### Added

- Toggle developer mode with five clicks on the version number, with capsule notifications on entry and exit.
- Added Z-Library library access, account settings, and corresponding MCP tools in developer mode.
- Added a separate software proxy setting for Calibre/KFX Input downloads and app updates, with download percentages.

### Improved

- Unified sidebar and feature-page icon sizes and strokes; selected child navigation items now show a small dot.
- Simplified settings and About sections by removing outer frames and aligning headings; help text and the donation QR code now appear on hover.
- Improved reflection editor and expandable settings layouts.
- Development builds derive their version from the latest stable release and increment their build number; master updates automatically tag and publish stable releases without duplicate releases.

### Fixed

- Removed the developer-mode click timeout and count every left mouse click.

## 1.0.6 (2026-09-29)

### Added

- Added dictionary and Wikipedia lookup popups to the reader.

### Fixed

- Fixed data consistency and recovery issues across sync, library import, reader annotations, MCP tools, and Kindle transfers.
- Strengthened path and integrity checks for EPUB, MCP, device-file, and model-download handling.
- Fixed the MCP server keeping files in use during installer updates.

### Improved

- Improved S3/WebDAV file matching, interrupted-transfer recovery, concurrent access, and deletion safeguards.
- Improved reader lookup popup positioning so it stays with the selected page in two-page reading.
- Moved MCP server settings to the “Reading & AI” category and collapsed the section by default.
- Added the official website link to the top of both README files.

## 1.0.2 (2026-09-19)

### Fixed

- Fixed device-library scanning waiting for all metadata enrichment before showing any books; the enumerated list now appears progressively.
- Fixed the device library not being refreshed after the complete device-send task, so newly sent and updated books appear in the latest list.
- Fixed the bottom-right task spinner not continuously indicating active send, delete, import, export, backup, and related tasks.
- Fixed rubber-band selections remaining after a task completed or the user switched between the computer and device libraries.
- Fixed state cleanup for reader annotation editing, deletion, and reading-reflection previews.

### Improved

- Improved library rubber-band auto-scroll, selection state handling, and navigation blocking during active tasks.
- Improved reader annotation controls, annotation anchoring, and reading-reflection group previews.

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

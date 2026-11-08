// Type definitions for the Kkindle plugin API (window.kkindle), apiVersion 1.
//
// Use in a plugin page or provider script:
//   /// <reference path="./kkindle.d.ts" />
// or copy this file into your project and add it to tsconfig "include".
//
// Every method returns a Promise that rejects with an Error whose message
// can be shown to the user. Feature-test optional APIs with kkindle.has()
// rather than comparing app versions.

export {};

declare global {
  interface Window {
    readonly kkindle: Kkindle.Api;
  }
  const kkindle: Kkindle.Api;

  namespace Kkindle {
    type Guid = string;
    type Theme = "light" | "dark";
    type ReadingStatus = "unread" | "reading" | "finished";

    // ---- context and capabilities ------------------------------------

    interface Selection {
      text: string;
      startOffset: number;
      endOffset: number;
      prefix: string;
      suffix: string;
      bookId: Guid | null;
      fileId: Guid | null;
      chapterIndex: number | null;
      chapterPath: string | null;
      /** Reader-internal position; store it as-is. */
      position: unknown | null;
    }

    interface Context {
      pluginId: string;
      pluginVersion: string;
      /** Contribution id that opened the page, or "dictionary:<id>" etc. in provider scripts. */
      action: string | null;
      /** Books selected when the window opened; without libraryAll only these are accessible. */
      bookIds: Guid[];
      /** UI language, e.g. "zh-CN" or "en-US". */
      language: string;
      appVersion: string;
      theme: Theme;
      selection: Selection | null;
      developerMode: boolean;
      /** True in provider and background scripts, which have no window or DOM. */
      headless: boolean;
    }

    interface Capabilities {
      apiVersion: number;
      appVersion: string;
      headless: boolean;
      /** Every bridge method this host implements, e.g. "book.getChapterText". */
      methods: string[];
      permissions: {
        library: "read" | "write" | null;
        libraryAll: boolean;
        network: boolean;
        import: boolean;
        translation: boolean;
        pinyin: boolean;
        storage: boolean;
        secrets: boolean;
        content: boolean;
        ai: boolean;
        readingWrite: boolean;
        bookFiles: boolean;
      } | null;
    }

    // ---- library ------------------------------------------------------

    interface Book {
      id: Guid;
      title: string;
      authors: string;
      series: string | null;
      seriesIndex: number | null;
      description: string | null;
      publisher: string | null;
      publishDate: string | null;
      isbn: string | null;
      /** Free text, e.g. "320" or "约 300 页". */
      pageCount: string | null;
      binding: string | null;
      rating: number | null;
      ratingCount: number | null;
      /** Comma-separated. */
      tags: string;
      category: string;
      hasCover: boolean;
      favorite: boolean;
      readingStatus: ReadingStatus;
      collectionIds: Guid[];
      updatedAt: string;
      /** Upper-case formats, e.g. ["EPUB", "PDF"]. */
      formats: string[];
    }

    type BookPatch = Partial<Pick<Book,
      "title" | "authors" | "series" | "seriesIndex" | "description" | "publisher" | "publishDate"
      | "isbn" | "pageCount" | "binding" | "tags" | "category" | "rating" | "ratingCount">>;

    interface SearchOptions {
      query?: string | null;
      author?: string | null;
      tag?: string | null;
      format?: string | null;
      category?: string | null;
      collectionId?: Guid | null;
      readingStatus?: ReadingStatus | null;
      favorite?: boolean | null;
      sort?: "updated" | "title" | "author" | "created" | "progress" | null;
      pageIndex?: number;
      /** 1–200, default 50. */
      pageSize?: number;
    }

    interface SearchPage {
      books: Book[];
      totalCount: number;
      pageIndex: number;
      pageSize: number;
    }

    interface Collection {
      id: Guid;
      name: string;
      bookCount: number;
    }

    interface ImportResult {
      items: { sourcePath: string; succeeded: boolean; message: string; bookId: Guid | null; added: boolean }[];
      successCount: number;
      addedCount: number;
    }

    interface FileChunk {
      fileId: Guid;
      name: string;
      format: string;
      size: number;
      offset: number;
      /** Base64 of the bytes read. */
      data: string;
      hasMore: boolean;
    }

    interface TranslateOptions {
      provider?: "ai" | "bing" | "google";
      sourceLanguage?: string;
      targetLanguage?: string;
      translated?: boolean;
      bilingual?: boolean;
      aiRequestsPerMinute?: number;
      googleProxyAddress?: string;
      resume?: boolean;
    }

    interface PinyinOptions {
      engine?: "dotnet-g2p" | "g2pw";
      outputStyle?: "tone-marked" | "tone-number" | "none";
      aiReview?: boolean;
      resume?: boolean;
    }

    interface LibraryApi {
      /** Books opened with the window, or the given ids. Needs permissions.library. */
      getBooks(ids?: Guid[]): Promise<Book[]>;
      /** Whole-library search. Needs permissions.libraryAll. */
      search(options?: SearchOptions): Promise<SearchPage>;
      getBook(id: Guid): Promise<Book | null>;
      getCollections(): Promise<Collection[]>;
      createCollection(name: string): Promise<Collection>;
      addToCollection(bookId: Guid, collectionId: Guid): Promise<{ bookId: Guid; collectionId: Guid; member: boolean }>;
      removeFromCollection(bookId: Guid, collectionId: Guid): Promise<{ bookId: Guid; collectionId: Guid; member: boolean }>;
      /** Paths must come from ui.pickFiles in this window. Needs permissions.import. */
      importFiles(paths: string[]): Promise<ImportResult>;
      /** Needs permissions.library = "write". */
      updateBook(id: Guid, patch: BookPatch): Promise<Book>;
      /** data: URL, or null when the book has no cover. */
      getCover(id: Guid): Promise<string | null>;
      /** "image/png;base64,..." or a data: URL. */
      setCover(id: Guid, data: string): Promise<unknown>;
      /** Raw bytes of a book file in chunks (default 4 MB, max 8 MB). Needs permissions.bookFiles. */
      readFile(bookId: Guid, options?: { fileId?: Guid; offset?: number; length?: number }): Promise<FileChunk>;
      /** Needs permissions.import and network access to the URL. */
      importDownload(url: string, options?: { fileName?: string; headers?: Record<string, string> })
        : Promise<{ fileName: string; imported: boolean; added: boolean; message: string; bookId: Guid | null } | { cancelled: true; fileName: string }>;
      translateBook(bookId: Guid, options?: TranslateOptions): Promise<Record<string, unknown>>;
      annotatePinyin(bookId: Guid, options?: PinyinOptions): Promise<Record<string, unknown>>;
      cancelTask(): Promise<unknown>;
    }

    // ---- book content (permissions.content) ---------------------------

    interface Chapter {
      index: number;
      title: string;
      /** Path relative to the book root; matches annotation chapterPath. */
      path: string;
      /** Character length of the chapter text. */
      length: number;
    }

    interface Outline {
      bookId: Guid;
      fileId: Guid;
      format: string;
      chapters: Chapter[];
      toc: { title: string; chapterIndex: number; level: number; fragment: string | null }[];
    }

    interface ChapterText {
      chapterIndex: number;
      title: string;
      path: string;
      text: string;
      offset: number;
      /** Full length of the chapter, not of this slice. */
      length: number;
      hasMore: boolean;
    }

    interface BookApi {
      /** Reading order and table of contents of an EPUB/AZW3/MOBI file. */
      getToc(bookId: Guid, options?: { fileId?: Guid }): Promise<Outline>;
      /** Chapter text exactly as the reader sees it; offsets line up with annotations. */
      getChapterText(bookId: Guid, chapterIndex: number,
        options?: { fileId?: Guid; offset?: number; maxLength?: number }): Promise<ChapterText>;
    }

    // ---- reading data -------------------------------------------------

    interface Annotation {
      id: Guid;
      bookId: Guid;
      fileId: Guid;
      chapterPath: string;
      startOffset: number;
      endOffset: number;
      selectedText: string;
      note: string;
      color: string;
      underlineStyle: "solid" | "wavy" | "dashed" | "highlight";
      /** Present in getAnnotations results. */
      createdAt?: string;
      updatedAt?: string;
    }

    interface Bookmark {
      id: Guid;
      bookId: Guid;
      fileId: Guid;
      chapterPath: string;
      chapterIndex: number;
      title: string;
      quote: string;
      createdAt?: string;
    }

    interface ReadingApi {
      getProgress(bookId: Guid): Promise<{ bookId: Guid; title: string; files: Record<string, unknown>[] }>;
      getAnnotations(bookId: Guid, options?: { limit?: number }): Promise<{ bookId: Guid; annotations: Annotation[] }>;
      getBookmarks(bookId: Guid, options?: { limit?: number }): Promise<{ bookId: Guid; bookmarks: Bookmark[] }>;
      getStats(bookId: Guid): Promise<{ bookId: Guid; files: Record<string, unknown>[] }>;
      /** Needs permissions.readingWrite (and library). Offsets come from book.getChapterText. */
      addAnnotation(bookId: Guid, annotation: {
        fileId?: Guid;
        chapterIndex: number;
        startOffset: number;
        endOffset: number;
        note?: string;
        /** #RRGGBB */
        color?: string;
        style?: "solid" | "wavy" | "dashed" | "highlight";
      }): Promise<Annotation>;
      addBookmark(bookId: Guid, bookmark: {
        fileId?: Guid;
        chapterIndex: number;
        offset?: number;
        title?: string;
        quote?: string;
      }): Promise<Bookmark>;
    }

    // ---- AI (permissions.ai) ------------------------------------------

    interface AiStatus {
      available: boolean;
      provider: string | null;
      model: string | null;
      /** Why AI is unavailable (disabled, not configured, offline). */
      reason: string | null;
    }

    interface AiApi {
      status(): Promise<AiStatus>;
      /** Completes with the user's configured AI service. Resolves to the reply text. */
      complete(prompt: string, options?: {
        system?: string;
        history?: { role: "user" | "assistant"; content: string }[];
      }): Promise<string>;
    }

    // ---- network, storage, secrets, ui, events ------------------------

    interface FetchResponse {
      status: number;
      ok: boolean;
      url: string;
      contentType: string | null;
      headers: Record<string, string>;
      /** Text, or base64 when responseType is "base64". */
      body: string;
    }

    interface HttpApi {
      fetch(url: string, options?: {
        method?: "GET" | "POST" | "HEAD";
        headers?: Record<string, string>;
        body?: string | null;
        responseType?: "text" | "base64";
      }): Promise<FetchResponse>;
    }

    interface StorageApi {
      get<T = unknown>(key: string): Promise<T | null>;
      set(key: string, value: unknown): Promise<unknown>;
      remove(key: string): Promise<unknown>;
    }

    interface SecretsApi {
      get(key: string): Promise<string | null>;
      set(key: string, value: string | null): Promise<unknown>;
      remove(key: string): Promise<unknown>;
    }

    interface UiApi {
      close(): Promise<unknown>;
      notify(message: string): Promise<unknown>;
      setTitle(title: string): Promise<unknown>;
      openExternal(url: string): Promise<unknown>;
      pickFiles(options?: { multiple?: boolean; extensions?: string[] }): Promise<{ files: { name: string; path: string }[] }>;
      saveFile(fileName: string, content: { text?: string; base64?: string }): Promise<{ name: string; path: string } | null>;
      copyText(text: string): Promise<unknown>;
      readText(): Promise<{ text: string } | null>;
    }

    interface EventMap {
      "library.changed": Record<string, never>;
      "reading.changed": { kind: string };
      "theme.changed": { theme: Theme; appTheme: string; paper: string; ink: string; mutedInk: string; accent: string };
      "import.progress": { stage: "download" | "import"; downloadedBytes: number; totalBytes: number | null; percent: number | null };
      "task.progress": {
        task: "translation" | "pinyin" | "pinyin-model";
        percent: number | null;
        stage: string;
        message: string;
        processed: number;
        total: number;
        indeterminate: boolean;
      };
    }

    interface EventsApi {
      /** Returns an unsubscribe function. */
      on<K extends keyof EventMap>(name: K, handler: (payload: EventMap[K]) => void): () => void;
      on(name: string, handler: (payload: unknown) => void): () => void;
    }

    interface ReaderApi {
      /** Closes the plugin window and opens the book, optionally at a chapter and character offset. */
      open(bookId: Guid, options?: {
        fileId?: Guid;
        chapterIndex?: number;
        chapterPath?: string;
        offset?: number;
      }): Promise<unknown>;
    }

    // ---- providers (headless scripts) ---------------------------------

    interface DictionarySense {
      text?: string;
      definition?: string;
      pos?: string;
      partOfSpeech?: string;
      examples?: string[];
    }

    interface DictionaryEntry {
      headword?: string;
      word?: string;
      phonetic?: string;
      pronunciation?: string;
      senses?: (string | DictionarySense)[];
      definition?: string;
      note?: string;
    }

    /** A plain string, one entry, an array of entries or { entries }. */
    type DictionaryResult = string | DictionaryEntry | DictionaryEntry[] | { entries: DictionaryEntry[] } | null;
    type DictionaryHandler = (term: string, options: { language: string }) => DictionaryResult | Promise<DictionaryResult>;

    type TranslationResult = string | { text: string } | null;
    type TranslatorHandler = (text: string, options: { target: string; language: string })
      => TranslationResult | Promise<TranslationResult>;

    interface BackgroundRun {
      reason: "startup" | "interval";
      /** ISO time of the previous run, or null. */
      lastRun: string | null;
      lastSucceeded: boolean | null;
    }
    type BackgroundHandler = (run: BackgroundRun) => unknown | Promise<unknown>;

    interface RegisterApi {
      /** Registers contributes.dictionaries[id]. Call at the top level of the provider script. */
      dictionary(id: string, handler: DictionaryHandler): void;
      /** Registers contributes.translators[id]. */
      translator(id: string, handler: TranslatorHandler): void;
      /** Registers contributes.background. */
      background(handler: BackgroundHandler): void;
    }

    interface Api {
      readonly context: Readonly<Context>;
      readonly capabilities: Readonly<Capabilities>;
      /** True when this host implements the bridge method, e.g. kkindle.has("ai.complete"). */
      has(method: string): boolean;
      readonly register: RegisterApi;
      readonly book: BookApi;
      readonly ai: AiApi;
      readonly reader: ReaderApi;
      readonly library: LibraryApi;
      readonly pinyin: { status(): Promise<{ g2pwInstalled: boolean }>; downloadModel(): Promise<unknown> };
      readonly reading: ReadingApi;
      readonly http: HttpApi;
      readonly network: { requestAccess(origin: string): Promise<{ granted: boolean; host: string }> };
      readonly secrets: SecretsApi;
      readonly events: EventsApi;
      readonly storage: StorageApi;
      readonly ui: UiApi;
    }
  }
}

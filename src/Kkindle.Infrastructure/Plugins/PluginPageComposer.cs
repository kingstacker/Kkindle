using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kkindle.Core;

namespace Kkindle.Infrastructure.Plugins;

/// <summary>
/// Application colours handed to plugin pages as CSS variables. Values must
/// be CSS colours (<c>#RRGGBB</c>); the accent set mirrors the active app
/// palette so plugin buttons hover and press like the main window's.
/// </summary>
public sealed record PluginTheme(
    bool IsDark,
    string Paper,
    string Ink,
    string MutedInk,
    string Hairline,
    string Pressed,
    string SoftHover,
    string? StrongBorder = null,
    string? Accent = null,
    string? AccentHover = null,
    string? AccentPressed = null,
    string? OnAccent = null,
    string? Danger = null)
{
    public static PluginTheme Light { get; } = new(
        false, "#FFFFFF", "#000000", "#5A5A5A", "#E2E2E2", "#D9D9D9", "#F2F2F2",
        "#BFBFBF", "#000000", "#333333", "#1A1A1A", "#FFFFFF", "#A7342F");
}

/// <summary>
/// The reader selection a page was opened from, when the page is a reader
/// action. Offsets are character offsets inside the current chapter.
/// </summary>
public sealed record PluginSelectionContext(
    string Text,
    int StartOffset,
    int EndOffset,
    string Prefix,
    string Suffix,
    Guid? BookId,
    Guid? FileId,
    int? ChapterIndex,
    string? ChapterPath,
    ReaderContentPosition? Position);

/// <summary>Everything a page learns about the app when it opens.</summary>
public sealed record PluginPageContext(
    string PluginId,
    string PluginVersion,
    string? ActionId,
    IReadOnlyList<Guid> BookIds,
    string Language,
    string AppVersion,
    PluginSelectionContext? Selection = null,
    bool DeveloperMode = false)
{
    /// <summary>True for provider and background scripts, which have no window or document.</summary>
    public bool Headless { get; init; }
}

/// <summary>
/// Turns a plugin page into one self-contained HTML document: relative
/// scripts, stylesheets and images are inlined from the plugin folder, and a
/// restrictive Content-Security-Policy, the theme stylesheet and the
/// <c>window.kkindle</c> bridge are injected ahead of the plugin's own code.
/// Pages can therefore only reach the network through the host bridge.
/// </summary>
public static partial class PluginPageComposer
{
    public const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; " +
        "img-src data: blob:; font-src data:; media-src data: blob:; connect-src 'none'; " +
        "frame-src 'none'; object-src 'none'; form-action 'none'; base-uri 'none'";

    private const long MaximumInlinedBytes = 16L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Compose(InstalledPlugin plugin, string pagePath, PluginPageContext context, PluginTheme theme)
    {
        var path = plugin.ResolveFile(pagePath);
        var html = File.ReadAllText(path);
        var normalizedPage = PluginPackage.NormalizeEntryPath(pagePath);
        var slash = normalizedPage.LastIndexOf('/');
        var pageDirectory = slash < 0 ? string.Empty : normalizedPage[..slash];
        long inlined = 0;

        string ReadAsset(string reference, bool binary, out string mediaType)
        {
            var file = plugin.ResolveFile(CombineRelative(pageDirectory, reference));
            mediaType = MediaTypeFor(file);
            var info = new FileInfo(file);
            if (!info.Exists) throw new PluginPackageException($"插件页面引用的文件“{reference}”不存在。");
            inlined += info.Length;
            if (inlined > MaximumInlinedBytes) throw new PluginPackageException("插件页面引用的资源过大。");
            return binary
                ? Convert.ToBase64String(File.ReadAllBytes(file))
                : File.ReadAllText(file);
        }

        // Script blocks are masked first: their contents may legitimately
        // contain markup strings ("<img src=...>" in a template literal) that
        // must not be mistaken for real elements.
        var scripts = new List<string>();
        html = ScriptBlockRegex().Replace(html, match =>
        {
            scripts.Add(match.Value);
            return $"@@KKSCRIPT{scripts.Count - 1}@@";
        });
        html = StylesheetRegex().Replace(html, match =>
        {
            var source = WebUtility.HtmlDecode(match.Groups["href"].Value);
            if (IsExternal(source)) return string.Empty;
            var css = ReadAsset(source, binary: false, out _);
            return $"<style>{css.Replace("</style", "<\\/style", StringComparison.OrdinalIgnoreCase)}</style>";
        });
        html = ImageRegex().Replace(html, match =>
        {
            var source = WebUtility.HtmlDecode(match.Groups["src"].Value);
            if (IsExternal(source) || source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return match.Value;
            var data = ReadAsset(source, binary: true, out var mediaType);
            return match.Value.Replace(match.Groups["src"].Value, $"data:{mediaType};base64,{data}");
        });

        for (var index = 0; index < scripts.Count; index++)
        {
            var block = scripts[index];
            var source = ScriptRegex().Match(block);
            if (source.Success)
            {
                var reference = WebUtility.HtmlDecode(source.Groups["src"].Value);
                block = IsExternal(reference)
                    ? string.Empty
                    : $"<script>{EscapeScript(ReadAsset(reference, binary: false, out _))}\n</script>";
            }
            html = html.Replace($"@@KKSCRIPT{index}@@", block, StringComparison.Ordinal);
        }

        var head = new StringBuilder()
            .Append("<meta charset=\"utf-8\">")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"").Append(ContentSecurityPolicy).Append("\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
            .Append("<style id=\"kkindle-theme\">").Append(BuildThemeCss(theme)).Append("</style>")
            .Append("<script>").Append(EscapeScript(BuildBridgeScript(context, theme, plugin.Manifest))).Append("\n</script>")
            .ToString();

        var headMatch = HeadRegex().Match(html);
        if (headMatch.Success)
            return html.Insert(headMatch.Index + headMatch.Length, head);
        var htmlMatch = HtmlTagRegex().Match(html);
        if (htmlMatch.Success)
            return html.Insert(htmlMatch.Index + htmlMatch.Length, $"<head>{head}</head>");
        return $"<!DOCTYPE html><html><head>{head}</head><body>{html}</body></html>";
    }

    /// <summary>
    /// The shared plugin stylesheet. It reproduces the main window's control
    /// language — square corners, black filled primary and outlined
    /// secondary buttons, underlined text fields, boxed selects, line-and-dot
    /// switches and the old-style Song typeface — so plugin pages read as part
    /// of the app rather than as web pages.
    /// </summary>
    public static string BuildThemeCss(PluginTheme theme)
    {
        var ink = theme.Ink;
        var paper = theme.Paper;
        var check = SvgDataUrl($"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'><path d='M3.5 8.2l3 3 6-6.4' fill='none' stroke='{paper}' stroke-width='1.6'/></svg>");
        var chevron = SvgDataUrl($"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 12 12'><path d='M2.5 4.5l3.5 3.5 3.5-3.5' fill='none' stroke='{ink}' stroke-width='1.2'/></svg>");
        return $$"""
        :root {
          color-scheme: {{(theme.IsDark ? "dark" : "light")}};
          --kk-paper: {{paper}};
          --kk-ink: {{ink}};
          --kk-muted: {{theme.MutedInk}};
          --kk-hairline: {{theme.Hairline}};
          --kk-pressed: {{theme.Pressed}};
          --kk-hover: {{theme.SoftHover}};
          --kk-strong-border: {{theme.StrongBorder ?? theme.MutedInk}};
          --kk-accent: {{theme.Accent ?? ink}};
          --kk-accent-hover: {{theme.AccentHover ?? theme.Accent ?? ink}};
          --kk-accent-pressed: {{theme.AccentPressed ?? theme.Accent ?? ink}};
          --kk-on-accent: {{theme.OnAccent ?? paper}};
          --kk-danger: {{theme.Danger ?? (theme.IsDark ? "#EAA59B" : "#A7342F")}};
          --kk-radius: 0px;
          --kk-font: "KingHwaOldSong", "Songti SC", "STSong", "Noto Serif CJK SC", "Source Han Serif SC", "SimSun", "NSimSun", serif;
          --kk-ui-font: var(--kk-font);
        }
        html, body { margin: 0; padding: 0; background: var(--kk-paper); color: var(--kk-ink); font: 14px/1.55 var(--kk-font); }
        * { box-sizing: border-box; }
        [hidden] { display: none !important; }
        ::selection { background: var(--kk-ink); color: var(--kk-paper); }
        :focus-visible { outline: 1px solid var(--kk-ink); outline-offset: 1px; }
        h1, h2, h3 { font-weight: 600; }
        .kk-muted { color: var(--kk-muted); }
        .kk-danger { color: var(--kk-danger); }
        .kk-title { font-size: 17px; font-weight: 600; margin: 0; }
        .kk-hairline { border: 0; border-top: 1px solid var(--kk-hairline); margin: 0; }

        .kk-button { font: inherit; font-size: 14px; line-height: 1.2; color: var(--kk-ink); background: var(--kk-paper);
          border: 1px solid var(--kk-ink); border-radius: 0; min-height: 35px; padding: 7px 14px; cursor: pointer;
          display: inline-flex; align-items: center; justify-content: center; gap: 6px; white-space: nowrap;
          transition: background-color .15s, border-color .15s, color .15s; }
        .kk-button:hover:not(:disabled) { background: var(--kk-hover); }
        .kk-button:active:not(:disabled) { background: var(--kk-pressed); }
        .kk-button.kk-primary { background: var(--kk-accent); color: var(--kk-on-accent); border-color: var(--kk-accent); }
        .kk-button.kk-primary:hover:not(:disabled) { background: var(--kk-accent-hover); border-color: var(--kk-accent-hover); }
        .kk-button.kk-primary:active:not(:disabled) { background: var(--kk-accent-pressed); border-color: var(--kk-accent-pressed); }
        .kk-button.kk-quiet { border-color: transparent; background: transparent; }
        .kk-button.kk-quiet:hover:not(:disabled) { background: var(--kk-hover); }
        .kk-button.kk-danger { color: var(--kk-danger); border-color: var(--kk-danger); background: var(--kk-paper); }
        .kk-button:disabled, .kk-button.kk-primary:disabled { background: var(--kk-hover); color: var(--kk-muted);
          border-color: var(--kk-strong-border); cursor: default; }
        .kk-button.kk-small { min-height: 28px; padding: 3px 10px; font-size: 13px; }

        .kk-input { font: inherit; font-size: 14px; color: var(--kk-ink); background: transparent; border: 0;
          border-bottom: 1px solid var(--kk-hairline); border-radius: 0; min-height: 35px; padding: 6px 2px; outline: none; }
        .kk-input:hover:not(:disabled) { border-bottom-color: var(--kk-strong-border); }
        .kk-input:focus, .kk-input:focus-visible { border-bottom-color: var(--kk-ink); outline: none; }
        .kk-input::placeholder { color: var(--kk-muted); opacity: 1; }
        .kk-input:disabled { color: var(--kk-muted); }
        input.kk-input[type=search]::-webkit-search-decoration { -webkit-appearance: none; }
        textarea.kk-input { min-height: 72px; resize: vertical; line-height: 1.55; }
        select.kk-input { -webkit-appearance: none; appearance: none; border: 1px solid var(--kk-ink); background: var(--kk-paper)
          url("{{chevron}}") no-repeat right 9px center / 12px 12px; padding: 6px 30px 6px 10px; cursor: pointer; }
        select.kk-input:focus { border-color: var(--kk-ink); }

        input[type=checkbox], input[type=radio] { -webkit-appearance: none; appearance: none; margin: 0; flex: none;
          width: 16px; height: 16px; border: 1px solid var(--kk-ink); background: var(--kk-paper); cursor: pointer;
          display: inline-grid; place-content: center; vertical-align: -3px; }
        input[type=radio] { border-radius: 50%; }
        input[type=checkbox]:checked { background: var(--kk-ink) url("{{check}}") no-repeat center / 14px 14px; }
        input[type=radio]:checked::before { content: ""; width: 8px; height: 8px; border-radius: 50%; background: var(--kk-ink); }
        input[type=checkbox]:disabled, input[type=radio]:disabled { opacity: .45; cursor: default; }

        /* The main window's switch: a 20px line with a 10px dot. */
        input[type=checkbox].kk-switch { width: 24px; height: 24px; border: 0; background: transparent; position: relative; }
        input[type=checkbox].kk-switch::before { content: ""; position: absolute; left: 2px; right: 2px; top: 50%; height: 1px; background: #B0B0B0; }
        input[type=checkbox].kk-switch::after { content: ""; position: absolute; left: 2px; top: 7px; width: 10px; height: 10px;
          border-radius: 50%; background: #B0B0B0; transition: left .15s; }
        input[type=checkbox].kk-switch:checked { background: transparent; }
        input[type=checkbox].kk-switch:checked::before { background: var(--kk-ink); }
        input[type=checkbox].kk-switch:checked::after { left: 12px; background: var(--kk-ink); }

        /* A settings row: label and hint on the left, control on the right. */
        .kk-row { display: flex; align-items: center; gap: 24px; min-height: 44px; padding: 6px 8px; margin: 0 -8px; }
        label.kk-row { cursor: pointer; }
        label.kk-row:hover { background: var(--kk-hover); }
        .kk-row > .kk-row-text { flex: 1; min-width: 0; }
        .kk-row-text strong { display: block; font-size: 14px; font-weight: 400; }
        .kk-row-text span { display: block; font-size: 12px; color: var(--kk-muted); line-height: 1.5; margin-top: 2px; }

        .kk-progress { height: 4px; background: var(--kk-hairline); overflow: hidden; }
        .kk-progress > span { display: block; height: 100%; width: 0; background: var(--kk-ink); transition: width .2s ease; }
        .kk-spinner { display: inline-block; width: 12px; height: 12px; border: 1.5px solid var(--kk-hairline);
          border-top-color: var(--kk-ink); border-radius: 50%; animation: kk-spin .8s linear infinite; vertical-align: -2px; }
        @keyframes kk-spin { to { transform: rotate(360deg); } }

        ::-webkit-scrollbar { width: 10px; height: 10px; background: transparent; }
        ::-webkit-scrollbar-thumb { background: #C9C9C9; border-radius: 0; border: 3px solid var(--kk-paper); }
        ::-webkit-scrollbar-thumb:hover { background: #B4B4B4; }
        ::-webkit-scrollbar-corner { background: transparent; }
        """;
    }

    private static string SvgDataUrl(string svg) =>
        "data:image/svg+xml," + Uri.EscapeDataString(svg).Replace("'", "%27", StringComparison.Ordinal);

    /// <summary>
    /// What this host supports, frozen into <c>kkindle.capabilities</c>.
    /// Plugins should feature-test methods rather than compare app versions.
    /// </summary>
    public static object BuildCapabilities(PluginManifest? manifest, string appVersion, bool headless) => new
    {
        apiVersion = PluginManifest.SupportedApiVersion,
        appVersion,
        headless,
        methods = PluginBridge.Methods,
        permissions = manifest is null ? null : new
        {
            library = manifest.LibraryAccess switch
            {
                PluginLibraryAccess.Write => "write",
                PluginLibraryAccess.Read => "read",
                _ => null
            },
            libraryAll = manifest.HasLibraryScopeAll,
            network = manifest.HasNetworkPermission,
            import = manifest.CanImportLibrary,
            translation = manifest.AllowsTranslation,
            pinyin = manifest.AllowsPinyin,
            storage = manifest.UsesStorage,
            secrets = manifest.UsesSecrets,
            content = manifest.CanReadContent,
            ai = manifest.UsesAi,
            readingWrite = manifest.CanWriteReadingData,
            bookFiles = manifest.CanReadBookFiles
        }
    };

    public static string BuildBridgeScript(PluginPageContext context, PluginTheme theme, PluginManifest? manifest = null)
    {
        var contextJson = JsonSerializer.Serialize(new
        {
            pluginId = context.PluginId,
            pluginVersion = context.PluginVersion,
            action = context.ActionId,
            bookIds = context.BookIds.Select(id => id.ToString("D")).ToArray(),
            language = context.Language,
            appVersion = context.AppVersion,
            theme = theme.IsDark ? "dark" : "light",
            selection = context.Selection,
            developerMode = context.DeveloperMode,
            headless = context.Headless
        }, JsonOptions);
        var capabilitiesJson = JsonSerializer.Serialize(
            BuildCapabilities(manifest, context.AppVersion, context.Headless),
            JsonOptions);
        return BridgeTemplate
            .Replace("__CONTEXT__", contextJson, StringComparison.Ordinal)
            .Replace("__CAPABILITIES__", capabilitiesJson, StringComparison.Ordinal);
    }

    // The host answers through window.__kkindleBridge.reply(id, payload).
    private const string BridgeTemplate = """
        (function () {
          'use strict';
          var pending = new Map();
          var listeners = Object.create(null);
          var sequence = 0;
          function post(message, attempt) {
            if (typeof window.invokeCSharpAction === 'function') {
              window.invokeCSharpAction(message);
              return;
            }
            // The platform adapter injects the callback during page load; allow
            // ample time before reporting that the host is unreachable.
            if ((attempt || 0) > 600) {
              var parsed = JSON.parse(message);
              settle(parsed.id, { ok: false, error: 'Kkindle bridge is unavailable.' });
              return;
            }
            setTimeout(function () { post(message, (attempt || 0) + 1); }, 20);
          }
          function settle(id, payload) {
            var entry = pending.get(id);
            if (!entry) return;
            pending.delete(id);
            if (payload && payload.ok) entry.resolve(payload.result);
            else entry.reject(new Error((payload && payload.error) || 'Unknown error'));
          }
          function emit(name, payload) {
            var list = listeners[name];
            if (!list) return;
            list.slice().forEach(function (handler) {
              try { handler(payload); }
              catch (error) { setTimeout(function () { throw error; }, 0); }
            });
          }
          function on(name, handler) {
            if (typeof handler !== 'function') throw new TypeError('handler must be a function');
            var list = listeners[name] || (listeners[name] = []);
            list.push(handler);
            return function () {
              var index = list.indexOf(handler);
              if (index >= 0) list.splice(index, 1);
            };
          }
          function call(method, params) {
            return new Promise(function (resolve, reject) {
              var id = ++sequence;
              pending.set(id, { resolve: resolve, reject: reject });
              post(JSON.stringify({ id: id, method: method, params: params || {} }), 0);
            });
          }
          // Handlers registered by provider and background scripts; the host
          // looks them up by "kind:id" after the script has run.
          var handlers = Object.create(null);
          function register(kind, id, handler) {
            if (typeof id === 'function') { handler = id; id = ''; }
            if (typeof handler !== 'function') throw new TypeError('handler must be a function');
            handlers[String(kind) + ':' + String(id || '')] = handler;
          }
          Object.defineProperty(window, '__kkindleBridge', {
            value: Object.freeze({ reply: settle, event: emit, handlers: handlers }),
            writable: false, configurable: false
          });
          function deepFreeze(value) {
            if (value && typeof value === 'object') {
              Object.getOwnPropertyNames(value).forEach(function (key) { deepFreeze(value[key]); });
              Object.freeze(value);
            }
            return value;
          }
          var context = Object.freeze(__CONTEXT__);
          var capabilities = deepFreeze(__CAPABILITIES__);
          var api = {
            context: context,
            capabilities: capabilities,
            has: function (method) { return capabilities.methods.indexOf(String(method)) >= 0; },
            register: Object.freeze({
              dictionary: function (id, handler) { register('dictionary', id, handler); },
              translator: function (id, handler) { register('translator', id, handler); },
              background: function (handler) { register('background', '', handler); }
            }),
            book: Object.freeze({
              getToc: function (bookId, options) {
                options = options || {};
                return call('book.getToc', {
                  bookId: String(bookId),
                  fileId: options.fileId == null ? null : String(options.fileId)
                });
              },
              getChapterText: function (bookId, chapterIndex, options) {
                options = options || {};
                return call('book.getChapterText', {
                  bookId: String(bookId),
                  chapterIndex: chapterIndex == null ? 0 : chapterIndex,
                  fileId: options.fileId == null ? null : String(options.fileId),
                  offset: options.offset == null ? 0 : options.offset,
                  maxLength: options.maxLength == null ? 200000 : options.maxLength
                });
              }
            }),
            ai: Object.freeze({
              status: function () { return call('ai.status'); },
              complete: function (prompt, options) {
                options = options || {};
                return call('ai.complete', {
                  prompt: String(prompt),
                  system: options.system == null ? null : String(options.system),
                  history: options.history || []
                }).then(function (result) { return result.text; });
              }
            }),
            reader: Object.freeze({
              open: function (bookId, options) {
                options = options || {};
                return call('reader.open', {
                  bookId: String(bookId),
                  fileId: options.fileId == null ? null : String(options.fileId),
                  chapterIndex: options.chapterIndex == null ? null : options.chapterIndex,
                  chapterPath: options.chapterPath == null ? null : String(options.chapterPath),
                  offset: options.offset == null ? null : options.offset
                });
              }
            }),
            library: Object.freeze({
              getBooks: function (ids) { return call('library.getBooks', { ids: ids || context.bookIds }); },
              search: function (options) {
                options = options || {};
                return call('library.search', {
                  query: options.query == null ? null : String(options.query),
                  author: options.author == null ? null : String(options.author),
                  tag: options.tag == null ? null : String(options.tag),
                  format: options.format == null ? null : String(options.format),
                  category: options.category == null ? null : String(options.category),
                  collectionId: options.collectionId == null ? null : String(options.collectionId),
                  readingStatus: options.readingStatus == null ? null : String(options.readingStatus),
                  favorite: typeof options.favorite === 'boolean' ? options.favorite : null,
                  sort: options.sort == null ? null : String(options.sort),
                  pageIndex: options.pageIndex == null ? 0 : options.pageIndex,
                  pageSize: options.pageSize == null ? 50 : options.pageSize
                });
              },
              getBook: function (id) { return call('library.getBook', { id: String(id) }); },
              getCollections: function () { return call('library.getCollections'); },
              createCollection: function (name) { return call('library.createCollection', { name: String(name) }); },
              addToCollection: function (bookId, collectionId) {
                return call('library.addToCollection', { bookId: String(bookId), collectionId: String(collectionId) });
              },
              removeFromCollection: function (bookId, collectionId) {
                return call('library.removeFromCollection', { bookId: String(bookId), collectionId: String(collectionId) });
              },
              importFiles: function (paths) { return call('library.importFiles', { paths: paths || [] }); },
              updateBook: function (id, patch) { return call('library.updateBook', { id: id, patch: patch }); },
              getCover: function (id) { return call('library.getCover', { id: id }); },
              setCover: function (id, data) { return call('library.setCover', { id: id, data: data }); },
              readFile: function (bookId, options) {
                options = options || {};
                return call('library.readFile', {
                  bookId: String(bookId),
                  fileId: options.fileId == null ? null : String(options.fileId),
                  offset: options.offset == null ? 0 : options.offset,
                  length: options.length == null ? 4194304 : options.length
                });
              },
              importDownload: function (url, options) {
                options = options || {};
                return call('library.importDownload', {
                  url: String(url),
                  fileName: options.fileName == null ? null : String(options.fileName),
                  headers: options.headers || {}
                });
              },
              translateBook: function (bookId, options) {
                options = options || {};
                return call('library.translateBook', {
                  bookId: String(bookId),
                  provider: options.provider || 'ai',
                  sourceLanguage: options.sourceLanguage || 'auto',
                  targetLanguage: options.targetLanguage || 'zh-CN',
                  translated: options.translated !== false,
                  bilingual: options.bilingual === true,
                  aiRequestsPerMinute: options.aiRequestsPerMinute || 0,
                  googleProxyAddress: options.googleProxyAddress == null ? '' : String(options.googleProxyAddress),
                  resume: options.resume !== false
                });
              },
              annotatePinyin: function (bookId, options) {
                options = options || {};
                return call('library.annotatePinyin', {
                  bookId: String(bookId),
                  engine: options.engine || 'dotnet-g2p',
                  outputStyle: options.outputStyle || 'tone-marked',
                  aiReview: options.aiReview !== false,
                  resume: options.resume !== false
                });
              },
              cancelTask: function () { return call('library.cancelTask'); }
            }),
            pinyin: Object.freeze({
              status: function () { return call('pinyin.status'); },
              downloadModel: function () { return call('pinyin.downloadModel'); }
            }),
            reading: Object.freeze({
              getProgress: function (bookId) { return call('reading.getProgress', { bookId: String(bookId) }); },
              getAnnotations: function (bookId, options) {
                options = options || {};
                return call('reading.getAnnotations', {
                  bookId: String(bookId),
                  limit: options.limit == null ? 500 : options.limit
                });
              },
              getBookmarks: function (bookId, options) {
                options = options || {};
                return call('reading.getBookmarks', {
                  bookId: String(bookId),
                  limit: options.limit == null ? 500 : options.limit
                });
              },
              getStats: function (bookId) { return call('reading.getStats', { bookId: String(bookId) }); },
              addAnnotation: function (bookId, annotation) {
                annotation = annotation || {};
                return call('reading.addAnnotation', {
                  bookId: String(bookId),
                  fileId: annotation.fileId == null ? null : String(annotation.fileId),
                  chapterIndex: annotation.chapterIndex,
                  startOffset: annotation.startOffset,
                  endOffset: annotation.endOffset,
                  note: annotation.note == null ? null : String(annotation.note),
                  color: annotation.color == null ? null : String(annotation.color),
                  style: annotation.style == null ? null : String(annotation.style)
                });
              },
              addBookmark: function (bookId, bookmark) {
                bookmark = bookmark || {};
                return call('reading.addBookmark', {
                  bookId: String(bookId),
                  fileId: bookmark.fileId == null ? null : String(bookmark.fileId),
                  chapterIndex: bookmark.chapterIndex,
                  offset: bookmark.offset == null ? 0 : bookmark.offset,
                  title: bookmark.title == null ? null : String(bookmark.title),
                  quote: bookmark.quote == null ? null : String(bookmark.quote)
                });
              }
            }),
            http: Object.freeze({
              fetch: function (url, options) {
                options = options || {};
                return call('http.fetch', {
                  url: String(url),
                  method: options.method || 'GET',
                  headers: options.headers || {},
                  body: options.body == null ? null : String(options.body),
                  responseType: options.responseType || 'text'
                });
              }
            }),
            network: Object.freeze({
              // Asks the user to allow one extra HTTPS host at runtime, for
              // mirrors or personal domains that cannot be known up front.
              requestAccess: function (origin) { return call('network.requestAccess', { origin: String(origin) }); }
            }),
            secrets: Object.freeze({
              get: function (key) { return call('secrets.get', { key: String(key) }); },
              set: function (key, value) {
                return call('secrets.set', { key: String(key), value: value == null ? null : String(value) });
              },
              remove: function (key) { return call('secrets.remove', { key: String(key) }); }
            }),
            events: Object.freeze({ on: on }),
            storage: Object.freeze({
              get: function (key) { return call('storage.get', { key: key }); },
              set: function (key, value) { return call('storage.set', { key: key, value: value === undefined ? null : value }); },
              remove: function (key) { return call('storage.remove', { key: key }); }
            }),
            ui: Object.freeze({
              close: function () { return call('ui.close'); },
              notify: function (message) { return call('ui.notify', { message: String(message) }); },
              setTitle: function (title) { return call('ui.setTitle', { title: String(title) }); },
              openExternal: function (url) { return call('ui.openExternal', { url: String(url) }); },
              pickFiles: function (options) {
                options = options || {};
                return call('ui.pickFiles', {
                  multiple: options.multiple === true,
                  extensions: options.extensions || []
                });
              },
              saveFile: function (fileName, content) {
                content = content || {};
                return call('ui.saveFile', {
                  fileName: String(fileName),
                  text: content.text == null ? null : String(content.text),
                  base64: content.base64 == null ? null : String(content.base64)
                });
              },
              copyText: function (text) { return call('ui.copyText', { text: String(text) }); },
              readText: function () { return call('ui.readText'); }
            })
          };
          Object.defineProperty(window, 'kkindle', { value: Object.freeze(api), writable: false, configurable: false });

          // Page diagnostics: console output, uncaught errors and rejected
          // promises reach the host, which shows them in its developer log.
          // Nothing is buffered before the platform adapter exists.
          function describeDiagnostic(value) {
            try {
              if (typeof value === 'string') return value;
              if (value instanceof Error) return value.stack || value.message || String(value);
              return JSON.stringify(value);
            } catch (error) {
              return String(value);
            }
          }
          function reportDiagnostic(level, values) {
            if (!context.developerMode) return;
            if (typeof window.invokeCSharpAction !== 'function') return;
            try {
              post(JSON.stringify({
                id: ++sequence,
                method: 'ui.log',
                params: { level: level, message: values.map(describeDiagnostic).join(' ') }
              }), 0);
            } catch (error) { }
          }
          ['log', 'info', 'warn', 'error', 'debug'].forEach(function (level) {
            var native = console[level];
            console[level] = function () {
              reportDiagnostic(level, Array.prototype.slice.call(arguments));
              if (typeof native === 'function') native.apply(console, arguments);
            };
          });
          window.addEventListener('error', function (event) {
            reportDiagnostic('error', [String(event.message || 'Script error') + (event.filename
              ? ' (' + event.filename + ':' + event.lineno + ')'
              : '')]);
          });
          window.addEventListener('unhandledrejection', function (event) {
            reportDiagnostic('error', ['Unhandled rejection: ' + describeDiagnostic(event.reason)]);
          });

          if (typeof document !== 'undefined' && document && document.documentElement) {
            document.documentElement.setAttribute('data-kk-theme', context.theme);
            document.documentElement.setAttribute('lang', context.language);
          }
        })();
        """;

    /// <summary>Resolves a page-relative reference to a plugin-root-relative path.</summary>
    internal static string CombineRelative(string baseDirectory, string reference)
    {
        var parts = baseDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        var path = Uri.UnescapeDataString(reference.Split('?', '#')[0]).Replace('\\', '/');
        foreach (var segment in path.Split('/'))
        {
            if (segment is "" or ".") continue;
            if (segment == "..")
            {
                if (parts.Count == 0)
                    throw new PluginPackageException($"插件页面引用的文件“{reference}”超出了插件目录。");
                parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(segment);
        }
        return string.Join('/', parts);
    }

    private static bool IsExternal(string reference) =>
        reference.Contains("//", StringComparison.Ordinal)
        || reference.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
        || reference.StartsWith("http:", StringComparison.OrdinalIgnoreCase)
        || reference.StartsWith("https:", StringComparison.OrdinalIgnoreCase);

    private static string EscapeScript(string code) =>
        code.Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase);

    private static string MediaTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".css" => "text/css",
        ".js" => "text/javascript",
        _ => "application/octet-stream"
    };

    [GeneratedRegex("""<script\b[^>]*\bsrc\s*=\s*["'](?<src>[^"']+)["'][^>]*>\s*</script\s*>""", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptRegex();

    [GeneratedRegex("""<link\b(?=[^>]*\brel\s*=\s*["']?stylesheet)[^>]*\bhref\s*=\s*["'](?<href>[^"']+)["'][^>]*>""", RegexOptions.IgnoreCase)]
    private static partial Regex StylesheetRegex();

    [GeneratedRegex("""<img\b[^>]*\bsrc\s*=\s*["'](?<src>[^"']+)["'][^>]*>""", RegexOptions.IgnoreCase)]
    private static partial Regex ImageRegex();

    [GeneratedRegex("""<script\b[^>]*>[\s\S]*?</script\s*>""", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptBlockRegex();

    [GeneratedRegex("<head\\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HeadRegex();

    [GeneratedRegex("<html\\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlTagRegex();
}

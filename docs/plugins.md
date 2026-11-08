# Kkindle 插件

插件是一个扩展名为 `.kkplugin` 的文件（本质是 zip），导入后在书库书籍的右键菜单和详情页的“插件操作”按钮里出现入口。插件页面运行在独立的网页窗口中，只能使用它声明的权限，不会接触主程序的内部实现。

插件还可以提供不带窗口的**脚本**：阅读器查词时的词典、划词翻译的翻译服务，以及定时运行的后台任务。它们和页面共用同一套 `window.kkindle` 接口和权限。

开发工具都在仓库里：

- `sdk/kkindle.d.ts`：`window.kkindle` 的 TypeScript 类型。
- `sdk/kkindle-mock.js`：在普通浏览器里模拟宿主，脱离应用开发页面。
- `tools/Kkindle.PluginTool`（命令 `kkplugin`）：校验、打包、签名、生成插件市场索引。

## 安装与使用

- 设置 → 高级 → 插件 → **导入插件…**，选择 `.kkplugin` 文件。导入前会列出插件声明的权限，确认后才安装。
- 安装目录：程序根目录下的 `plugins/<插件 id>/`；插件自己的数据在 `plugin-data/<插件 id>/`。两者都不参与同步和备份。
- 每个插件的开关、设置页、卸载按钮都在同一页。卸载会同时删除插件保存的数据。
- 升级：再次导入同 id 的更高版本即可覆盖；导入较低版本会给出提示但允许安装。
- **插件市场**：设置 → 高级 → 插件 → **插件市场…** 读取官方索引 `https://kkindle.stacker.beauty/plugins/index.json`，列出可安装和可更新的插件；安装仍然走同一个权限确认框，下载的文件必须与索引里的 SHA-256 和大小一致。开发时可以用环境变量 `KKINDLE_PLUGIN_CATALOG` 指向自己的索引（支持 `https://`、本机回环地址和 `file://`）。
- **更新检查**：打开了“自动检查更新”和网络功能时，启动约 45 秒后对照索引检查已安装插件，有新版本会在状态栏提示，到插件市场里点「更新」即可。
- **签名**：插件列表和安装确认框会显示来源——「随应用附带」「官方签名」「已签名：发布者」或「未签名」。已签名插件升级时如果换了签名密钥，确认框会给出醒目警告（可能是冒名包）。签名被篡改的包直接拒绝安装。详见下文“签名”。
- **后台任务**：同一页的「允许插件后台任务」开关控制所有插件的定时脚本，默认开启。
- 随包插件：桌面版安装包自带“书籍翻译与注音”，放在程序目录的 `bundled-plugins/` 下。首次启动时自动安装到上面的插件目录；之后的版本如果随包插件版本号更高，会自动更新已安装的副本，开关状态保持不变。用户卸载后不会再自动装回，需要时可以手动导入。已安装记录在插件目录的 `bundled-plugins.json`。

## 插件包结构

```
com.example.my-plugin/
├── manifest.json
├── icon.svg
├── signature.json        可选，kkplugin sign 生成
├── scripts/
│   ├── dictionary.js     词典 / 翻译 / 后台脚本（无窗口）
│   └── background.js
└── ui/
    ├── app.html
    ├── app.js
    ├── library.html
    └── settings.html
```

打包：`kkplugin pack examples/book-info`（或旧脚本 `scripts/package-plugin.sh examples/book-info`），产物为 `<目录名>.kkplugin`。见下文“开发工具”。

页面引用的相对资源（`<script src>`、`<link rel="stylesheet">`、`<img src>`）在打开时会被**内联**进同一个文档，因此页面里没有其他文件可加载，也不允许链接到外部地址。

## manifest.json

```json
{
  "id": "com.example.my-plugin",
  "name": "我的插件",
  "version": "1.0.0",
  "apiVersion": 1,
  "author": "作者",
  "description": "一句话说明这个插件做什么。",
  "homepage": "https://example.com/my-plugin",
  "minAppVersion": "1.1.3",
  "icon": "ui/icon.svg",
  "i18n": {
    "en-US": {
      "name": "My Plugin",
      "description": "What this plugin does, in one sentence.",
      "titles": { "match": "Match details", "catalog": "Online catalog", "app": "Online library", "dict": "Example dictionary" }
    }
  },
  "permissions": {
    "network": ["https://example.com", "https://*.examplecdn.com"],
    "library": "write",
    "libraryAll": true,
    "translation": true,
    "pinyin": true,
    "import": true,
    "storage": true,
    "secrets": true,
    "content": true,
    "bookFiles": true,
    "readingWrite": true,
    "ai": true
  },
  "contributes": {
    "bookActions": [
      { "id": "match", "title": "匹配详情", "page": "ui/app.html", "multiple": true, "width": 1080, "height": 760 }
    ],
    "commands": [
      { "id": "catalog", "title": "在线书目", "page": "ui/library.html", "width": 1080, "height": 760 }
    ],
    "readerActions": [
      { "id": "lookup", "title": "查资料", "page": "ui/reader.html", "width": 960, "height": 680 }
    ],
    "settings": { "page": "ui/settings.html", "width": 560, "height": 460 },
    "app": { "title": "在线书库", "page": "ui/library.html", "width": 1080, "height": 760 },
    "dictionaries": [
      { "id": "dict", "title": "示例词典", "script": "scripts/dictionary.js" }
    ],
    "translators": [
      { "id": "mt", "title": "示例翻译", "script": "scripts/dictionary.js" }
    ],
    "background": { "script": "scripts/background.js", "intervalMinutes": 360, "onStartup": true }
  }
}
```

| 字段 | 说明 |
|---|---|
| `id` | 小写字母、数字、点和短横线，3–64 个字符；同时是安装目录名 |
| `version` | `1.0.0` 形式；再次导入更高版本即为升级 |
| `apiVersion` | 目前固定为 `1`，不兼容时导入会被拒绝 |
| `minAppVersion` | 可选，`1.2.0` 形式；低于此版本的 Kkindle 拒绝安装，插件市场显示「需要 Kkindle x.y.z」 |
| `homepage` | 可选，`https://` 地址；显示在插件列表和市场中 |
| `i18n` | 可选，按语言代码（如 `en-US`）提供 `name`、`description` 和 `titles`。`titles` 的键是贡献点 `id`，独立页面用 `app`。找不到完全匹配时按语言前缀（`en`）匹配，仍找不到就用顶层字段 |
| `permissions.network` | 允许请求的 HTTPS 来源；`*.example.com` 包含子域名，端口必须是默认端口 |
| `permissions.library` | `read` 可读选中书籍，`write` 可修改；声明 `bookActions` 时必须至少为 `read` |
| `permissions.libraryAll` | `true` 时允许搜索整个书库、读取分组和阅读数据；创建或修改分组还需要 `library` 为 `write` |
| `permissions.translation` | 允许调用宿主的整本书翻译引擎，把结果加入书库；必须同时声明 `permissions.library`（至少 `read`） |
| `permissions.pinyin` | 允许调用宿主的书籍注音引擎、查询/下载 g2pW 模型，把结果加入书库；必须同时声明 `permissions.library`（至少 `read`） |
| `permissions.import` | 允许把插件指定的文件下载并加入书库（单个文件上限 500 MB） |
| `permissions.storage` | 允许使用插件私有键值存储（上限 1 MB） |
| `permissions.secrets` | 允许使用系统密钥库加密保存账号、密码等敏感信息 |
| `permissions.content` | 允许读取书籍目录和章节正文（`book.getToc` / `book.getChapterText`），支持 EPUB、AZW3、MOBI；需要 `library` |
| `permissions.bookFiles` | 允许按块读取书籍原始文件（`library.readFile`）；需要 `library` |
| `permissions.readingWrite` | 允许新增划线和书签（`reading.addAnnotation` / `addBookmark`）；需要 `library` |
| `permissions.ai` | 允许使用用户在应用里配置的 AI 服务（`ai.complete`），插件拿不到 API Key |
| `contributes.bookActions` | 书籍操作；`multiple: true` 时允许一次选中多本书 |
| `contributes.commands` | 全局命令；显示在书库工具栏的「插件工具」菜单里，打开时不带选中的书籍 |
| `contributes.readerActions` | 阅读器划词操作；选中文字后出现在阅读器选区工具栏，页面通过 `kkindle.context.selection` 取得选中内容 |
| `contributes.settings` | 设置页；插件设置完全由插件自己的页面实现 |
| `contributes.app` | 独立页面；在设置 → 插件里以「打开」按钮进入，不依赖选中的书籍 |
| `contributes.dictionaries` | 词典脚本；阅读器查词弹窗里多出一个以 `title` 命名的标签页。见“词典、翻译与后台脚本” |
| `contributes.translators` | 翻译脚本；阅读器划词翻译面板顶部多出一个以 `title` 命名的按钮 |
| `contributes.background` | 后台脚本；`intervalMinutes`（0 或 15–10080）和 `onStartup`（默认 `true`）至少要有一个生效 |

声明了 `permissions.network` 的插件还可以在运行时请求访问额外域名（例如轮换的镜像或个人域名），用户确认后才会放行；确认结果保存在 `plugin-data/<id>/network.json`，卸载插件时一并删除。

## 页面 API

插件页面通过 `window.kkindle` 调用宿主。所有方法返回 Promise；失败时 Promise 被拒绝，`error.message` 可直接展示给用户。页面有严格的 CSP：外链脚本、样式、图片和 `connect-src` 都被禁止，网络请求只能走 `kkindle.http.fetch`。

### 上下文

```js
kkindle.context
// { pluginId, pluginVersion, action, bookIds, language, appVersion, theme,
//   selection, developerMode, headless }
```

`language` 是界面语言（`zh-CN` / `en-US`），可据此切换插件自己的文案。`headless` 在词典、翻译和后台脚本里为 `true`。

`bookIds` 是本次打开窗口时选中的书籍；没有 `libraryAll` 权限时，插件只能访问这些书籍。

`selection` 只在阅读器划词操作中非空：

```js
{
  text, startOffset, endOffset, prefix, suffix,
  bookId, fileId, chapterIndex, chapterPath,
  position   // 阅读器内部定位信息，通常原样保存即可
}
```

### 能力检测与接口稳定性

```js
kkindle.capabilities
// { apiVersion: 1, appVersion, headless, methods: ["library.getBooks", ...],
//   permissions: { library, libraryAll, network, import, translation, pinyin,
//                  storage, secrets, content, ai, readingWrite, bookFiles } }

if (kkindle.has("ai.complete")) { /* 当前宿主支持这个方法 */ }
```

**按方法检测，不要比较版本号。** 约定如下：

- `apiVersion: 1` 期间只做向后兼容的增量：新增方法、新增可选参数、返回对象新增字段。已有方法的名称、参数和返回字段不会删改。
- 新方法发布后，旧版应用里 `kkindle.has(name)` 为 `false`，调用会被拒绝（`不支持的方法`）。可选功能先检测再用；必须依赖新方法时，在 manifest 里写 `minAppVersion`，旧版应用会拒绝安装而不是运行到一半失败。
- 不兼容的改动只会出现在 `apiVersion: 2`，届时旧插件仍按版本 1 运行或被明确拒绝，不会静默出错。
- 返回对象里没有写进文档的字段不属于接口，可能变化。

### 书库

```js
const books = await kkindle.library.getBooks();            // 本次选中的书籍
const book  = await kkindle.library.getBooks([id]);        // 也可以显式指定
await kkindle.library.updateBook(id, { publisher, description, rating, ratingCount });
const dataUrl = await kkindle.library.getCover(id);        // 没有封面时为 null
await kkindle.library.setCover(id, "image/png;base64,...");
```

可写字段：`title`、`authors`、`series`、`seriesIndex`、`description`、`publisher`、`publishDate`、`isbn`、`pageCount`、`binding`、`tags`、`category`、`rating`（0–10）、`ratingCount`。一次调用里任一字段不合法，整次更新都不会写入。写回后主窗口的书库会自动刷新。

书籍对象还带有 `favorite`、`readingStatus`（`unread` / `reading` / `finished`）、`collectionIds`、`updatedAt` 和 `formats`。

#### 搜索整个书库（`permissions.libraryAll`）

```js
const page = await kkindle.library.search({
  query: "三体",              // 匹配标题、作者、标签、丛书和拼音
  author: null, tag: null, format: null, category: null,
  collectionId: null,
  readingStatus: null,        // unread / reading / finished / null
  favorite: null,             // true / false / null
  sort: "updated",            // updated / title / author / created / progress
  pageIndex: 0,
  pageSize: 50                // 1–200
});
// { books, totalCount, pageIndex, pageSize }

const book = await kkindle.library.getBook(bookId);   // 单本书的详情
```

#### 分组

```js
const collections = await kkindle.library.getCollections();
// [{ id, name, bookCount }]

const created = await kkindle.library.createCollection("待读");   // 需要 library: "write"
await kkindle.library.addToCollection(bookId, created.id);
await kkindle.library.removeFromCollection(bookId, created.id);
```

分组名称最多 60 个字符，且不能与现有分组重名。「未收藏」是系统分组，不能通过插件修改。宿主的「分组互斥」设置只约束主界面操作，插件调用会保留多分组关系。

#### 阅读数据

```js
const progress = await kkindle.reading.getProgress(bookId);
// { bookId, title, files: [{ fileId, format, chapterPath, chapterIndex,
//   scrollPosition, progressPercent, flowMode, updatedAt, contentPosition }] }

const { annotations } = await kkindle.reading.getAnnotations(bookId, { limit: 500 });
// [{ id, bookId, fileId, chapterPath, startOffset, endOffset,
//    selectedText, note, color, underlineStyle, createdAt, updatedAt }]

const { bookmarks } = await kkindle.reading.getBookmarks(bookId, { limit: 500 });
// [{ id, bookId, fileId, chapterPath, chapterIndex, title, quote, createdAt }]

const stats = await kkindle.reading.getStats(bookId);
// { bookId, files: [{ fileId, format, cumulativeSeconds, progressPercent,
//   completedChapters, totalChapters, updatedAt }] }
```

阅读数据按书籍文件（格式）保存，因此返回值总是 `files` 数组；划线和高亮都表示为 `annotations`，`note` 为空时表示只有划线。

声明 `permissions.readingWrite` 后可以**新增**划线和书签（不能修改或删除已有数据）：

```js
const annotation = await kkindle.reading.addAnnotation(bookId, {
  chapterIndex: 3,            // book.getToc 的 chapters[].index
  startOffset: 120,           // book.getChapterText 文本中的字符位置
  endOffset: 168,             // 必须大于 startOffset，跨度最多 20,000 个字符
  note: "批注（可选）",
  color: "#C0392B",           // #RRGGBB，默认 #000000
  style: "highlight",         // solid / wavy / dashed / highlight
  fileId: null                // 可选，默认取第一个可读格式
});
// { id, bookId, fileId, chapterPath, startOffset, endOffset, selectedText, note, color, underlineStyle }

const bookmark = await kkindle.reading.addBookmark(bookId, {
  chapterIndex: 3, offset: 120,
  title: "可选，默认章节名", quote: "可选，默认取该位置之后的文字"
});
// { id, bookId, fileId, chapterPath, chapterIndex, title, quote }
```

偏移量和阅读器内部使用的是同一套章节文本，因此只要来自 `book.getChapterText`，写入的划线会准确落在原文位置；`selectedText`、前后文由宿主根据偏移截取，插件不能伪造。写入的数据和用户自己做的划线、书签存在一起，下次打开这本书即可看到。

### 书籍正文（`permissions.content`）

```js
const outline = await kkindle.book.getToc(bookId, { fileId: null });
// { bookId, fileId, format,
//   chapters: [{ index, title, path, length }],         // 阅读顺序（spine）
//   toc: [{ title, chapterIndex, level, fragment }] }    // 目录，可多级

const slice = await kkindle.book.getChapterText(bookId, 0, { offset: 0, maxLength: 200000 });
// { chapterIndex, title, path, text, offset, length, hasMore }
```

- 支持 EPUB、AZW3、MOBI；书籍没有这些格式时报错。`fileId` 省略时自动选择。
- `text` 是阅读器看到的纯文本（去掉标签、保留段落换行）；`length` 是整章长度，章节很长时用 `offset` + `hasMore` 分段读取，每次最多 200,000 个字符。
- `path` 与划线、书签里的 `chapterPath` 一致，可直接对应。
- 访问范围和书库一样：没有 `libraryAll` 时只能读本次选中的书。
- 云端未下载的书会先按需下载到本地。

### 读取书籍文件（`permissions.bookFiles`）

```js
let offset = 0, chunks = [];
for (;;) {
  const chunk = await kkindle.library.readFile(bookId, { offset, length: 4 * 1024 * 1024 });
  // { fileId, name, format, size, offset, data /* base64 */, hasMore }
  chunks.push(chunk.data);
  if (!chunk.hasMore) break;
  offset = chunk.offset + atob(chunk.data).length;
}
```

每块默认 4 MB、最多 8 MB。适合导出、转换或上传到用户自己的服务；只需要文字时请用 `book.getChapterText`，权限更小。

### 在阅读器中打开

```js
await kkindle.reader.open(bookId, {
  chapterIndex: 3,            // 或 chapterPath: "OEBPS/Text/ch3.xhtml"
  offset: 120,                // 章节内字符位置，可选
  fileId: null
});
```

关闭插件窗口，打开阅读器并跳到指定位置；不需要额外权限，但书籍访问范围规则同样适用。脚本（无窗口）里不可用。

### AI（`permissions.ai`）

```js
const status = await kkindle.ai.status();
// { available, provider, model, reason }   reason：未启用 / 未配置 / 网络已关闭 等

const text = await kkindle.ai.complete("把这段话改写得更简洁：……", {
  system: "你是一名编辑。",
  history: [{ role: "user", content: "……" }, { role: "assistant", content: "……" }]
});
```

使用用户在 设置 → AI 里配置的服务和模型，插件拿不到 API Key。`complete` 直接返回回复文本。提示词、系统提示和历史合计上限 200,000 个字符，历史最多 50 条。AI 未启用、未配置或网络关闭（本机模型除外）时调用失败，先用 `status()` 判断并给出提示。

### 网络

```js
const response = await kkindle.http.fetch(url, {
  method: "GET",              // GET / POST / HEAD
  headers: { Accept: "text/html" },
  body: null,                 // POST 时为请求体
  responseType: "text"        // "text" 或 "base64"
});
// { status, ok, url, contentType, headers, body }
```

- 只能请求 `https://` 地址，且必须匹配 manifest 里声明的来源或用户批准的域名；重定向目标同样要重新校验。
- `Cookie`、`Host`、`Sec-*` 等请求头不允许设置；响应头里的 `Set-Cookie` 不会暴露。
- `set-cookie` 在同一个页面内会自动保存，适合同一站点多次请求。
- 应用设置里关闭网络功能时，请求会直接失败。
- 单个响应上限 10 MB。

### 下载并入库

```js
const result = await kkindle.library.importDownload("https://example.com/book.epub", {
  fileName: "书名.epub",
  headers: { "remix-userid": "…" }   // 可选，和 http.fetch 的限制相同
});
// { fileName, imported, added, message, bookId } 或 { cancelled: true, fileName }
```

宿主会校验地址权限，下载到临时文件后调用书库的导入流程。下载地址被重定向到未声明的 https 域名（常见于文件 CDN）时，会询问用户是否允许，允许后该域名和运行时授权一样长期有效；`http.fetch` 的重定向仍然只能到已允许的域名。服务返回 JSON 或 HTML 页面（额度用完、登录失效等）时视为下载被拒绝，不会导入。下载前会向用户显示文件名和大小并请求确认（同一窗口内可以勾选不再询问）。只接受常见电子书扩展名（EPUB、PDF、MOBI、AZW3、TXT、DJVU、FB2 等），单个文件上限 500 MB。下载和导入进度通过 `kkindle.events.on("import.progress", …)` 推送：

```js
kkindle.events.on("import.progress", ({ stage, downloadedBytes, totalBytes, percent }) => {
  // stage: "download" | "import"
});
```

### 本地文件与剪贴板

```js
const { files } = await kkindle.ui.pickFiles({
  multiple: false,
  extensions: [".epub", ".pdf"]     // 省略时显示「所有文件」
});
// files: [{ name, path }]

const imported = await kkindle.library.importFiles(files.map(file => file.path));
// { items: [{ sourcePath, succeeded, message, bookId, added }], successCount, addedCount }

const saved = await kkindle.ui.saveFile("笔记.txt", { text: "..." });
// 也可以传 { base64: "..." }；用户取消时返回 null；成功返回 { name, path }

await kkindle.ui.copyText("要复制的文字");
const result = await kkindle.ui.readText();   // { text } 或 null
```

- 文件和保存对话框都由宿主弹出，插件不能直接访问文件系统。
- `library.importFiles` 需要 `permissions.import`，并且只接受本窗口文件选择器返回的路径；每条路径导入一次后即失效，防止插件读取任意本地文件。
- 保存内容上限 10 MB，复制/读取剪贴板文本上限 100,000 个字符。

### 书籍翻译与注音

整本书的翻译和注音由宿主引擎完成；插件负责收集选项、展示进度和结果。翻译沿用应用设置里的 AI 服务、免费翻译服务和网络开关，插件不需要保存 API Key。选择 AI 翻译时，如果应用里还没有配置好 AI 服务，会在开始前直接报错，不会等到扫描完书稿才失败。

```js
const result = await kkindle.library.translateBook(bookId, {
  provider: "ai",             // ai / bing / google
  sourceLanguage: "auto",     // 见 TranslationLanguageCatalog
  targetLanguage: "zh-CN",
  translated: true,           // 生成单译版
  bilingual: false,           // 生成双语对照版
  aiRequestsPerMinute: 0,     // 0 表示不限制
  googleProxyAddress: "",
  resume: true                // 有缓存时继续
});
// { outputCount, added, skipped, failed, characters, segments, outputs } 或 { cancelled: true }
```

```js
const result = await kkindle.library.annotatePinyin(bookId, {
  engine: "dotnet-g2p",       // dotnet-g2p / g2pw
  outputStyle: "tone-marked", // tone-marked / tone-number / none
  aiReview: true,             // 用应用里的 AI 复核多音字；未配置时自动关闭
  resume: true
});
// { scannedPages, annotatedPages, characters, reviewCandidates, aiReviewed, aiReview, replaced }
```

已经有注音版的书可以再次生成；成功后旧的注音版会被移除，新文件沿用原来的 `_pinyin` 名称。任务进行中关闭插件窗口会先询问是否取消任务，选择继续则窗口保留、任务照常运行。

```js
await kkindle.library.cancelTask();       // 请求取消当前任务，结果以 { cancelled: true } 返回
const status = await kkindle.pinyin.status();   // { g2pwInstalled }
await kkindle.pinyin.downloadModel();           // 下载 g2pW 模型（约 590 MB），成功返回 { installed: true }，取消返回 { cancelled: true }
```

同一时间只允许一个书籍任务（翻译、注音或模型下载）。任务进度通过 `task.progress` 推送：

```js
kkindle.events.on("task.progress", ({ task, percent, stage, message, processed, total, indeterminate }) => {
  // task: "translation" | "pinyin" | "pinyin-model"
});
```

翻译和注音把中间结果保存在应用的 `data/translations`、`data/pinyin` 目录，取消或失败后再次以 `resume: true` 调用即可继续。成功生成的文件会自动加入原书籍，插件无法指定其他书籍。

### 账号信息

```js
await kkindle.secrets.set("password", "……");
const password = await kkindle.secrets.get("password");   // 不存在时为 null
await kkindle.secrets.remove("password");
```

值用系统密钥库（Windows DPAPI / macOS Keychain）加密后保存在 `plugin-data/<id>/secrets.json`。换一台电脑或另一个系统账号无法解密，需要重新填写。

### 存储

```js
await kkindle.storage.set("settings", { interval: 30000 });
const settings = await kkindle.storage.get("settings");
await kkindle.storage.remove("settings");
```

值是任意 JSON；每个插件总量上限 1 MB。

### 界面

```js
kkindle.ui.setTitle("当前书名");   // 窗口标题
kkindle.ui.notify("已写回");       // 底部浮层提示
kkindle.ui.openExternal(url);     // 用系统浏览器打开 http(s) 链接
kkindle.ui.close();               // 关闭窗口
```

### 运行时域名授权

```js
const { granted, host } = await kkindle.network.requestAccess("https://mirror.example");
```

地址必须是 `https://` 默认端口。manifest 里已经声明的域名直接返回 `granted: true`；其他域名会弹窗询问用户，允许后该域名在本插件内长期有效。`http.fetch` 和 `library.importDownload` 都可使用已批准的域名。

### 全局事件

```js
const off = kkindle.events.on("library.changed", () => refresh());
kkindle.events.on("reading.changed", ({ kind }) => { /* annotation / bookmark / ... */ });
kkindle.events.on("theme.changed", ({ theme, paper, ink, mutedInk, accent }) => { /* ... */ });
```

- `library.changed`：书库增删改后触发，载荷为空对象。
- `reading.changed`：阅读进度、划线、书签、阅读统计或读后感变化后触发，`kind` 为变化类型。
- `theme.changed`：用户切换应用主题后触发；宿主会自动替换页面的主题样式表，插件只需更新自己缓存的主题值。

`kkindle.events.on` 返回取消订阅函数。事件只在插件窗口打开期间推送；窗口关闭后不需要清理。

### 样式

宿主会注入主题变量和一组与主窗口一致的控件样式（黑白纸墨、直角、下划线输入框），页面直接使用即可跟随应用主题和配色：

```css
body { color: var(--kk-ink); background: var(--kk-paper); font: 14px var(--kk-font); }
/* 颜色：--kk-paper --kk-ink --kk-muted --kk-hairline --kk-strong-border --kk-hover --kk-pressed
         --kk-accent --kk-accent-hover --kk-accent-pressed --kk-on-accent --kk-danger
   其他：--kk-radius（0px） --kk-font */
```

| 类 | 用途 |
| --- | --- |
| `.kk-button` | 描边按钮；加 `.kk-primary` 为主操作（实心），`.kk-quiet` 为弱化按钮，`.kk-danger` 为危险操作，`.kk-small` 为紧凑尺寸 |
| `.kk-input` | 输入框（只有下划线）；用在 `select` 上是带边框的下拉框，也适用于 `textarea` |
| `input[type=checkbox]` / `input[type=radio]` | 自动使用方形复选框和单选框 |
| `input[type=checkbox].kk-switch` | 主窗口设置页样式的开关 |
| `.kk-row` + `.kk-row-text`（内含 `strong`、`span`） | 设置行：左侧标题和说明，右侧控件；用 `label.kk-row` 时整行可点击并有悬停背景 |
| `.kk-title` / `.kk-muted` / `.kk-danger` / `.kk-hairline` | 标题、次要文字、错误文字、分隔线 |
| `.kk-progress > span` / `.kk-spinner` | 进度条（设置 `span` 的宽度）和加载指示 |

带 `hidden` 属性的元素始终隐藏，不会被页面里的 `display` 规则覆盖。字体随应用使用宋体系字体；由于 CSP 只允许 `data:` 字体，不要引用外部字体文件。

## 词典、翻译与后台脚本

`contributes.dictionaries`、`contributes.translators` 和 `contributes.background` 指向的是 `.js` 脚本而不是页面。脚本在宿主内的 JavaScript 沙箱里运行，没有窗口和 DOM，但 `window.kkindle` 与页面完全相同，权限检查也相同。脚本在顶层注册处理函数：

```js
// scripts/dictionary.js
kkindle.register.dictionary("dict", async (term, { language }) => {
  const res = await kkindle.http.fetch("https://dict.example.com/api?q=" + encodeURIComponent(term));
  if (!res.ok) return null;                       // null / 空数组：显示“没有结果”
  const data = JSON.parse(res.body);
  return {
    headword: data.word,
    phonetic: data.ipa,                           // 可选
    senses: data.meanings.map(m => ({ pos: m.pos, text: m.definition, examples: m.examples })),
    note: "来源：Example Dictionary"               // 可选
  };
});

kkindle.register.translator("mt", async (text, { target, language }) => {
  const key = await kkindle.secrets.get("apiKey");
  const res = await kkindle.http.fetch("https://mt.example.com/translate", {
    method: "POST",
    headers: { "Content-Type": "application/json", Authorization: "Bearer " + key },
    body: JSON.stringify({ text, target })
  });
  return JSON.parse(res.body).translation;        // 字符串或 { text }
});
```

```js
// scripts/background.js
kkindle.register.background(async ({ reason, lastRun, lastSucceeded }) => {
  // reason: "startup" | "interval"；lastRun 为上次运行时间（ISO 字符串）或 null
  const page = await kkindle.library.search({ sort: "updated", pageSize: 20 });
  await kkindle.storage.set("lastSync", { at: new Date().toISOString(), count: page.totalCount });
});
```

| 贡献点 | 何时调用 | 参数 | 返回值 |
|---|---|---|---|
| `dictionaries` | 阅读器查词弹窗打开时，每个词典一个标签页 | `(term, { language })` | 字符串、单个词条、词条数组或 `{ entries: [...] }`；`null` 表示无结果 |
| `translators` | 用户在划词翻译面板点击该翻译服务的按钮 | `(text, { target, language })`，`target` 为面板当前的目标语言 | 字符串、`{ text }` 或 `{ translation }` |
| `background` | 启动约 20 秒后（`onStartup`）和每隔 `intervalMinutes` 分钟 | `({ reason, lastRun, lastSucceeded })` | 忽略；抛出异常或 reject 视为失败 |

词条对象的字段：`headword`（或 `word`，缺省为查询词）、`phonetic`（或 `pronunciation`）、`senses`（字符串或 `{ pos/partOfSpeech, text/definition, examples }` 的数组；没有 `senses` 时可直接写 `definition`）、`note`。宿主只显示这些字段，并截断过长内容：最多 20 个词条、每条 30 个义项、每个义项 5 个例句、单段文字 4,000 字符；译文最多 100,000 字符。返回的内容一律作为纯文本显示，不会解释 HTML。

运行规则：

- 每次调用都会新建沙箱并重新执行整个脚本，所以全局变量不会在两次调用之间保留；需要缓存时用 `kkindle.storage`。
- 时限：词典和翻译 20 秒，后台任务 2 分钟；内存上限 64 MB，脚本文件最多 2 MB，返回值序列化后最多 200,000 字符。超时或超限即视为失败。
- 可用的全局对象：标准 JavaScript（含 `async`/`await`）、`Promise`、`setTimeout` / `clearTimeout`、`atob` / `btoa`、`console`。**没有** `fetch`、`XMLHttpRequest`、`document`、`localStorage`；联网用 `kkindle.http.fetch`。
- 脚本不能弹出界面：`ui.pickFiles`、`ui.saveFile`、剪贴板、`ui.openExternal`、`reader.open` 会被拒绝；`network.requestAccess` 不会弹窗，只对 manifest 声明或用户之前在页面里批准过的域名返回 `granted: true`；`library.importDownload` 需要用户确认，因此在脚本里总是被拒绝。`ui.notify` 显示在主窗口状态栏。
- 脚本的 `context.bookIds` 为空，读取书籍需要 `libraryAll`（搜索后按 id 访问）。
- 后台任务同一时间只运行一个插件；连续失败 5 次后暂停，直到插件更新到新版本。运行记录在 `plugin-data/background-state.json`。用户可以在插件设置里整体关闭后台任务；网络开关同样约束脚本。
- 开发者模式下，脚本的 `console.*` 和错误写入 `plugin-data/headless-console.log`（上限 1 MB），排查问题时先看这里。

## 从示例插件开始

`examples/book-info` 是最小可运行的插件：只声明读取权限，打开后展示选中书籍的作者、丛书、出版社、出版日期、ISBN、页数、标签和格式。它不连接网络、不修改书库，适合直接复制成新插件的起点。

### 文件结构

```
examples/book-info/
├── manifest.json         插件身份、权限和入口
└── ui/
    ├── icon.svg          设置 → 插件列表中的图标
    ├── app.html          书籍操作打开的页面
    └── app.js            页面脚本
```

`manifest.json` 必须位于插件目录根部，`page` 路径都相对于插件目录。打包后的 `.kkplugin` 是一个普通 zip，目录结构不变。

### manifest.json：声明插件

示例的完整内容：

```json
{
  "id": "com.example.book-info",
  "name": "书籍信息示例",
  "version": "1.0.0",
  "apiVersion": 1,
  "author": "Kkindle",
  "description": "最小示例插件：读取选中书籍的信息并展示，适合作为自己插件的起点。",
  "icon": "ui/icon.svg",
  "permissions": {
    "library": "read"
  },
  "contributes": {
    "bookActions": [
      {
        "id": "info",
        "title": "查看书籍信息",
        "page": "ui/app.html",
        "multiple": true,
        "width": 720,
        "height": 620
      }
    ]
  }
}
```

逐项说明：

| 字段 | 示例值 | 说明 |
|---|---|---|
| `id` | `com.example.book-info` | 全局唯一，小写字母、数字、点和短横线，3–64 个字符。它同时是安装目录名；换 id 就是另一个插件。建议使用反向域名。 |
| `name` | 书籍信息示例 | 显示在设置 → 插件和权限确认框里的名称。 |
| `version` | `1.0.0` | 三段式版本号。导入同 id 的更高版本即覆盖升级；相同或更低版本会提示。 |
| `apiVersion` | `1` | 宿主接口版本，目前只能是 `1`。 |
| `author` / `description` | | 显示在插件列表；描述写清插件做什么、访问哪些网站。 |
| `icon` | `ui/icon.svg` | 可选，必须是包内文件；当前用于打包校验，插件列表暂不显示图标。 |
| `permissions.library` | `read` | `read` 可读取本次打开的书籍，`write` 还可修改。声明 `bookActions` 时不能省略。 |
| `permissions.network` | — | 允许请求的 HTTPS 来源。写 `[]` 表示“允许联网，但没有预先批准的站点”，运行时再用 `network.requestAccess` 逐个询问；完全不写则不能联网。 |
| `permissions.storage` | — | 允许使用插件私有的键值存储。需要保存设置时声明。 |
| `permissions.secrets` | — | 允许用系统密钥库保存密码、令牌等。需要账号登录时声明。 |
| `permissions.import` | — | 允许把 URL 指向的电子书下载并加入书库。 |
| `permissions.translation` / `pinyin` | — | 允许调用宿主的整本书翻译/注音引擎，必须同时声明 `library`。 |
| `contributes.bookActions[]` | | 书籍右键菜单和详情页“插件操作”里的入口。`id` 在插件内唯一；`title` 是菜单文字；`page` 是打开的页面；`multiple` 默认 `true`，为 `false` 时一次只能选一本书；`width`/`height` 缺省为 960×680。 |
| `contributes.settings` | | 设置页，形如 `{ "page": "ui/settings.html", "width": 640, "height": 520 }`；宽高缺省 640×520。 |
| `contributes.app` | | 独立页面，形如 `{ "title": "工具", "page": "ui/app.html", "width": 960, "height": 680 }`；在设置 → 插件里用「打开」进入，不依赖选中的书籍。 |

最省事的权限原则：先只声明功能真正需要的项。读取就够时不要写 `write`；不联网就不要写 `network`。安装时的权限确认框会把这些内容原样展示给用户。

### app.html：页面

示例页面是一个普通 HTML 文档：

```html
<!DOCTYPE html>
<html lang="zh-CN">
<head>
  <meta charset="utf-8">
  <title>书籍信息示例</title>
  <style>
    body { padding: 20px 22px; font-size: 14px; }
    .book { border-top: 1px solid var(--kk-hairline); padding: 12px 0; }
  </style>
</head>
<body>
  <div id="list"></div>
  <script src="app.js"></script>
</body>
</html>
```

打开窗口时，宿主会：

1. 把相对路径的 `<script src>`、`<link rel="stylesheet">` 和 `<img src>` 直接内联进同一份文档，因此页面里没有其他文件可加载；总内联大小上限 16 MB。
2. 注入主题样式、`window.kkindle` 桥接和严格的 CSP。CSP 禁止外链脚本、外链图片和 `connect-src`，所以图片要么来自内联数据，要么通过 `library.getCover` / `http.fetch` 拿到 base64 后转成 `data:` 地址。
3. 禁止页面导航、打开新窗口和提交表单，链接只能交给 `kkindle.ui.openExternal`。

样式建议直接使用宿主注入的变量和控件类（见文末“样式”一节），这样能自动跟随浅色/深色主题。不要依赖系统原生 `<select>` 的弹窗外观，需要下拉菜单时在页面里自己画。

### app.js：读取和展示

`window.kkindle` 在页面脚本执行前已经存在，不需要等待事件：

```js
(async function () {
  'use strict';
  const list = document.getElementById('list');

  try {
    const books = await window.kkindle.library.getBooks();
    for (const book of books) {
      const card = document.createElement('section');
      const title = document.createElement('h2');
      title.textContent = book.title || '未命名书籍';
      card.appendChild(title);
      list.appendChild(card);
    }
  } catch (error) {
    list.textContent = '读取失败：' + (error && error.message ? error.message : error);
  }
})();
```

关键点：

- `kkindle.library.getBooks()` 不传参数时只返回**本次窗口选中的书籍**。`getBooks([id])` 也只能传入这些 id；其他 id 会被拒绝。独立应用页和设置页没有选中书籍，`getBooks()` 返回空数组。
- 返回的书籍字段：`id`、`title`、`authors`、`series`、`seriesIndex`、`description`、`publisher`、`publishDate`、`isbn`、`pageCount`、`binding`、`rating`、`ratingCount`、`tags`、`category`、`hasCover`、`formats`。
- 所有 `kkindle` 方法都返回 Promise，失败时 reject，`error.message` 可以直接展示。不要用未捕获的 Promise：页面里没有可见的控制台。
- 展示远端或书库文本时用 `textContent`，不要拼 `innerHTML`，避免把书名或简介当成 HTML 执行。
- 页面脚本不会自动重跑；需要刷新时关闭窗口再打开。

### 打开窗口时发生了什么

以书籍操作为例：

1. 用户选中一本书或多本书，右键选择插件操作，或在详情页点“插件操作”。
2. 宿主检查插件是否启用、`multiple` 是否允许当前选择，然后把选中的书 id 传给窗口。
3. 宿主把页面合成为单个文档，注入 `window.kkindle`，按 manifest 的宽高创建窗口。
4. 页面通过 `kkindle.context` 能读到 `{ pluginId, pluginVersion, action, bookIds, language, appVersion, theme }`；`action` 是操作 id，独立应用页为 `null`。
5. 插件调用 `updateBook` 后，主窗口书库会刷新；没有修改时关闭窗口不会产生额外请求。

### 改成自己的插件

1. 复制 `examples/book-info` 到新目录，例如 `~/Developer/my-plugin`。
2. 修改 `manifest.json`：换 `id`、`name`、`version`、`author`、`description`，把操作 `id`/`title` 改成自己的入口；需要多本书同时处理时保留 `"multiple": true`。
3. 按功能补充权限：写回元数据用 `"library": "write"`；保存设置加 `"storage": true`；联网加 `"network": []` 或具体来源；保存密码加 `"secrets": true`。
4. 修改 `ui/app.html` 和 `ui/app.js`。可以拆成多个 JS/CSS 文件，宿主会按页内相对路径内联。
5. 打包、安装、测试；修改后递增 `version`，重新导入覆盖旧版本。

### 常用能力速查

| 需求 | 调用 | 权限 |
|---|---|---|
| 读选中书籍 | `library.getBooks()` | `library: read` |
| 搜索整个书库 | `library.search(options)` / `library.getBook(id)` | `libraryAll` |
| 读/建/改分组 | `library.getCollections` / `createCollection` / `addToCollection` / `removeFromCollection` | `libraryAll`（修改还需 `library: write`） |
| 读进度/划线/书签/统计 | `reading.getProgress/getAnnotations/getBookmarks/getStats(bookId)` | `libraryAll` |
| 新增划线/书签 | `reading.addAnnotation` / `reading.addBookmark` | `readingWrite` + `library` |
| 读目录和章节正文 | `book.getToc(bookId)` / `book.getChapterText(bookId, index)` | `content` + `library` |
| 读书籍原始文件 | `library.readFile(bookId, { offset, length })` | `bookFiles` + `library` |
| 打开阅读器并定位 | `reader.open(bookId, { chapterIndex, offset })` | — |
| 调用用户配置的 AI | `ai.status()` / `ai.complete(prompt, options)` | `ai` |
| 检测宿主是否支持某方法 | `kkindle.has("book.getToc")` / `kkindle.capabilities` | — |
| 提供词典/翻译/后台任务 | `register.dictionary` / `register.translator` / `register.background` | 贡献点 + 实际用到的权限 |
| 选择并导入本地文件 | `ui.pickFiles()` → `library.importFiles(paths)` | `import` |
| 保存文件 / 剪贴板 | `ui.saveFile` / `ui.copyText` / `ui.readText` | — |
| 监听书库、阅读数据和主题变化 | `events.on('library.changed' / 'reading.changed' / 'theme.changed', handler)` | — |
| 写回字段 | `library.updateBook(id, patch)` | `library: write` |
| 读/写封面 | `library.getCover(id)` / `library.setCover(id, dataUrl)` | `library` |
| 保存设置 | `storage.get/set/remove` | `storage` |
| 保存账号密码 | `secrets.get/set/remove` | `secrets` |
| 请求网页/JSON | `http.fetch(url, options)` | `network` |
| 追加站点访问 | `network.requestAccess(origin)` | `network` |
| 下载电子书入库 | `library.importDownload(url, { fileName })` | `import` |
| 整本翻译/注音 | `library.translateBook` / `library.annotatePinyin` | `translation` / `pinyin` + `library` |
| 进度事件 | `events.on('import.progress' / 'task.progress', handler)` | 对应功能 |
| 窗口标题/提示/关闭 | `ui.setTitle` / `ui.notify` / `ui.close` | — |
| 用系统浏览器打开链接 | `ui.openExternal(url)` | — |

设置页是独立窗口，同样能使用 `storage`、`secrets`、`ui`；保存的设置由匹配窗口或应用页自行读取。应用页适合放搜索、列表这类不依赖选中书籍的功能。

### 打包与安装

```sh
dotnet run --project tools/Kkindle.PluginTool -- pack examples/book-info -o dist/
# 或旧脚本：scripts/package-plugin.sh examples/book-info dist/
```

打包时保证 `manifest.json` 位于压缩包根部，并排除 `.git`、`node_modules`、`.DS_Store` 等无关文件。然后在 设置 → 高级 → 插件 → **导入插件…** 选择产物；确认权限后安装。安装后的副本在程序根目录的 `plugins/<id>/`，插件数据在 `plugin-data/<id>/`，两者都不参与同步和备份。

### 调试与排错

- **先跑校验**：`kkplugin validate <目录或 .kkplugin>` 用应用自己的解析代码检查清单、页面和脚本文件、签名，并列出权限、贡献点和语言；它能通过，应用就能安装。
- **脱离应用开发页面**：在页面里先引入 `sdk/kkindle-mock.js`，用浏览器打开即可调用全部接口（数据是假的，存储用 `localStorage`）。在 `window.KKINDLE_MOCK` 里可以改上下文、权限、书籍和 AI 回复；`kkindleMock.lookup("词")`、`kkindleMock.translate("text")`、`kkindleMock.runBackground()` 可以直接调试脚本里注册的处理函数。在应用里 `window.kkindle` 已存在，mock 不会生效。
- **页面打不开**：先在插件列表确认版本、操作页路径和权限是否被拒绝；安装/升级失败时宿主会显示解析错误。
- **开发者模式**：在 设置 → 高级 → 插件 里打开「插件开发模式」，之后新打开的插件窗口会：
  - 允许在页面内右键打开 WebView 开发者工具；
  - 在窗口顶部显示「重新加载」和「清空日志」按钮；
  - 在窗口底部显示页面日志，捕获 `console.*`、未捕获异常和未处理的 Promise rejection；
  - 在插件设置里显示「从文件夹安装…」，可以直接安装未打包的开发目录（自动忽略 `.git`、`node_modules` 等目录）。
  开发者模式只对新窗口生效，关闭插件窗口再打开即可。发布前请关闭它。
- **接口报错**：所有桥接调用都会 reject，把 `error.message` 显示出来；常见原因是缺少权限、请求了未批准的域名、修改字段不合法。
- **网络问题**：先确认应用设置里的网络开关，再确认 `permissions.network` 声明或运行时授权；HTTP 错误码由 `http.fetch` 的结果返回，不会自动抛异常。
- **改完没生效**：关闭插件窗口重新打开；设置页保存的值通常在下一次打开匹配窗口时读取。升级后仍显示旧内容时，检查是否导入了更高版本、以及 id 是否与原来一致。

### 开发工具：kkplugin

```sh
dotnet run --project tools/Kkindle.PluginTool -- help
```

| 命令 | 作用 |
|---|---|
| `validate <目录或 .kkplugin>` | 用应用的解析代码校验，列出权限、贡献点、语言和签名 |
| `pack <目录> [-o 输出目录] [--key 私钥.pem] [--publisher 名称]` | 打包为 `.kkplugin`，给了 `--key` 时顺便签名 |
| `sign <插件.kkplugin> --key 私钥.pem [--publisher 名称]` | 给已有的包签名（会替换旧签名） |
| `verify <插件.kkplugin>` | 只校验签名 |
| `keygen [-o 目录]` | 生成 ECDSA P-256 密钥对：`kkplugin-<密钥 ID>.pem`（私钥，权限 600）和 `.pub` |
| `index <目录> --base-url https://…/plugins/ [-o index.json]` | 为目录里所有 `.kkplugin` 生成插件市场索引，并用应用的解析器复核 |

TypeScript 项目把 `sdk/kkindle.d.ts` 复制到工程里（或在 `tsconfig.json` 的 `include` 里引用），`kkindle.*` 即有完整类型和注释；JavaScript 文件也可以在开头写 `/// <reference path="kkindle.d.ts" />` 获得编辑器补全。

### 签名

签名文件 `signature.json` 位于包根部，记录插件 id、版本和包内每个文件（除自身外）的 SHA-256，用 ECDSA P-256（ES256）签名。任何文件被修改、增加或删除，签名都会失效，应用拒绝安装。

| 来源 | 含义 |
|---|---|
| 随应用附带 | 从安装包的 `bundled-plugins/` 安装 |
| 官方签名 | 用 Kkindle 官方密钥签名（公钥写在 `PluginSignature.OfficialKeys`） |
| 已签名：发布者 | 用第三方密钥签名。应用不认识这把钥匙，但会记住它：之后的升级换了密钥会醒目警告 |
| 未签名 | 没有 `signature.json`，仍可安装，确认框会标明 |

第三方开发者：`kkplugin keygen` 生成自己的密钥，之后每次发布都用同一把私钥签名。**私钥丢失就无法再以同一身份发布更新**（用户升级时会看到“签名者变了”的警告），请离线备份，不要提交到仓库。

### 插件市场与索引

插件市场读取一个静态 JSON 索引，可以放在任何 HTTPS 静态站点上：

```json
{
  "format": 1,
  "generatedAt": "2026-11-07T00:00:00Z",
  "plugins": [
    {
      "id": "com.kkindle.book-translation",
      "name": "书籍翻译与注音",
      "version": "1.2.0",
      "description": "……",
      "author": "Kkindle",
      "homepage": "https://kkindle.stacker.beauty/",
      "minAppVersion": null,
      "trust": "official",
      "download": "https://kkindle.stacker.beauty/plugins/book-translation.kkplugin",
      "sha256": "64 位十六进制",
      "size": 123456,
      "i18n": { "en-US": { "name": "Book Translation & Pinyin", "description": "……" } }
    }
  ]
}
```

- 必填：`id`、`name`、`version`、`download`、`sha256`；缺字段或格式不对的条目会被跳过，同一个 id 只取第一条。
- `download` 可以是相对索引地址的路径；必须是 `https://`（开发时可以是回环地址或 `file://`，但要和索引同类）。
- 下载后先核对 `sha256` 和 `size`，再走正常的校验、签名和权限确认；索引里的 `trust` 只是给人看的说明，应用不读取它，来源一律以包里的签名为准。
- 包上限与手动导入相同；`format` 大于 1 的索引会提示需要更新应用。

官方插件的发布流程：

```sh
scripts/publish-plugins.sh                    # plugins/ 下全部插件
scripts/publish-plugins.sh book-translation   # 只发布一个
```

脚本用官方私钥（环境变量 `KKINDLE_PLUGIN_SIGNING_KEY` 指定，默认 `~/.config/kkindle/plugin-signing/official-p256.pem`）打包签名到 `website/plugins/`，再生成 `website/plugins/index.json`；之后照常运行 `scripts/build_website.sh` 部署网站即可。发布前记得递增插件版本号。

### 发布前检查

- 权限只声明真正用到的项；描述里写清楚联网做什么。
- 没有把网站地址写死在插件里；需要站点时让用户填写。
- 空书库、单本/多本、请求失败、字段为空都有对应的界面状态。
- 所有外部文本都用 `textContent` 写入；不拼接 HTML。
- 浅色和深色主题下都检查过；窗口缩到最小尺寸时不溢出。
- 版本号已递增；从干净目录打包；用旧版本升级安装验证过一次。
- `kkplugin validate` 通过；用了新接口时写好 `minAppVersion`，或先用 `kkindle.has()` 检测。
- 有英文用户时补上 `i18n.en-US`，页面按 `kkindle.context.language` 切换文案。
- 打算长期发布的插件用自己的密钥签名，并备份私钥。

## 现有插件源码

仓库中保留的插件源码在 `plugins/`，其中 `book-translation` 由 `src/Kkindle.App/Kkindle.App.csproj` 复制到输出目录的 `bundled-plugins/`，随桌面版发布（修改后记得递增 `manifest.json` 的版本号，老用户才会收到更新）；书籍详情匹配和在线书库的源码单独维护，不在本仓库中：

- “书籍详情匹配”插件（id `com.kkindle.book-details-match`），搜索在线图书条目，展示候选和详情对比，按字段勾选写回书名、作者、丛书、出版社、出版年、ISBN、页数、装帧、简介、评分和封面。插件设置中填写包含 `{query}`（也可用 `{title}`、`{author}`、`{isbn}`）的搜索地址模板；字段和封面从搜索结果及条目页面自动识别，不依赖特定网站，插件包内不包含任何网站地址。批量请求间隔默认 30 秒，同一本书的页面和详情之间使用 3 秒间隔。第一次打开插件设置页时会显示免责声明，同意后不再显示。
- `plugins/book-translation`：“书籍翻译与注音”插件（id `com.kkindle.book-translation`），书籍右键菜单里提供“翻译书籍”和“生成注音书”两个操作，分别选择翻译服务、语言、输出模式、注音引擎/样式，展示进度并支持取消；设置页保存默认选项。翻译和注音都使用宿主引擎，结果直接加入书库。
- “在线书库”插件（id `com.kkindle.online-library`），打开后在页内“设置”中填写服务地址和账号，同一页面登录、搜索、分页浏览，按行下载并直接加入书库，支持下载进度显示和下载前的确认提示。服务地址由用户填写，插件包内不包含任何网站地址。首次打开时会显示免责声明，同意后不再显示。

```sh
scripts/package-plugin.sh plugins/book-translation dist/
```

## 限制

- 只在桌面端可用（Windows、macOS、Linux）。移动端没有插件运行时。
- 插件下载只经过宿主，单个响应 10 MB、单本电子书 500 MB 为上限；插件不能直接写入书库目录。
- 插件页面不能自己打开子窗口，也不能留在窗口之外的导航。
- Linux 的插件窗口依赖 WebKitGTK/WPE，界面渲染需要单独验证；不可用时只能使用设置页之外的宿主界面。
- 暂不提供「元数据来源」贡献：主界面的在线元数据流程没有暴露给插件。需要匹配书籍信息时，请使用 `contributes.bookActions` 打开自己的页面（现有“书籍详情匹配”插件即此模式）。
- 插件页面只在窗口打开期间运行，窗口关闭后页面逻辑停止。需要定时工作时用 `contributes.background` 脚本（最短间隔 15 分钟，单次 2 分钟内完成）；耗时的整本书翻译、注音和模型下载由宿主执行，关闭窗口前会询问是否取消。
- 词典、翻译和后台脚本没有 DOM，不能弹出界面，也不能调用整本书翻译/注音任务。
- `contributes.commands` 目前显示在书库工具栏的「插件工具」菜单中，macOS 原生菜单栏暂不追加插件项。

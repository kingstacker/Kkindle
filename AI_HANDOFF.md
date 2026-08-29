# Kkindle 项目交接文档

> 供后续 AI 和开发者快速接手。本文只记录当前有效状态，不保留已完成的迁移流水账。
>
> 更新时间：2026-08-29
>
> 本次验证目录：/home/stacker/work_pro/Kkindle

## 1. 当前状态

- Kkindle 是 C# / .NET 10 / Avalonia 12.1.1 跨平台桌面应用，Windows、Linux、macOS 各有一个瘦启动项目。
- Avalonia 是唯一 UI 实现；src/Kkindle.App.WinUI 已完整删除。4 个阅读器脚本和应用图标已迁入 src/Kkindle.App，解决方案、测试及三端打包引用均已更新。
- 当前开发版本为 0.6.0-dev.6，统一定义在 Directory.Build.props。关于页通过程序集 AssemblyInformationalVersion 显示版本，不再维护 UI 硬编码版本号。
- 当前工作分支为 `native-engine`（自绘排版引擎已落地，见第 9 节），主线为 `master`，远程为 git@github.com:kingstacker/Kkindle.git。Route A 的未提交改动在 `git stash@{0}`。
- 2026-08-29 落地：Kreader 的 EPUB 排版完全切换到自绘引擎（`src/Kkindle.Layout` + `NativeReaderHost`），WebView 排版脚本与桥注入已删除；PDF 仍走平台 WebView。详见第 9 节。
- 当前 `global.json` 固定 .NET SDK 10.0.400；Avalonia 主包和桌面包为 12.1.1，Avalonia.Controls.WebView 为 12.1.0。
- 2026-08-29 验证结果：Linux Debug 构建 0 警告、0 错误；可移植测试 286 项全部通过（含 9 项 `KkindleLayoutEngineTests` 自绘引擎测试；54 项 WebKit 脚本测试随排版路径移除而删除）。Kreader 验证 harness 的 WebKit 断言已删除并留桩（运行输出 SKIPPED），待按 PageModel 重写（见 9.4）。
- Linux Debug 入口为 `src/Kkindle.Desktop.Linux/bin/Debug/net10.0/Kkindle`。调试产物不提交 Git；运行时必须保留同目录的 DLL、PDB、WebView 和资源文件，不能只复制入口文件。
- Linux 真实桌面启动和 Calibre 转换已验收；Windows/macOS 真实桌面、真实 Kindle 设备和 macOS 签名/公证仍未完整验收。

## 2. 项目结构

    Kkindle/
    ├─ src/Kkindle.App/                Avalonia UI、阅读器及共享脚本
    ├─ src/Kkindle.Layout/             自绘排版引擎（HarfBuzz shaping + Skia 绘制，零 UI 依赖）
    ├─ src/Kkindle.Core/               模型、接口和领域策略
    ├─ src/Kkindle.Infrastructure/     SQLite、字典、转换、缓存、网络服务
    ├─ src/Kkindle.Platform.Common/    挂载磁盘型 Kindle、跨平台公共实现
    ├─ src/Kkindle.Platform.Windows/   WPD/MTP、DPAPI、WM_DEVICECHANGE
    ├─ src/Kkindle.Platform.Linux/     Secret Service、udisksctl、XDG 路径
    ├─ src/Kkindle.Platform.MacOS/     Keychain、diskutil、Application Support
    ├─ src/Kkindle.Desktop.Windows/    Windows 启动头
    ├─ src/Kkindle.Desktop.Linux/      Linux 启动头
    ├─ src/Kkindle.Desktop.MacOS/      macOS 启动头
    ├─ tests/Kkindle.Tests/            可移植测试
    ├─ tests/Kkindle.Tests.Windows/    Windows/WPD 测试
    └─ tests/Kkindle.Platform.Common.Tests/ 挂载设备公共层测试

数据目录：

- Windows：EXE 旁的 data/、backups/、app-root.json。
- Linux：$XDG_DATA_HOME/Kkindle，配置在 $XDG_CONFIG_HOME/Kkindle。
- macOS：~/Library/Application Support/Kkindle。

## 3. 当前功能

### 3.1 书库与阅读器

- 本地书库支持 EPUB、PDF、MOBI、AZW3 导入，SHA-256 去重，同书多格式合并，元数据/封面编辑、搜索筛选、收藏和分类。
- Linux 文件管理器拖放支持 `text/uri-list` 与 GNOME 文件拖放格式；拖入文件夹会递归收集 EPUB、PDF、MOBI、AZW3，并去重后导入。
- Kreader 支持 EPUB、PDF 和 AZW3 临时转 EPUB；包括分页、双栏、滚动、书签、搜索、划线批注、脚注、AI、阅读统计、禅模式及进度恢复。
- Linux 竖排由自绘引擎按固定网格排版：字格 1em、列距 = 行距、首列缩进 2 根、数字 1 位正立 / 2 位合字 / 3 位及以上与拉丁为原子侧排 run、收标点悬挂进页边距；横排为两端对齐 + 2em 首行缩进。EPUB 全部经 `NativeReaderHost`（HarfBuzz + Skia 自绘）渲染，WebKitGTK 排版兼容层与桥注入已删除，实现细节与后续工作见第 9 节。
- Linux 文本回退阅读器（`ReaderLinuxTextFallback*`，`UseLinuxPlainTextRecoveryFallback=false`）已被自绘引擎取代：源码与测试保留仅供诊断，计划整体清理（见 9.4）。
- 极简目录使用细线矩形三横图标，收起/展开按钮与字体按钮视觉对齐。
- 极简目录章节浮窗异步读取 EPUB 章节正文前 4 个非空行；按章节路径缓存，切换书籍时清空。PDF 只显示页码。
- 阅读统计图表的竖线已改为细线。
- 一键豆瓣匹配会先显示与其他弹窗一致的确认提示，明确“不推荐频繁使用”；两次批量匹配至少间隔 30 秒。
- 豆瓣搜索解析搜索页内嵌 `window.__DATA__` 的全部条目（每页约 15 条，不再截断前 10 条）；单本匹配与批量匹配共用标题/作者清理逻辑（去副标题、括号附加信息、多作者），带作者无结果时自动回退纯标题重搜。
- 候选选择弹窗带关键词输入框和“搜索”按钮（支持回车），没有合适条目时可以直接改词重搜，弹窗保持打开；触发访问验证的错误仍会中断整个匹配流程。

### 3.2 Kindle 连接与缓存

- 连接 Kindle 后立即预读书籍、字体、字典和 My Clippings.txt，无需先切换到对应页面。
- 字体、字典和标注快照写入 data/kindle-device-cache.json，按 KindleDevice.Identity（串码/稳定设备身份）隔离。
- 同一设备保持连接时，页面切换优先使用内存和串码缓存；书籍、资源或标注发生变更时只刷新对应数据，不重复扫描全部文件。
- 设备断开或身份改变时清理当前内存快照；重新连接会加载该设备持久化快照并后台更新。
- Windows 支持 USB 磁盘及 WPD/MTP Kindle；Linux/macOS 当前仅支持挂载为文件系统的 Kindle。

### 3.3 标注、封面和导出

- Kindle My Clippings.txt 支持简体/繁体中文、英语、日语、韩语类型和日期解析。
- Kindle 将划线与笔记写成两个块；显示层会按同书和相邻位置配对，作为一条“划线与笔记”展示。删除时会一次删除配对的两个源块。
- “显示全部”按书名合并电脑书库和 Kindle 书库的同一本书，子项显示来源；按单一来源筛选时仍分开显示。
- 阅读资料封面优先使用本地书库封面，其次使用 Kindle 扫描封面。标题匹配会去除括号附加信息并做规范化：去括号后相等即视为同书（短书名如“三体”也能命中），包含关系仍要求较长标题。打开页面刷新时会确保 Kindle 书籍已扫描（同一设备重连不会再触发自动扫描，此前会导致封面全部缺失）。
- Kindle 书籍没有内嵌封面时，会读取书籍声明的 Kindle 系统缩略图；WPD/MTP 和挂载磁盘两种通道均支持。
- 导出模式支持全选/取消全选，只导出实际勾选项；未选中的记录不会进入 Markdown 或纯文本结果。

### 3.4 词典

- Kreader 本地词典支持 MDX、AZW、AZW3、MOBI、KFX、TXT、TSV、CSV。
- MDX 由仓库内带许可证的 ThirdParty/MdxParser 解析。
- Kindle 词典通过 Calibre 转为临时 EPUB 后提取词头与释义；不支持 DRM 词典。
- 临时转换目录在完成或失败后清理，单个坏词条不会破坏已解析词条。

### 3.5 Calibre、KFX 和版本

- 三端发行包都不捆绑 Calibre 或 KFX Input。
- Calibre 路径统一由 CalibreExecutableLocator 发现，禁止在业务代码中写死 .exe。
- 如果用户误把 `calibre`/`calibre.exe` 主程序配置为转换器，定位器会自动解析同目录的 `ebook-convert`；设置页文件选择器也只允许选择 `ebook-convert`。
- 检测到 Calibre 后只禁用 Calibre 下载/安装按钮；KFX Input 按钮必须通过 calibre-customize --list-plugins 确认插件已安装后才禁用。
- KFX Input 插件包校验要求导入标记文件与同目录 `__init__.py` 配对，避免误把依赖目录初始化文件当作插件入口。
- Kkindle 自己安装 KFX Input 时才使用独立 CALIBRE_CONFIG_DIRECTORY，已有用户配置保持不变。
- GitHub Release 工作流根据提交记录调用 GitHub generate-notes API 生成 Changelog；创建和更新 Release 都使用同一份自动说明。

## 4. 关键约束

### 4.1 WebView 与阅读器

- EPUB 落盘时必须移除 script、iframe、on*、javascript:、外部资源，并注入 nonce CSP。不要扩大导航白名单或重新启用书籍自带脚本。
- Windows WebView2 指针由 Avalonia 管理。禁止对 Avalonia 提供的指针调用 Marshal.ReleaseComObject，也不要反射 Avalonia 内部 COM 类型，否则打开书籍可能原生闪退。
- 阅读器导航必须经过现有意图、序列号、取消令牌和单消费者 gate；不要从 WebView 回调直接并发调用切章。
- EPUB 排版由 src/Kkindle.Layout（HarfBuzz+Skia，零 UI 依赖）与 src/Kkindle.App/NativeReaderHost.cs 承担；宿主输入输出走与旧桥相同的 JSON 协议（scroll/selection/pageClick/wheel/key/link/footnoteHover），改协议必须同步 HandleReaderBridgeMessage 与 NativeReaderHost.Emit。
- SkiaSharp/HarfBuzzSharp 的 Linux native 库必须显式引用（metapackage 不含）：见 Kkindle.Layout.csproj。升级引擎库 = 独立 PR + 重跑 KkindleLayoutEngineTests 确定性快照。

### 4.2 Kindle 数据

- 所有设备文件操作必须经过根路径白名单，拒绝 ..、符号链接/reparse point 逃逸和目录目标。
- 发送到 Kindle 时若已存在同名书籍会直接替换（临时文件、SHA-256 校验、原子改名；WPD 先删除旧项再拷贝），让更新后的封面/元数据到达既有条目——此前生成 "(2)" 副本导致豆瓣新封面永远到不了 Kindle。导出和资源传输仍使用唯一文件名，不覆盖已有文件。
- 发送时会把书库封面（如豆瓣匹配封面）作为 `--cover` 传给 Calibre 转换，并优先用它生成 `system/thumbnails` 书架缩略图，覆盖书籍文件内嵌的旧封面。
- 允许访问 Kindle system/thumbnails 中由书籍元数据明确引用的缩略图；不要读取或修改其他 Kindle 内部数据库。
- 删除 Kindle 标注只改写 My Clippings.txt，不修改云端标注或书籍侧车数据库。
- 新增缓存必须绑定稳定设备身份，不能用盘符或页面状态作为唯一键。

### 4.3 UI、数据与版本

- 默认视觉严格使用黑、白、灰，直角矩形、细线、无渐变和强阴影。除非用户明确要求，不引入彩色或圆角卡片。
- Slider 和 ComboBox 事件可能在 AXAML 初始化时触发，处理器必须保留空值/就绪守卫。
- 不改变 App.axaml 资源合并层级，不重做窗口 chrome 初始化顺序。
- SQLite 新表使用 CREATE TABLE IF NOT EXISTS；旧表加列必须先检查 PRAGMA table_info。
- 应用版本只在 Directory.Build.props 维护；关于页读取程序集版本。发版标签、构建版本和显示版本必须一致。
- 发布包不得恢复 Calibre/KFX Input 捆绑，也不得处理或绕过 DRM。

## 5. 构建与验证

仓库 `global.json` 当前固定 SDK 10.0.400。Linux 本机验证命令：

    cd /home/stacker/work_pro/Kkindle

    dotnet test tests/Kkindle.Tests/Kkindle.Tests.csproj --no-restore
    dotnet build src/Kkindle.Desktop.Linux/Kkindle.Desktop.Linux.csproj --no-restore

结果：286 项测试通过（含 9 项自绘引擎测试）；Linux Debug 构建 0 警告、0 错误。2026-08-22 还通过 Debug UI 使用系统 `ebook-convert` 实际完成两本 EPUB→AZW3 转换，并额外验证了配置指向 `calibre` 主程序时的自动纠正。

Windows 验证从临时目录执行绝对项目路径，以确保使用仓库锁定的 SDK：

    cd C:\Users\kings\AppData\Local\Temp

    dotnet test C:\Users\kings\Desktop\01_Projects\Kkindle\tests\Kkindle.Tests\Kkindle.Tests.csproj -c Debug --no-restore
    dotnet test C:\Users\kings\Desktop\01_Projects\Kkindle\tests\Kkindle.Tests.Windows\Kkindle.Tests.Windows.csproj -c Debug -p:Platform=x64 --no-restore
    dotnet test C:\Users\kings\Desktop\01_Projects\Kkindle\tests\Kkindle.Platform.Common.Tests\Kkindle.Platform.Common.Tests.csproj -c Debug --no-restore

    dotnet build C:\Users\kings\Desktop\01_Projects\Kkindle\src\Kkindle.Desktop.Windows\Kkindle.Desktop.Windows.csproj -c Debug -p:Platform=x64 --no-restore -t:Rebuild
    dotnet build C:\Users\kings\Desktop\01_Projects\Kkindle\src\Kkindle.Desktop.Linux\Kkindle.Desktop.Linux.csproj -c Debug --no-restore -t:Rebuild
    dotnet build C:\Users\kings\Desktop\01_Projects\Kkindle\src\Kkindle.Desktop.MacOS\Kkindle.Desktop.MacOS.csproj -c Debug --no-restore -t:Rebuild

每次代码修改后必须重新生成 Windows x64 Debug，并同步完整目录：

    dotnet build C:\Users\kings\Desktop\01_Projects\Kkindle\src\Kkindle.Desktop.Windows\Kkindle.Desktop.Windows.csproj -c Debug -p:Platform=x64 --no-restore

    robocopy C:\Users\kings\Desktop\01_Projects\Kkindle\src\Kkindle.Desktop.Windows\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64 C:\Users\kings\Desktop\01_Projects\Kkindle\artifacts\Kkindle-debug-win-x64-latest /E /XF kkindle-crash.log /XD data backups

robocopy 返回码 0 至 7 都表示成功。同步时必须保留目标目录的 data/、backups/、kkindle-crash.log 和 WebView2 用户数据。

## 6. 发布

- Windows：scripts/Build-Release.ps1。
- Linux：scripts/build-linux-release.sh，输出 .deb 和 tar.gz。
- macOS：scripts/build-macos-release.sh，输出 .app tar.gz，支持 Developer ID 和 notarization。
- 推送 vX.Y.Z 或预发布标签触发 .github/workflows/release.yml，三端构建完成后聚合产物、校验和及自动 Changelog。
- Windows 安装包尚未配置代码签名；macOS 公开分发仍需要 Developer ID 与公证凭据。

## 7. 已知限制

- Linux/macOS 的窗口、字体、WebKit 阅读器、PDF、挂载 Kindle、密钥环和安全弹出尚未真机验收。
- MTP-only Kindle 只在 Windows 支持；Linux/macOS 只支持文件系统挂载设备。
- PDF 使用平台 WebView 内置查看器，选择、缩放、点击区域翻页等能力受引擎限制。
- 自绘引擎当前恒为分页：连续滚动（FlowMode=0）与双栏暂以分页呈现（见 9.4）；PDF 仍使用平台 WebView 内置查看器。
- Kindle/KFX 字典和书籍不处理 DRM。
- 当前 Windows 环境没有可用 WSL 发行版，因此只能编译 macOS 项目，不能在本机执行 bash -n scripts/build-macos-release.sh 或验证 .app 签名。
- Windows/Linux/macOS Rebuild 可能报告 MainWindow.ReaderInteraction.cs 中两个 Linux 文本回退字段未使用的 CS0169 警告；当前不影响构建。

## 8. 工作约定

- 工作区可能包含用户尚未提交的修改，不要回滚不属于当前任务的变更；用户明确要求“提交全部改动”时，仍不要提交 `.claude/settings.local.json` 等本机权限配置。
- 默认只生成 Windows x64 Debug；除非用户明确要求，不生成 Release、安装包、便携包或 GitHub Release。
- 单文件导入、转换或封面解析失败不能升级为整批失败。
- 修改行为时同步更新测试和本文档；提交前运行 git diff --check。
- 不提交 artifacts/、bin/、obj/、本地数据、缓存、日志、密钥或账号信息。

## 9. 自绘排版引擎（Kreader Native Engine，2026-08-29 已落地）

### 9.1 当前状态：已切换，WebView 排版已移除

- **EPUB 排版已完全由自绘引擎承担**：加载、shaping、断行、禁则、分页、绘制全部在 `src/Kkindle.Layout`（纯 C#，HarfBuzzSharp 8.3.1.3 + SkiaSharp 3.119.4，三端同一套 native 库）；`src/Kkindle.App/NativeReaderHost.cs` 是 Avalonia 宿主，实现 `IReaderHost` + `IReaderPageSnapshotProvider`，把输入翻译成与原 WebKit 桥一致的 JSON 协议（`scroll`/`selection`/`pageClick`/`wheel`/`key`/`link`/`footnoteHover`），MainWindow 的批注工具栏、脚注弹窗、进度、书签、TOC、整本书搜索全部复用。
- **WebView 排版机制已删除**：五个脚本库（ReaderPaginationScripts/ReaderNavigationScripts/ReaderVerticalPageScripts/ReaderAppearanceScripts/ReaderWaveScripts）、三个脚本测试文件、`EpubReaderPreparationService` 的桥脚本与 CSP 注入全部移除；提取缓存格式 bump 到 **69**（旧缓存自动重建）。PDF 仍走平台 WebView 内置查看器（`NativeWebViewReaderHost` 仅为 PDF 保留）。
- 阅读器宿主按打开格式选择：EPUB → `NativeReaderHost`，PDF → `NativeWebViewReaderHost`（`EnsureReaderHostsAsync` 按类型重建，Windows 的预加载宿主同样按格式创建）。
- 测试：`KkindleLayoutEngineTests` 9 项（横排分页覆盖/无越界、禁则上收法、竖排数字规则、竖排偏移单调、跨次排版 JSON 逐字节一致、命中测试往返、脚注隐藏、出版社 CSS 强调、真实字体渲染冒烟）；全套可移植测试 286 项通过，Linux Debug 构建 0 警告 0 错误。
- **Route A 已被取代**：`page-compose-wip` 分支与其未提交改动保存在 `git stash@{0}`（route-A-wip-backup before native engine cutover），不再合并。

### 9.2 引擎红线（延续原决策，全部已实现）

- 度量与绘制同一份代码：advance 来自 HarfBuzz shaping（float，units/upem 显式换算），绘制用同一字形 id 画（HarfBuzz 与 Skia 读同一字体文件，索引一致）。
- 字体：捆绑京华老宋体（`Assets/Fonts/KingHwaOldSong-v3.0.ttf`，竖排优化字体，含 vert 特形）；`TypesetFontLibrary` 管理字体与回退链，系统字体栈不参与排版。
- 依赖钉死：SkiaSharp 3.119.4 / HarfBuzzSharp 8.3.1.3；**注意两个 metapackage 的 nuspec 不含 Linux native，必须在消费工程显式引用 `SkiaSharp.NativeAssets.Linux` 与 `HarfBuzzSharp.NativeAssets.Linux`**（Kkindle.Layout 已引用，测试与 App 经传递获得）。
- 竖排是自研固定网格：字格 1em、列距 = 行距、首列缩进 2em、标题加粗居中且前后各空一列、数字规则与旧实现一致（1 位正立、2 位合字、3+/拉丁为原子侧排 run）、收标点悬挂进下页边距、开标点不落列尾；HarfBuzz TTB 只用于取 vert 竖排字形。
- 横排：行高网格、两端对齐（inter-character 均摊）、`text-indent: 2em`、禁则用"上收法"（收标点放不进时连同上一格下移，开标点不落行尾）；与 WebKit 的差异仅是行尾不悬挂（竖排仍悬挂）。

### 9.3 位置与偏移约定（兼容性关键）

- 批注/搜索/进度的字符偏移沿用 body textContent 约定：`XhtmlChapterLoader` 逐字拼接所有正文文本节点（含隐藏脚注定义与 ruby rt 的 ghost 文本），与 WebKit 的 textContent 一致，旧批注数据直接命中。
- 进度 `ScrollPosition`：竖排 = 页首字符偏移；横排分页 = 页号 × 视口宽（`NativeReaderHost.GetScrollState` 上报，`scroll` 消息语义与原桥相同，竖排取绝对值）。
- `EpubReaderPreparationService` 的净化（script/iframe/on*/外链/CSS url）保留，注入类工作全部移除；脚注标记 `<sup class="kkindle-footnote-marker">` 注入保留。

### 9.4 后续工作（按优先级）

- **真机验证**：Linux Debug 入口打开真实 EPUB（横排 + 竖排各一）人工核对渲染；Windows/macOS 构建验收（引擎三端确定性依赖同字体同库，理论上一致，需实测 PageModel JSON 快照）。
- **翻页动画**：当前 native 面板用 Avalonia 透明度渐变（140ms 出 / 200ms 入）；slide/墨水动画改用 `CaptureVisiblePageAsync`（已实现，PNG 快照）+ Avalonia 覆盖层复刻（`ReaderLinuxFallbackTransitionPlayer` 的三档实现可参考，其 WaveSweepMs 已内联 230）。
- **验证 harness 重写**：`MainWindow.KreaderValidation.cs` 的 WebKit 竖排断言已删除并留桩（SKIPPED），需按 PageModel/页面快照重写 KKINDLE_KREADER_VALIDATE 系列。
- **连续滚动与双栏（FlowMode=0 / TwoPageMode）**：native 宿主当前恒为单页分页，两个模式暂以分页呈现；后续以"虚拟页列滚动 / 双页并排"实现，或在设置页于 native 引擎下隐藏这两个选项。
- 竖排图片降级为独立页（与横排一致的整页插图槽），行内图、ruby 注音渲染（rt 已入偏移流不渲染）、表格美化、内嵌字体解混淆按 9.4 原计划 Phase 4 推进。
- Linux 文本回退阅读器（`ReaderLinuxTextFallback*`、`UseLinuxPlainTextRecoveryFallback=false`）已被引擎取代，可在下一个清理项中整体删除（其转场播放器 `ReaderLinuxFallbackTransitionPlayer` 可先留作动画参考）。
- **升级纪律**：升级 SkiaSharp/HarfBuzzSharp 必须 rebase `KkindleLayoutEngineTests` 的确定性快照并单独成 PR。

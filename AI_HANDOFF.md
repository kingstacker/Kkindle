# Kkindle 项目交接文档

> 供后续 AI 和开发者快速接手。本文只记录当前有效状态，不保留已完成的迁移流水账。
>
> 更新时间：2026-08-22
>
> 本次验证目录：/home/stacker/work_pro/Kkindle

## 1. 当前状态

- Kkindle 是 C# / .NET 10 / Avalonia 12.1.1 跨平台桌面应用，Windows、Linux、macOS 各有一个瘦启动项目。
- Avalonia 是唯一 UI 实现；src/Kkindle.App.WinUI 已完整删除。4 个阅读器脚本和应用图标已迁入 src/Kkindle.App，解决方案、测试及三端打包引用均已更新。
- 当前开发版本为 0.6.0-dev.3，统一定义在 Directory.Build.props。关于页通过程序集 AssemblyInformationalVersion 显示版本，不再维护 UI 硬编码版本号。
- 当前工作分支为 `master`，已合并 `origin/codex/fix-calibre-output-profile`，远程为 git@github.com:kingstacker/Kkindle.git。
- 当前 `global.json` 固定 .NET SDK 10.0.400；Avalonia 主包和桌面包为 12.1.1，Avalonia.Controls.WebView 为 12.1.0。
- 2026-08-22 验证结果：.NET 10 全解决方案构建 0 警告、0 错误；Windows、可移植及平台公共测试共 276 项全部通过。Linux Debug 版此前已实际通过 EPUB→AZW3 转换，书库中两本测试书均显示 `AZW3 · EPUB`。
- Linux Debug 入口为 `src/Kkindle.Desktop.Linux/bin/Debug/net10.0/Kkindle`。调试产物不提交 Git；运行时必须保留同目录的 DLL、PDB、WebView 和资源文件，不能只复制入口文件。
- Linux 真实桌面启动和 Calibre 转换已验收；Windows/macOS 真实桌面、真实 Kindle 设备和 macOS 签名/公证仍未完整验收。

## 2. 项目结构

    Kkindle/
    ├─ src/Kkindle.App/                Avalonia UI、阅读器及共享脚本
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
- Linux 文本回退阅读器支持跨页选择同步、批注范围渲染、划线样式、选择工具栏轻触关闭和分页/滚动交互；WebView 自带选择工具栏不再与 Avalonia 工具栏重复显示。
- 极简目录使用细线矩形三横图标，收起/展开按钮与字体按钮视觉对齐。
- 极简目录章节浮窗异步读取 EPUB 章节正文前 4 个非空行；按章节路径缓存，切换书籍时清空。PDF 只显示页码。
- 阅读统计图表的竖线已改为细线。
- 一键豆瓣匹配会先显示与其他弹窗一致的确认提示，明确“不推荐频繁使用”；两次批量匹配至少间隔 30 秒。

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
- 阅读资料封面优先使用本地书库封面，其次使用 Kindle 扫描封面。标题匹配会去除括号附加信息并做规范化，解决长书名无法命中。
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
- 分页时 html 是唯一滚动容器，body 必须 overflow:visible。单页 column-count:1，双栏 column-count:2，翻页步长统一使用 scrollingElement.clientWidth。
- fragment 分页断点只用 break-before: column !important，不要加入 page-break-before。
- 阅读器导航必须经过现有意图、序列号、取消令牌和单消费者 gate；不要从 WebView 回调直接并发调用切章。
- 阅读器脚本位于 src/Kkindle.App/ReaderNavigationScripts.cs、ReaderPaginationScripts.cs、ReaderAppearanceScripts.cs、ReaderWaveScripts.cs。

### 4.2 Kindle 数据

- 所有设备文件操作必须经过根路径白名单，拒绝 ..、符号链接/reparse point 逃逸和目录目标。
- 不覆盖 Kindle 上内容不同的同名书籍；发送使用临时文件、校验和及原子改名。
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

结果：237 项测试通过；Linux Debug 构建 0 警告、0 错误。2026-08-22 还通过 Debug UI 使用系统 `ebook-convert` 实际完成两本 EPUB→AZW3 转换，并额外验证了配置指向 `calibre` 主程序时的自动纠正。

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
- 竖排只支持滚动模式。
- Kindle/KFX 字典和书籍不处理 DRM。
- 当前 Windows 环境没有可用 WSL 发行版，因此只能编译 macOS 项目，不能在本机执行 bash -n scripts/build-macos-release.sh 或验证 .app 签名。
- Windows/Linux/macOS Rebuild 可能报告 MainWindow.ReaderInteraction.cs 中两个 Linux 文本回退字段未使用的 CS0169 警告；当前不影响构建。

## 8. 工作约定

- 工作区可能包含用户尚未提交的修改，不要回滚不属于当前任务的变更；用户明确要求“提交全部改动”时，仍不要提交 `.claude/settings.local.json` 等本机权限配置。
- 默认只生成 Windows x64 Debug；除非用户明确要求，不生成 Release、安装包、便携包或 GitHub Release。
- 单文件导入、转换或封面解析失败不能升级为整批失败。
- 修改行为时同步更新测试和本文档；提交前运行 git diff --check。
- 不提交 artifacts/、bin/、obj/、本地数据、缓存、日志、密钥或账号信息。

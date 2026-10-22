# Kkindle Agent Handoff

> 更新时间：2026-10-22
>
> 工作目录：`C:\Users\kings\Desktop\Kkindle`
>
> 当前分支：`dev`，跟踪 `origin/dev`
>
> 当前提交：`db7837f feat: move transfer progress into task history`
>
> 最近标签：`v1.0.14-dev.8`；当前 `HEAD` 比该标签多 1 个提交

## 当前基线

- Kkindle 是 C# / .NET 10 / Avalonia 跨平台桌面应用，支持 Windows、Linux、macOS；Windows 是主要开发和交付目标。
- `global.json` 固定 .NET SDK `10.0.400`，`Avalonia` 和 `Avalonia.Themes.Fluent` 为 `12.1.1`，`Avalonia.Controls.WebView` 为 `12.1.0`。
- 本机 SDK 位于仓库内 `.dotnet\`。系统 `dotnet` 当前只注册了 .NET 8 SDK，不能用于本项目；命令优先使用 `.\.dotnet\dotnet.exe`。
- `Directory.Build.props` 当前源码版本仍为 `1.0.13-dev.1`，但最新开发标签是 `v1.0.14-dev.8`。发版版本不能只看标签或 EXE 文件名，必须同时核对 `Directory.Build.props`、Git 标签和构建日志。
- 工作区当前仅有未跟踪的 `.dotnet-install.ps1`；`.dotnet/`、`.dotnet-home/`、`.nuget/`、`artifacts/`、`data/` 均为本地环境或运行数据，不纳入提交。
- 修改前先运行 `git status` 和 `git log --oneline -10`。工作区可能有用户修改，禁止回滚或覆盖无关改动。

## 代码地图

- `src/Kkindle.App`：Avalonia UI、书库、Kreader 交互、设置、MCP 设置、后台任务中心和宿主装配。核心窗口按功能拆在 `MainWindow.*.cs` partial 文件中。
- `src/Kkindle.Core`：领域模型、服务接口、阅读内容位置、主题、后台任务和同步模型。
- `src/Kkindle.Layout`：无 UI 依赖的 HarfBuzz + Skia 排版、断行、分页、竖排和命中测试。
- `src/Kkindle.Infrastructure`：SQLite、书籍导入与格式转换、EPUB/MOBI/AZW3/TXT、PDF 文本、词典、AI、TTS、备份、S3/WebDAV 同步。
- `src/Kkindle.Platform.Common`、`Kkindle.Platform.Windows`、`Kkindle.Platform.Linux`、`Kkindle.Platform.MacOS`：设备、文件系统、密钥链和 WPD/MTP 等平台能力。
- `src/Kkindle.McpServer`：独立 MCP 服务入口，多目标 `net8.0` 与 `net10.0-windows10.0.19041.0`。
- `src/Kkindle.Desktop.*`：Windows、Linux、macOS 启动项目。
- `tests/Kkindle.Tests`：可移植核心与基础设施测试。
- `tests/Kkindle.App.Tests`：Avalonia Headless / Skia UI 测试；默认跳过部分慢测试。
- `tests/Kkindle.Tests.Windows`：WPD/MTP 等 Windows 专用测试。
- `tests/Kkindle.Platform.Common.Tests`：跨平台公共层测试。

## 当前功能状态

- 书库支持 EPUB、PDF、MOBI/PRC、AZW3/KF8 和 TXT；TXT 自动识别 UTF-8、BOM Unicode、GB18030，识别章节/分卷后转为 EPUB。Calibre 仅在部分转换和 KFX 工作流中可选依赖。
- Kreader 的 EPUB/MOBI/AZW3 使用 `NativeReaderHost` + `Kkindle.Layout` 自绘，PDF 使用 `NativePdfReaderHost` + PDFium 自绘。
- 阅读器支持分页、滚动、单页/双页、竖排、目录、搜索、书签、批注、阅读进度、AI 助手、词典/Wikipedia、翻译、拼音、听书和阅读统计。PDF 支持连续/单页/双页、缩放、旋转、文本选择、搜索、书签及任意页面位置批注，不提供 OCR。
- 阅读进度使用 `ReaderContentPosition` 保存正文偏移、图片序号、备用页索引和页内位置；不得只依赖 `ScrollPosition` 数值跨模式恢复。
- S3/WebDAV 共用版本 5 同步协议，支持增量快照、独立 gzip 基线、未知字段保留、同步详情和按需下载；兼容性规则见 `docs/s3-snapshot-compatibility.md` 与 `docs/s3-sync-extension.md`。
- 耗时操作统一进入后台任务中心；传输、导入、转换、同步等进度和完成记录应继续沿用该入口，避免重新把临时进度面板分散到主窗口。
- Windows 支持 WPD/MTP Kindle；Linux/macOS 只支持以文件系统挂载方式出现的 Kindle。

## 关键约束

- EPUB 落盘必须继续净化 `script`、`iframe`、`on*`、`javascript:` 和外部资源；不要启用书籍脚本或扩大导航白名单。
- Kreader 切章和导航沿用取消令牌、会话序号和 gate；禁止从宿主回调直接并发切章。加载中、已取消和旧请求不得回写当前阅读位置。
- 正文偏移必须遵循 `XhtmlChapterLoader` 的 `textContent` 约定，否则旧批注、搜索、书签和进度会错位。
- 竖排分页是固定字格布局；`VerticalWriting` 与双页、滚动模式的组合规则不能随意放宽。数字、拉丁字符、禁则和标点规则集中在 `src/Kkindle.Layout`。
- SQLite 新表和索引使用 `IF NOT EXISTS`；旧表加列前检查 `PRAGMA table_info`。新增同步实体必须在 `S3SyncService.EntityRegistry.cs` 注册，并补齐增量、合并、删除、清理和对象引用测试。
- 同步协议新增可选字段时保持版本 5，并保留未知字段及原始时间戳；字段改名、删除、类型变化或语义变化必须提高最低读取协议。存在未知数据时不得自动淘汰远端快照或清理对象。
- Windows WebView2 指针由 Avalonia 管理，不得手动释放或反射操作 COM 类型。
- AXAML 初始化期间可能触发 UI 事件，保留空值和就绪状态守卫。
- UI 保持现有黑白灰、直角细线和低干扰视觉风格。新增可操作控件应补充 `AutomationProperties.Name`。
- 内置字体兼容层依赖 Avalonia 12.1.1 私有字体 API。升级 Avalonia 或更换内置字体时必须重跑字体生命周期、样式组合和完整 UI 回归。

## 构建与验证

在仓库根目录使用仓库内 SDK：

```powershell
.\.dotnet\dotnet.exe restore Kkindle.sln -p:Platform=x64
.\.dotnet\dotnet.exe build Kkindle.sln --no-restore -p:Platform=x64
.\.dotnet\dotnet.exe test Kkindle.sln --no-build -p:Platform=x64
```

完整慢测试需要显式开启：

```powershell
.\.dotnet\dotnet.exe test Kkindle.sln -p:Platform=x64 -p:RunSlowTests=true
```

2026-10-22 交接检查结果：

- `dotnet build Kkindle.sln --no-restore -p:Platform=x64` 当前在依赖审计阶段失败，报告中危 `NU1902` 和高危 `NU1903`，均为 `System.Security.Cryptography.Pkcs 10.0.0`；编译器错误为 0。下一轮应先确认并修复该传递依赖，再执行完整测试和交付。
- 最近一份有记录的快速回归是 2026-10-17：`Kkindle.Tests` 899、`Kkindle.App.Tests` 13、`Kkindle.Platform.Common.Tests` 12、`Kkindle.Tests.Windows` 43。该结果早于当前 `HEAD`，不能作为当前提交的绿色结论。
- 完整 UI 回归会明显更慢，且会启动大量 Avalonia 测试宿主。优先按项目拆分运行并保留 `.trx`，不要用超时命令掩盖挂起。

## Windows Debug 交付

用户约定：每次修改代码后必须重新生成自包含 Windows x64 Debug 版并启动冒烟验证。

```powershell
.\.dotnet\dotnet.exe publish src\Kkindle.Desktop.Windows\Kkindle.Desktop.Windows.csproj -c Debug -p:Platform=x64 -r win-x64 --self-contained true -p:PublishSingleFile=false
```

发布源目录：

```text
src\Kkindle.Desktop.Windows\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\publish
```

同步到交付目录时只覆盖、不删除运行时数据：

```powershell
robocopy src\Kkindle.Desktop.Windows\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\publish artifacts\Kkindle-debug-win-x64-latest /E /XF kkindle-crash.log /XD data backups
```

`robocopy` 退出码小于等于 7 视为成功。交付前确认目录中存在 `Kkindle.exe`、`hostfxr.dll`、`hostpolicy.dll` 和 `coreclr.dll`，再从以下入口启动：

```text
artifacts\Kkindle-debug-win-x64-latest\Kkindle.exe
```

除非用户明确要求，不生成 Release EXE、安装包或便携包。

## 已知限制与后续优先级

1. 修复 `System.Security.Cryptography.Pkcs 10.0.0` 的 NuGet 审计失败，并重新确认 Windows、Linux、macOS 构建。
2. 在当前 `HEAD` 上重跑四个测试项目，分别记录快速和 `RunSlowTests=true` 的结果，更新本文测试基线。
3. 完成 Windows Debug 自包含发布和启动冒烟；若涉及阅读器，补做真实 EPUB/PDF 打开、重排、进度恢复和主题切换。
4. 对真实 S3/WebDAV、Kindle/Kobo USB/WPD、在线 AI、TTS 和邮件流程做集成验证；自动测试主要使用本地替身。
5. 仍缺少高对比度主题、完整屏幕阅读器审计、Linux/macOS 实机回归、PDF OCR、超长阅读内存压力和突然断电恢复验证。
6. 同步协议扩展后要同时验证旧客户端拒绝、未知字段往返、对象清理保护和多设备收敛，不能只测当前客户端上传下载。

## 接手建议

1. 先读本文件、`agent.md`、`Directory.Build.props`、`global.json` 和最近 10 个提交。
2. 运行 `git status --short`，区分用户改动与本地环境文件。
3. 从 `docs/` 中与任务对应的设计记录开始；性能、阅读位置和同步改动不要只看单个提交。
4. 修改后先跑最接近目标的测试项目，再跑快速全量；涉及 UI、阅读器、字体或同步协议时补跑慢测试。
5. 交付时说明验证命令、实际结果、未覆盖范围和 Debug EXE 路径，不要用历史测试数量代替当前验证。

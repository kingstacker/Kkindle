# Kkindle Agent Handoff（精简版）

> 更新时间：2026-08-30
>
> 工作目录：`/home/stacker/work_pro/Kkindle`

## 当前基线

- Kkindle 是 C# / .NET 10 / Avalonia 12.1.1 跨平台桌面应用，支持 Windows、Linux、macOS；应用版本由 `Directory.Build.props` 统一维护，目前为 `0.7.0`。
- 当前分支为 `master`，已同步 `origin/master`，同步基线为 `5e50c51`。工作区可能存在未提交的用户修改，先看 `git status`，不要回滚无关改动。
- `.claude/` 是本机配置目录，保留其内容，不要纳入功能修改。
- Linux Debug 入口：`src/Kkindle.Desktop.Linux/bin/Debug/net10.0/Kkindle`。
- SDK 由 `global.json` 固定为 .NET `10.0.400`；不要绕过仓库锁定的 SDK。

## 代码地图

- `src/Kkindle.App`：Avalonia UI、Kreader 交互和宿主。
- `src/Kkindle.Layout`：无 UI 依赖的 HarfBuzz + Skia 排版、断行、分页和命中测试。
- `src/Kkindle.Core`：模型、接口、布局与业务策略。
- `src/Kkindle.Infrastructure`：SQLite、EPUB 准备、格式转换、字典、备份和网络服务。
- `src/Kkindle.Platform.*`：设备、密钥环和平台文件系统实现。
- `src/Kkindle.Desktop.*`：三端启动项目。
- `tests/Kkindle.Tests`：可移植单元测试。

## Kreader 当前实现

- EPUB（含临时转换得到的 MOBI/AZW3）使用 `NativeReaderHost` + `Kkindle.Layout` 自绘；PDF 使用平台 WebView 内置查看器。
- 竖排是固定字格的单页分页布局：`VerticalWriting=true` 时强制 `FlowMode=1`、关闭双页；数字、拉丁字符、禁则和标点规则在 `src/Kkindle.Layout` 中实现。
- 书籍排版参数（字号、行高、正文宽度、页边距、字体等）按 `BookFile` 保存；竖排和段首缩进是全局偏好。打开书籍时使用 `ReaderLayoutDefaults.ApplyGlobalPreferences` 合并，不能让旧的书籍记录覆盖全局方向。
- 进度按 `BookFile` 保存。竖排分页的 `ScrollPosition` 是页首正文字符偏移；横排分页是像素偏移；滚动模式是内容像素偏移。原生引擎切换布局时必须保留语义锚点，不能把像素值直接当字符偏移。
- 返回书架前直接从当前宿主读取位置并保存；退出和重新打开会使旧的延迟进度写入失效，关闭过程中忽略宿主桥事件。
- `UseLinuxPlainTextRecoveryFallback=false`；Linux 文本回退代码仅供诊断/兼容，不是生产 EPUB 渲染路径。

## 关键约束

- EPUB 落盘必须继续净化 `script`、`iframe`、`on*`、`javascript:` 和外部资源；不要重新启用书籍脚本，也不要扩大导航白名单。
- Kreader 导航沿用取消令牌、序列号和 gate；不要从宿主回调直接并发切章。
- 偏移必须保持 `XhtmlChapterLoader` 的正文 `textContent` 约定，否则旧批注、搜索和书签会错位。
- SQLite 新表使用 `CREATE TABLE IF NOT EXISTS`；旧表加列先检查 `PRAGMA table_info`，禁止直接改用户数据库结构。
- Windows Avalonia 管理的 WebView2 指针不能手动释放或反射操作 COM 类型。
- UI 事件可能在 AXAML 初始化期间触发，保留控件空值和就绪状态守卫。
- 保持黑白灰、直角细线的现有界面风格；不要提交 `bin/`、`obj/`、`artifacts/`、本地数据、缓存、日志或密钥。

## 构建与验证

在仓库根目录执行：

```bash
dotnet test tests/Kkindle.Tests/Kkindle.Tests.csproj -c Debug --no-restore
dotnet build src/Kkindle.Desktop.Linux/Kkindle.Desktop.Linux.csproj -c Debug --no-restore
DISPLAY=:0 ./src/Kkindle.Desktop.Linux/bin/Debug/net10.0/Kkindle
```

截至本文更新时间：可移植测试 `332` 项通过；Linux Debug 构建 `0` 错误，有一个已有的 `Bitmap.Save` 过时 API 警告。用户约定：每次修改代码后都要重新构建并打开 Linux Debug 版。

## 已知限制与后续优先级

- `MainWindow.KreaderValidation.cs` 中两个旧 WebKit 专用验证入口仍返回 `SKIPPED`，应改写为 `NativeReaderHost` / 页面快照验证。
- 纯图片页没有独立的持久化页索引，当前进度主要依赖字符偏移；若需要保证封面或无文字页精确恢复，应为 `ReaderProgress` 增加页索引及迁移。
- PDF 的选择、缩放和点击区域能力受平台内置查看器限制。
- Linux/macOS 仅支持文件系统挂载型 Kindle；Windows 才支持 WPD/MTP。
- 升级 SkiaSharp/HarfBuzzSharp 必须同步重跑排版确定性测试，并单独核对页面快照。

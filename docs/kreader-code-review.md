# Kreader v1.0 代码审查

审查日期：2026-09-15。基线提交：`c7913e1`，分支：`6-UI-optimization-and-bug-resolution`。

用户指定的两项进度恢复问题已修复，并补齐书签、SQLite 迁移和 S3 快照兼容。本次新增 33 项测试（20 项 UI、13 项核心与存储），上一轮 5 类修复及其 18 项回归继续保留。修改留在工作区，未创建提交，原有界面修改保留。

## 本次补齐的进度修复

### P1 已修复：改变全局阅读模式后，重开书籍进度错位

修复前，`ReaderProgressRow` 只保存一个 `ScrollPosition` 数值。横排分页将其解释为横向像素，滚动模式解释为纵向像素，竖排解释为正文字符偏移。恢复时直接把旧数值交给当前全局排版，关闭后也没有独立的正文位置可用于调整字号或视口后的恢复。

修复前使用实际原生宿主测得以下结果（页码从 1 开始，保留为原始复现证据）：

| 场景 | 原正文字符偏移 | 保存的数值 | 对应正文应在 | 实际恢复到 |
| --- | ---: | ---: | ---: | ---: |
| 横排分页保存，改为竖排后重开 | 9,134 | 25,920 | 第 37 页 | 第 72 页 |
| 滚动阅读保存，改为分页后重开 | 9,134 | 19,440 | 第 37 页 | 第 28 页 |

现在新增版本为 1 的 `ReaderContentPosition`，保存正文字符偏移、图片序号、备用页索引和滚动视口内位置。`NativeReaderHost` 在关闭或保存时捕获内容位置，重开后根据当前全局排版重新定位。保存的章节片段不会覆盖精确位置；尚未完成恢复或正在加载的宿主不会把旧进度改写为章首。正文偏移沿用原 XHTML 文本坐标，没有插入占位字符。

相关位置：[内容位置模型](C:/Users/kings/Desktop/01_Projects/Kkindle/src/Kkindle.Core/ReaderContentPosition.cs:9)、[进度保存](C:/Users/kings/Desktop/01_Projects/Kkindle/src/Kkindle.App/MainWindow.Reader.cs:1032)、[恢复调用](C:/Users/kings/Desktop/01_Projects/Kkindle/src/Kkindle.App/MainWindow.ReaderInteraction.cs:1554)、[原生捕获与恢复](C:/Users/kings/Desktop/01_Projects/Kkindle/src/Kkindle.App/NativeReaderHost.cs:702)。书签跳转、重复书签判定和角标也使用内容位置，支持切换排版后定位到相同文字或图片。

`ReaderProgress`、`ReaderBookmarks` 增加可空的 `ContentPositionJson`，迁移先检查 `PRAGMA table_info`，可重复初始化而不覆盖旧记录。S3 快照增加可选字段，导入和导出均携带新位置；来自旧客户端的新记录会清除过时的内容位置，避免把较新的数值进度与较旧的锚点混用。缺失、损坏或未知版本的位置数据会回退到旧字段，不阻止整条记录读取。

旧数据兼容保留可用的数值位置；可识别的模式变化使用章节百分比近似恢复，旧书签优先尝试唯一正文摘录。旧记录没有保存的排版信息无法精确反推，首次升级恢复可能需要手动校准一次。新位置保存后，恢复以文字或图片为准；重排后页码本身可能改变。

### P2 已修复：纯图片章节的竖排位置无法保存

修复前，竖排进度取当前页的 `TextStartOffset`，没有文字的图片页全部回退为 0。包含 4 张整页图片的章节在第 3 页保存后会回到第 1 页。现在图片页保存章节内图片序号，关闭重开后恢复第 3 张图片；竖排纯图片页的阅读百分比也随翻页更新。

图片序号按内容顺序计数，同一图片文件出现多次仍可区分，不保存缓存绝对路径。回归覆盖四种排版、缓存目录变化，以及图片前的文字重排导致页数变化的情况。图片内容锚点参与会话内重排和持久化恢复，页索引仅作最后回退。旧版已经保存为 0 且没有其他位置线索的图片进度，无法追溯还原丢失的页码。

修复前的原始测量数据保留于 [persisted-position-findings.json](C:/Users/kings/Desktop/01_Projects/Kkindle/artifacts/kreader-review/position-probe/output/persisted-position-findings.json)。该文件记录旧行为，不代表当前实现。修复后的验证使用真实 `NativeReaderHost`、SQLite 读写和 MainWindow 的打开、关闭、重开与书签跳转流程。

## 上一轮修复继续保留

| 级别 | 原有问题与触发条件 | 修复方式 |
| --- | --- | --- |
| P2 | PDF 文档加载成功后调用停止，或取消其原会话，再导航同一文件会复用已取消的文档令牌，重试返回失败。准备尚未完成时也可能被误认为可复用。 | 仅复用完整就绪且未取消的文档；准备完成后才发布就绪状态，停止时清除状态。 |
| P2 | EPUB 在章中切换滚动/分页、调整滚动模式字号或切换首行缩进，会回到章首或显示错误段落。缩进重载写入的恢复位置又被配置过程清空。 | 修改排版前捕获正文锚点；缩进重载完成后恢复；等待正在进行的加载，并用配置序号阻止旧设置覆盖新设置。 |
| P2 | 初次恢复章节锚点时，滚动模式只改变页索引，双页模式可能从奇数索引开始；未消费的备用位置在下一次重排时又把阅读器拉回章首。 | 按滚动/单页/双页正确应用目标，一次性消费同一请求的备用位置；缩放时保存文本锚点，加载中视口变化在完成前重新排版。 |
| P2 | 原生滚动模式捕获书签位置时强制写入 `FlowMode=1`，与滚动刷新报告的 `FlowMode=0` 不一致，书签角标显示不稳定。 | 从宿主实际分页状态取得阅读模式。 |
| P2 | AI/TTS 设置初始化内部吞掉取消异常后，外层阅读器初始化仍继续启动会话计时器；已取消的打开请求还可能进入失败清理路径。 | 在初始化入口、各异步阶段之后验证会话取消；打开请求取消后不再对当前阅读器执行错误清理。 |

主要代码：[NativeReaderHost.cs](C:/Users/kings/Desktop/01_Projects/Kkindle/src/Kkindle.App/NativeReaderHost.cs:304)、[NativePdfReaderHost.cs](C:/Users/kings/Desktop/01_Projects/Kkindle/src/Kkindle.App/NativePdfReaderHost.cs:126)、[初始化链路](C:/Users/kings/Desktop/01_Projects/Kkindle/src/Kkindle.App/MainWindow.ReaderInteraction.cs:1387)、[书签位置](C:/Users/kings/Desktop/01_Projects/Kkindle/src/Kkindle.App/MainWindow.ReaderFeatures.cs:843)。

## 验证范围与结果

环境为 Windows x64、.NET SDK 10.0.400、Avalonia 12.1.1、SkiaSharp 3.119.4。UI 测试采用 Avalonia Headless 和真实 Skia 渲染。EPUB 使用 `NativeReaderHost`，PDF 使用 `NativePdfReaderHost`；旧 WebView 诊断中的 `SKIPPED` 结果未计为验证通过。

阅读器测试位于 [ReaderStabilityTests.cs](C:/Users/kings/Desktop/01_Projects/Kkindle/tests/Kkindle.App.Tests/ReaderStabilityTests.cs)，共 38 项，其中本次新增 20 项。存储兼容测试位于 [ReaderContentPositionTests.cs](C:/Users/kings/Desktop/01_Projects/Kkindle/tests/Kkindle.Tests/ReaderContentPositionTests.cs)，同步往返测试位于 [S3SyncTests.cs](C:/Users/kings/Desktop/01_Projects/Kkindle/tests/Kkindle.Tests/S3SyncTests.cs:14)。覆盖滚动、单页、双页、竖排、字体和视口变化、重复图片、旧数据库、异常元数据以及新旧快照。

| 验证 | 结果 | 记录 |
| --- | --- | --- |
| 阅读器与目录跟随回归 | 45 通过，0 跳过 | `artifacts/kreader-review/reader-position-regressions.trx` |
| 位置存储、迁移与 S3 相关测试 | 42 通过，0 跳过 | `artifacts/kreader-review/reader-position-storage.trx` |
| 核心全量测试 | 792 通过，0 跳过 | `artifacts/kreader-review/position-fix-full-core.trx` |
| 最终 UI 全量测试 | 206 通过，0 跳过，耗时约 7 分 51 秒 | `artifacts/kreader-review/position-fix-full-app.trx` |

同时保留了切章取消和导航 gate、目录/搜索定位、批注正文偏移、阅读时间累计、PDF 后台索引与资源释放的既有处理。数据库只增加可空兼容字段；XHTML 正文偏移约定、全局排版优先级和 PDF 的页码与视图状态存储不变。未进行 Linux/macOS 实机验证、超长时间内存压力测试或真实在线 TTS/AI/S3 服务调用。

上一轮为字体原生崩溃采用的 Avalonia 字体集合修复继续保留，构建仍会报告已有的 `AVA3001` 私有 API 提示；这不是本轮新引入的警告。

## 调试交付

已按项目约定完成 Windows x64 Debug 自包含发布与同步，包含 .NET 与 WindowsDesktop 10.0.11。`hostfxr.dll`、`hostpolicy.dll`、`coreclr.dll` 均存在；EXE、主程序集、运行时和配置等 10 个关键文件的 SHA-256 与发布源一致。

同步使用 `/E`，排除 `data/`、`backups/` 和 `kkindle-crash.log`，没有删除目标目录内容；robocopy 退出码为 3。同步前后 9,560 个受保护文件的路径、大小和修改时间一致。最终 `git diff --check` 通过。

EXE：[Kkindle.exe](C:/Users/kings/Desktop/01_Projects/Kkindle/artifacts/Kkindle-debug-win-x64-latest/Kkindle.exe)。本次发布、同步日志和核验数据使用 `artifacts/kreader-review/position-fix-publish-debug.log`、`position-fix-sync-debug.log`、`position-fix-sync-verification.json`。

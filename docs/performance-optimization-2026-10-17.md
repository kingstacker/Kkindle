# 书库交互、同步与阅读器热路径性能优化（2026-10-17）

本轮在 `dev` 分支完成，目标是消除上一轮之后仍然存在的结构性热点。改动均保持现有行为与数据格式。

## 书库与 UI

- 搜索输入增加 250ms 防抖；`SearchPageAsync`、书库计数和筛选项查询整体移到线程池执行，避免 SQLite 的同步执行阻塞 UI 线程。
- 书库页刷新改为一次 `Reset` 批量替换，不再逐项 `Remove/Insert/Move`；`VirtualizingWrapPanel` 每次刷新只清理一次已实现容器。
- 收藏夹与设备书单也改为批量替换集合，不再逐项增删。
- 书籍卡片的派生文本（格式、大小、分类、出版信息、简介摘要等）在 `Refresh` 时计算一次并缓存，变更时才发通知；未变化的刷新不再对每本书做封面 `FileInfo`。
- 封面缩略图并发从 2 提到 4。
- `LayoutUpdated` 中的排序控件视觉树查找改为缓存，模板重建时自动重新解析。
- 框选改为只遍历虚拟化面板已实现的容器，去掉每卡一次 `ContainerFromItem` 的 O(n²) 查找。
- 设备轮询在设备页隐藏且无设备连接时降为每 15 秒一次；进入设备页仍立即刷新。
- 启动时先显示书库首屏，再执行 S3 设置、Calibre 探测、资源清单、自动备份和设备检测等 Stage 3 初始化。

## 数据层

- 三个 SQLite 服务改用私有缓存（移除 `Cache=Shared`），统一设置
  `synchronous=NORMAL`、`cache_size=-65536`、`temp_store=MEMORY`、`busy_timeout=5000`。
- 增加 `Books(UpdatedAt/CreatedAt)`、`Books(ReadingStatus, UpdatedAt)`、`Books(IsFavorite, UpdatedAt)`、
  `BookFiles(BookId, Format)`、`ReaderAnnotations(UpdatedAt)` 以及大小写不敏感的标题/作者索引；
  依赖新增列的索引放在列迁移之后创建，兼容旧数据库。
- 导入时默认收藏夹 ID 在事务外查询一次并复用；同名书匹配改用 `COLLATE NOCASE` 走索引。

## S3 / WebDAV 同步

- 书籍文件、封面和设备快照下载改为与上传相同的有界并发（`ConcurrentRequests`）；
  路径规划、去重与封面冲突决策仍保持串行顺序。
- 阅读统计的逐书 N+1 查询改为整表批量读取：每台设备一次读取全部计数与按日计数，
  只在本机累计值增长时写入。
- 快照“未变化行”比较改用 UTF-8 字节序列，避免为每行生成两份 UTF-16 JSON 字符串。
- 正常情况下每次同步只写一次状态文件；清理阶段只有在真正压缩快照时才额外保存。
- “测试连接”只列举一次并对单个快照做解密校验，不再下载全部设备的完整快照。
- 上传前已计算的 SHA-256 透传给对象存储，multipart 不再重读整个大文件。
- 同步状态文件替换增加短暂重试，读取时允许删除共享，避免 Windows 上杀毒/索引器造成的偶发共享冲突。

## 阅读器

- 每个 `NativeReaderHost` 复用一个 `TypesetFontLibrary`；重排、切章和页数估算不再重复加载全部候选 CJK 字体。
- 页数估算增加按“章节文件签名 + 排版参数 + 视口”的内存缓存，并直接复用当前章节已完成的排版结果。
- 页内搜索在章节加载后只规范化一次正文与偏移表，后续按键只处理查询词。
- 字符矩形查找改为在有序 run 上二分，选区/搜索 overlay 从 O(字符数 × run 数) 降为 O(字符数 × log run 数)。
- 绘制阶段按 `PlacedRun` 引用缓存 `SKTextBlob`，滚动、选区和主题重绘不再为每个 run 重建字体与 blob。
- 垂直排版的 `Tokenize` 改为游标推进，消除长段落上的 O(n²) 文本元素查找。
- 垂直 debug box 改为仅在调试开关开启时收集。
- 图片按最长边 2048px 采样解码，缓存改为 LRU，并在翻页前后台预取当前页与相邻页的图片。
- 滚动位置上报节流到约 30fps，并在停止后补发最终位置，避免每个滚轮事件都做 JSON 序列化与 TOC 扫描。
- 翻译进度窗口把逐片段 UI 更新合并为约 100ms 一批，每批只排序/统计一次。

## 发布

- Windows Release 发布启用 `PublishReadyToRun`（Debug 发布保持关闭以保留调试能力）。

## 验证

- `Kkindle.Tests`：897 项通过。
- `Kkindle.App.Tests`：13 项通过。
- `Kkindle.Platform.Common.Tests`：12 项通过。
- `Kkindle.Tests.Windows`：43 项通过。
- 已生成并启动 Debug 自包含 x64 版本做冒烟验证。

## 尚未完成

- 一次同步仍会做三次完整数据库快照，状态文件仍保存全量快照。
- WPD 首次扫描仍会整本复制并哈希，且每本重建 Shell 会话；没有物理设备可验证，本轮未改动。
- 选区/搜索 overlay 的几何仍按字符逐个生成，命中测试仍线性扫描页内 glyph。
- 页数估算首次打开仍会排版全书；收藏夹视图仍是非虚拟化 `ItemsControl`。
- 翻译请求仍按批串行发送；PDF 文本提取仍逐字符调用多次 native API。

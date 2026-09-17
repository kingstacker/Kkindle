# Kkindle

[![Release](https://github.com/kingstacker/Kkindle/actions/workflows/release.yml/badge.svg)](https://github.com/kingstacker/Kkindle/actions/workflows/release.yml) [![最新版本](https://img.shields.io/github/v/release/kingstacker/Kkindle)](https://github.com/kingstacker/Kkindle/releases/latest)

**简体中文** · [English](README.md)

Kkindle 是一款基于 Avalonia 的跨平台电子书与 Kindle 管理器。它将本地书库、阅读、批注、AI 助手、格式转换和 Kindle 传输集中在一个简洁的桌面应用中。

![Kkindle 书库主界面](docs/images/主界面.png)

## 主要功能

- **书库管理**：导入 EPUB、PDF、MOBI、AZW3，管理元数据、封面、标签、分类、搜索和阅读状态；同名版本导入时可选择添加格式、独立保存或跳过，删除内容会先进入可恢复回收站。
- **Kreader 阅读器**：支持分页/滚动阅读、目录、书签、查找、批注、排版设置、阅读进度和 Windows 听书。
- **PDF 阅读**：书库自动使用第一页作为封面，支持连续滚动、单页和双页阅读，以及适合宽度/整页、清晰缩放和 90° 旋转；文字型论文支持同页双栏聚焦阅读和结构识别，可从论文面板跳转章节、图表、表格与参考文献。支持选择文字、六种划线/标记样式、批注编辑，以及通过“页注”按钮或右键在 PDF 任意位置添加研究批注。打开时先显示当前页，全文搜索和论文结构在后台准备；可见区域按屏幕像素密度重绘。搜索、书签和阅读进度沿用 EPUB 的本地保存逻辑。扫描版可显示原页并添加页面笔记，暂不提供 OCR。支持框选 PDF 区域并发送给兼容视觉输入的 AI，用于解释图表、公式和表格。
- **AI 阅读助手**：围绕当前书籍提问、章节总结、选文解释和全书概览；支持自动获取模型、模型选择、连通性检测以及 DeepSeek、OpenAI 兼容接口。只发送相关本地片段。
- **Kindle 管理**：识别 USB/WPD/MTP 设备，传输书籍，管理字体和词典，导入 `My Clippings.txt`。
- **工具与同步**：Calibre 格式转换、本地备份、凭据加密和可选 S3 / WebDAV 同步。

## 下载

从 [GitHub Releases](https://github.com/kingstacker/Kkindle/releases) 下载 Windows、Linux 或 macOS 安装包。

格式转换需要另行安装 Calibre。macOS 包已提供，但仍在进行目标设备验证。

设置中的“诊断”页面会检查数据目录、PDF 解析器、WebView、Calibre、TTS 和 Kindle 服务；跨平台问题可先从这里确认。书库设置中的“回收站”支持恢复误删的整本书或单个格式，也可在确认后永久清理。回收站内容会随本地备份一起保存。

## 云端同步

在“设置 → 数据与同步 → 云端同步”中选择 **S3** 或 **WebDAV**，填写连接信息后点击“保存设置”。两种方式的地址和凭据分别保留；立即同步、启动/退出时同步和定时同步均使用当前保存的方式。

WebDAV 服务地址应指向已存在的目录，例如 `https://dav.example.com/dav/`。需要认证时填写用户名和密码（部分服务使用应用密码）。程序会自动创建同步子目录，默认名为 `kkindle`，可在高级选项中修改；测试连接会验证读写能力并清理测试文件。支持标准 WebDAV 的 PROPFIND、MKCOL、GET、PUT、MOVE、DELETE 操作；文件上传完毕后通过 MOVE 发布，避免中断上传覆盖已有快照。

同步子目录、加密密钥、同步间隔和传输选项由两种方式共用。跨设备同步同一目录时应使用相同的加密密钥；更换密钥请使用新的同步子目录。独立连接参数文件只导出所选方式的凭据，兼容原来的 `.kkindle-s3.json`，WebDAV 使用 `.kkindle-webdav.json`。本地备份不包含连接密码或加密密钥。

## 从源码运行

需要 [.NET SDK 10.0.400](https://dotnet.microsoft.com/download/dotnet/10.0)。

```powershell
dotnet restore Kkindle.sln
dotnet build Kkindle.sln -p:Platform=x64
dotnet test Kkindle.sln --no-build -p:Platform=x64
# 发布前需要完整回归时再显式加入慢测试
dotnet test Kkindle.sln --no-build -p:Platform=x64 -p:RunSlowTests=true
dotnet run --project src\Kkindle.Desktop.Windows\Kkindle.Desktop.Windows.csproj -p:Platform=x64
```

默认测试会跳过耗时的 UI/渲染/GC 压力用例，只保留核心、平台、设备和少量 UI 冒烟回归；这些用例仍保留在仓库中，可用 `RunSlowTests=true` 手动开启。

Linux 还需要 WebKitGTK 和 Secret Service。Linux/macOS 的运行命令见[跨平台说明](docs/cross-platform.md)。

## 技术参考

- **功能参考与工具**：[Calibre](https://github.com/kovidgoyal/calibre)、[KFX Input](https://www.mobileread.com/forums/showthread.php?t=291290)。
- **运行依赖**：[Avalonia](https://github.com/AvaloniaUI/Avalonia)、[CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)、[EF Core](https://github.com/dotnet/efcore)、[PdfPig](https://github.com/UglyToad/PdfPig)。
- **测试工具**：[xUnit](https://github.com/xunit/xunit)、[VSTest](https://github.com/microsoft/vstest)。

## 致谢

- 社区：[LINUX DO](https://linux.do/?tl=en)
- 公益站：any

## 许可证

本项目采用 [MIT License](LICENSE) 开源；第三方组件和随应用分发的字体遵循各自许可证。

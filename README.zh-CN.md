# Kkindle

[![Release](https://github.com/kingstacker/Kkindle/actions/workflows/release.yml/badge.svg)](https://github.com/kingstacker/Kkindle/actions/workflows/release.yml) [![最新版本](https://img.shields.io/github/v/release/kingstacker/Kkindle)](https://github.com/kingstacker/Kkindle/releases/latest)

**简体中文** · [English](README.md)

Kkindle 是一款基于 Avalonia 的跨平台电子书与 Kindle 管理器。它将本地书库、阅读、批注、AI 助手、格式转换和 Kindle 传输集中在一个简洁的桌面应用中。

![Kkindle 书库主界面](docs/images/主界面.png)

## 主要功能

- **书库管理**：导入 EPUB、PDF、MOBI、AZW3，管理元数据、封面、标签、分类、搜索和阅读状态；同名版本导入时可选择添加格式、独立保存或跳过，删除内容会先进入可恢复回收站。
- **Kreader 阅读器**：支持分页/滚动阅读、目录、书签、查找、批注、排版设置、阅读进度和 Windows 听书。PDF 可保存页面笔记；有文本层的页面支持搜索、AI 和朗读，扫描图片页会明确提示当前不提供 OCR。
- **AI 阅读助手**：围绕当前书籍提问、章节总结、选文解释和全书概览；支持自动获取模型、模型选择、连通性检测以及 DeepSeek、OpenAI 兼容接口。只发送相关本地片段。
- **Kindle 管理**：识别 USB/WPD/MTP 设备，传输书籍，管理字体和词典，导入 `My Clippings.txt`。
- **工具与同步**：Calibre 格式转换、Z-Library 下载、本地备份、凭据加密和可选 S3 同步。

## 下载

从 [GitHub Releases](https://github.com/kingstacker/Kkindle/releases) 下载 Windows、Linux 或 macOS 安装包。

格式转换需要另行安装 Calibre。macOS 包已提供，但仍在进行目标设备验证。

设置中的“诊断”页面会检查数据目录、PDF 解析器、WebView、Calibre、TTS 和 Kindle 服务；跨平台问题可先从这里确认。书库设置中的“回收站”支持恢复误删的整本书或单个格式，也可在确认后永久清理。回收站内容会随本地备份一起保存。

## 从源码运行

需要 [.NET SDK 10.0.400](https://dotnet.microsoft.com/download/dotnet/10.0)。

```powershell
dotnet restore Kkindle.sln
dotnet build Kkindle.sln -p:Platform=x64
dotnet test Kkindle.sln --no-build -p:Platform=x64
dotnet run --project src\Kkindle.Desktop.Windows\Kkindle.Desktop.Windows.csproj -p:Platform=x64
```

Linux 还需要 WebKitGTK 和 Secret Service。Linux/macOS 的运行命令见[跨平台说明](docs/cross-platform.md)。

## 技术参考

- **功能参考与工具**：[zlibrary.koplugin](https://github.com/ZlibraryKO/zlibrary.koplugin)、[Calibre](https://github.com/kovidgoyal/calibre)、[KFX Input](https://www.mobileread.com/forums/showthread.php?t=291290)。
- **运行依赖**：[Avalonia](https://github.com/AvaloniaUI/Avalonia)、[CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)、[EF Core](https://github.com/dotnet/efcore)、[PdfPig](https://github.com/UglyToad/PdfPig)。
- **测试工具**：[xUnit](https://github.com/xunit/xunit)、[VSTest](https://github.com/microsoft/vstest)。

## 致谢

- 社区：[LINUX DO](https://linux.do/?tl=en)
- 公益站：any

## 许可证

本项目采用 [MIT License](LICENSE) 开源；第三方组件和随应用分发的字体遵循各自许可证。

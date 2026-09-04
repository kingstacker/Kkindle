# Kkindle

[![Release](https://github.com/kingstacker/Kkindle/actions/workflows/release.yml/badge.svg)](https://github.com/kingstacker/Kkindle/actions/workflows/release.yml) [![最新版本](https://img.shields.io/github/v/release/kingstacker/Kkindle)](https://github.com/kingstacker/Kkindle/releases/latest)

**简体中文** · [English](README.md)

Kkindle 是一款基于 Avalonia 的跨平台电子书与 Kindle 管理器。它将本地书库、阅读、批注、AI 助手、格式转换和 Kindle 传输集中在一个简洁的桌面应用中。

![Kkindle 书库主界面](docs/images/主界面.png)

## 主要功能

- **书库管理**：导入 EPUB、PDF、MOBI、AZW3，管理元数据、封面、标签、分类、搜索和阅读状态。
- **Kreader 阅读器**：支持分页/滚动阅读、目录、书签、查找、批注、排版设置、阅读进度和 Windows 听书。
- **AI 阅读助手**：围绕当前书籍提问、章节总结、选文解释和全书概览；支持自动获取模型、模型选择、连通性检测以及 DeepSeek、OpenAI 兼容接口。只发送相关本地片段。
- **Kindle 管理**：识别 USB/WPD/MTP 设备，传输书籍，管理字体和词典，导入 `My Clippings.txt`。
- **工具与同步**：Calibre 格式转换、Z-Library 下载、本地备份、凭据加密和可选 S3 同步。

## 下载

从 [GitHub Releases](https://github.com/kingstacker/Kkindle/releases) 下载 Windows、Linux 或 macOS 安装包。

格式转换需要另行安装 Calibre。macOS 包已提供，但仍在进行目标设备验证。

## 从源码运行

需要 [.NET SDK 10.0.400](https://dotnet.microsoft.com/download/dotnet/10.0)。

```powershell
dotnet restore Kkindle.sln
dotnet build Kkindle.sln -p:Platform=x64
dotnet test Kkindle.sln --no-build -p:Platform=x64
dotnet run --project src\Kkindle.Desktop.Windows\Kkindle.Desktop.Windows.csproj -p:Platform=x64
```

Linux 还需要 WebKitGTK 和 Secret Service。Linux/macOS 的运行命令见[跨平台说明](docs/cross-platform.md)。

## 许可证

本项目采用 [MIT License](LICENSE) 开源；第三方组件和随应用分发的字体遵循各自许可证。

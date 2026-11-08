# 第三方许可与发布材料维护

客户端根许可证为 GPL v3，第三方代码、字体及原生库保留各自的许可。
发布声明入口是根目录 `THIRD-PARTY-NOTICES.md`，原始文档、来源 URL 和
SHA-256 位于 `src/Kkindle.Infrastructure/ThirdParty/Notices/manifest.json`。
清单合并已恢复的不同平台依赖和缓存中的 SDK 运行时包，不代表某一个安装包
实际使用了其中全部依赖。

## 本次补齐的材料

- `ThirdParty/MdxParser/encrypt/MiniLZO.cs` 恢复原始 LZO 1996–2011 年版权、
  Frank Razenberg 的 C# 移植说明及 GPL-2.0-or-later 授权；算法没有改动。
  客户端选择 GPL v3 分发，完整文本见 `LICENSE.MiniLZO.txt`。该目录的 Apache
  2.0 文件只适用于相应解析器部分，不能覆盖 MiniLZO。MDX 的 Bouncy Castle
  摘要实现也补入历史原始许可及来源记录；原导入未记录准确版本，不虚构版本。
- 各 NuGet 包的 LICENSE、NOTICE、第三方声明，以及 .NET 运行时包的原始许可
  和声明随客户端发布。保留 ONNX Runtime、DotNetG2P、Skia/HarfBuzz 等包内
  的完整声明，而不是仅列许可证名称。
- Inter 六种样式和三个数学字体分别保留 OFL 或 GUST Font License / LPPL
  全文；版权从实际嵌入字体读取。清单记录字体版本及程序集哈希，更新字体或
  包装库后须重新核对。CSharpMath 编译进的 Typography 子模块也按实际提交
  保留原始许可证和源文件中的版权声明，摘录文件明确标明来源及摘录性质。
- 现有 PDFium 的 17 个许可与声明文件继续随程序发布。
- macOS 的 libmtp 1.1.21 / libusb 1.0.27 随应用附带实际编译使用的完整源码
  归档、原始 COPYING、准确构建脚本、架构元数据、校验值和重建/替换说明。
  详情见 [macOS MTP 对应源码说明](macos-mtp-corresponding-source.md)。

原有书体授权记录独立保留；这些新增材料不代替字体作者的自定义授权原件。

## 升级依赖后的操作

先恢复需要构建的项目，再运行：

```bash
python3 scripts/update-third-party-notices.py
```

脚本从已恢复的 NuGet 包提取原文，缺少原文时读取上游来源并保留版本/提交及
原文哈希。它不会在普通构建中自动下载文档。审核更新后的清单、版权、原始
LICENSE/NOTICE 和字体信息，再按项目约定进行敏感信息审查和 Git 提交。
新字体或新的 Typography 子模块版本需要明确更新脚本中的已核对版本记录。

材料已入库且依赖已恢复时，可以离线刷新：

```bash
python3 scripts/update-third-party-notices.py --offline
```

离线模式直接复用校验通过的原文和同一提交的版权摘录，不需要把
`artifacts/license-materials-cache/` 加入 Git。缺失必需原件时会失败，不会用
一个许可名称或其他组件的许可证代替。

## 构建和发布检查

Windows、macOS、Linux 和 MCP 项目的 `Publish` 会自动运行离线检查：

- 原始文档与清单哈希一致，必须保留 MDX、KindleUnpack、PDFium 等声明。
- 项目资产清单和发布目录的 `.deps.json` 中，包与运行时版本都已有许可材料。
- 发布副本与源码目录的原文逐字节一致。
- Release 目录不能出现仅用于开发集成的 DiagnosticsSupport DLL。

例如手动检查 macOS 发布目录：

```bash
dotnet run --file scripts/verify-third-party.cs -- \
  --repository . \
  --assets src/Kkindle.Desktop.MacOS/obj/project.assets.json \
  --publish /path/to/publish --configuration Debug
```

Android 和 iOS 使用显式 `AndroidAsset` / `BundleResource`，编译前自动检查
对应依赖和资源目标路径。仅设置桌面项目的 CopyToPublishDirectory 不足以
保证许可文本进入 APK 或 iOS 应用包。可以单独执行资源检查，无需构建应用：

```bash
dotnet msbuild src/Kkindle.Mobile.Android/Kkindle.Mobile.Android.csproj \
  -t:VerifyKkindleMobileLicenseResources -p:Configuration=Debug -p:RuntimeIdentifier=android-x64
dotnet msbuild src/Kkindle.Mobile.iOS/Kkindle.Mobile.iOS.csproj \
  -t:VerifyKkindleMobileLicenseResources -p:Configuration=Debug -p:RuntimeIdentifier=iossimulator-x64
```

检查最终 APK 时，使用生成的资源映射文件并指定归档：

```bash
dotnet run --file scripts/verify-third-party.cs -- \
  --repository . \
  --assets src/Kkindle.Mobile.Android/obj/project.assets.json \
  --resources src/Kkindle.Mobile.Android/obj/Debug/net10.0-android36.0/android-x64/third-party-resources.map \
  --archive /path/to/application.apk --archive-prefix assets/
```

同一归档检查也支持 IPA：将 `--archive-prefix` 改为实际的
`Payload/<应用目录>.app/`，并提供 iOS 的资产清单及资源映射。

macOS 发布脚本在打包前校验对应源码及未签名动态库的匹配关系；通用包保存
两个架构的元数据。签名阶段不单独签署许可证与源码归档，由应用外层签名封装
这些资源，之后再次检查源码材料的原始校验值。源码与动态库版本不匹配、遗漏
材料或校验失败时，发布脚本会终止。

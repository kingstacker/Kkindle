# Kkindle 项目 Agent 约定

## Shell 使用约定

- 默认 shell 是 **Windows PowerShell 7**。
- 在编写或运行命令之前，除非用户明确说明 shell 是 Bash、WSL、Git Bash 或其他 shell，否则默认使用 **PowerShell 语法**。
- **默认禁止使用 Bash 专用语法**（例如 `&&`、`||`、`export VAR=...`、`VAR=value command`、`$(...)`、`;` 分隔等），改用 PowerShell 等价写法。
- 对包含逗号的参数加引号，例如：`'a,b,c'`。
- JSON 参数优先使用单引号包裹，例如：`'{"ids":[1,2,3]}'`。
- 注意 PowerShell 中的特殊字符：`$`、`;`、反引号（`` ` ``）、引号、反斜杠、带空格的路径以及逗号；需要时使用反引号转义或单引号字符串避免意外解析。
- 在路径可能产生歧义、跨目录操作、读写具体文件、复制移动文件或排查问题时，**优先使用 Windows 绝对路径**，例如：`C:\Users\name\project`。
- 如果只是项目内部命令，且已经明确处于项目根目录，可以使用相对路径。

## 调试 EXE 生成约定（用户明确要求，2026-09-09 更新）

- 当前仓库 `global.json` 固定使用 .NET SDK **10.0.400**；Avalonia 主包和桌面包为 **12.1.1**，`Avalonia.Controls.WebView` 为 **12.1.0**。
- **每次修改代码后，必须重新生成带完整 .NET Runtime 的 x64 Debug EXE（self-contained）并同步到 `artifacts\Kkindle-debug-win-x64-latest\`**，然后在回复中告知用户 exe 路径。调试版不得使用依赖用户机器预装 .NET 的 framework-dependent 输出；`Kkindle.exe` 必须能够在未安装对应 .NET Runtime 的 Windows 机器上启动。
- 构建命令（项目根目录执行；必须使用 `publish`、`win-x64` 和 `--self-contained true`）：
  - `dotnet publish src/Kkindle.Desktop.Windows/Kkindle.Desktop.Windows.csproj -c Debug -p:Platform=x64 -r win-x64 --self-contained true -p:PublishSingleFile=false`
- 构建输出（即调试 exe 所在目录）：`src\Kkindle.Desktop.Windows\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\publish\Kkindle.exe`
- 发布目录必须包含随 EXE 一起分发的 .NET Runtime 文件，至少检查 `hostfxr.dll`、`hostpolicy.dll` 和 `coreclr.dll`；缺少任一文件时视为构建不合格，不得同步或启动。
- 同步到 latest 目录时**只覆盖文件、不删除内容**，保留 `data\`、`backups\`、`kkindle-crash.log` 等运行时数据：
  - `robocopy src\Kkindle.Desktop.Windows\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\publish artifacts\Kkindle-debug-win-x64-latest /E /XF kkindle-crash.log /XD data backups`（robocopy 退出码 ≤7 视为成功）
- 启动或交付时使用同步后的 `artifacts\Kkindle-debug-win-x64-latest\Kkindle.exe`，不要直接使用未发布的 `bin\...\win-x64\Kkindle.exe`。
- 除非用户明确要求，不生成 Release EXE、安装包或便携包；默认只出保留完整调试工具、.NET Runtime 与其他运行依赖的 x64 Debug EXE（与 `AI_HANDOFF.md` 第 8 节一致）。

## Linux Debug 验证信息（2026-08-22）

- Linux Debug 入口：`src/Kkindle.Desktop.Linux/bin/Debug/net10.0/Kkindle`。
- 构建命令：`dotnet build src/Kkindle.Desktop.Linux/Kkindle.Desktop.Linux.csproj --no-restore`。
- 测试命令：`dotnet test tests/Kkindle.Tests/Kkindle.Tests.csproj --no-restore`。
- 最近一次结果：237 项测试通过，Linux Debug 构建 0 警告、0 错误；Debug UI 已实际验证 EPUB→AZW3 转换成功。
- Linux/macOS 不是 Windows x64 EXE 同步规则的替代目标；只有用户明确要求时才构建对应 Debug 产物。

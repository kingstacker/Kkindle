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

## 构建与调试产物约定（2026-11-03 更新）

- 当前仓库 `global.json` 固定使用 .NET SDK **10.0.400**；Avalonia 主包和桌面包为 **12.1.1**，`Avalonia.Controls.WebView` 为 **12.1.0**。
- 修改代码后，默认构建当前开发平台上与任务相关的项目；开发者明确指定目标平台时，按指定目标构建。根据改动运行相应测试，并如实说明未覆盖的平台或无法完成的验证。
- 不因修改代码而默认构建其他平台，也不默认生成 Release 版本、安装包或便携包。纯文档修改无需构建应用，除非开发者明确要求。
- Windows 是当前平台或指定目标时，生成带完整 .NET Runtime 的 x64 Debug EXE（self-contained），并同步到 `artifacts\Kkindle-debug-win-x64-latest\`；回复中告知用户 EXE 路径。调试版不得依赖用户机器预装 .NET。
- Windows 构建命令（项目根目录执行；使用 `publish`、`win-x64` 和 `--self-contained true`）：
  - `dotnet publish src/Kkindle.Desktop.Windows/Kkindle.Desktop.Windows.csproj -c Debug -p:Platform=x64 -r win-x64 --self-contained true -p:PublishSingleFile=false`
- 构建输出（即调试 exe 所在目录）：`src\Kkindle.Desktop.Windows\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\publish\Kkindle.exe`
- 发布目录必须包含随 EXE 一起分发的 .NET Runtime 文件，至少检查 `hostfxr.dll`、`hostpolicy.dll` 和 `coreclr.dll`；缺少任一文件时视为构建不合格，不得同步或启动。
- 同步到 latest 目录时**只覆盖文件、不删除内容**，保留 `data\`、`backups\`、`kkindle-crash.log` 等运行时数据：
  - `robocopy src\Kkindle.Desktop.Windows\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\publish artifacts\Kkindle-debug-win-x64-latest /E /XF kkindle-crash.log /XD data backups`（robocopy 退出码 ≤7 视为成功）
- 启动或交付时使用同步后的 `artifacts\Kkindle-debug-win-x64-latest\Kkindle.exe`，不要直接使用未发布的 `bin\...\win-x64\Kkindle.exe`。
- macOS Debug 构建：`dotnet build src/Kkindle.Desktop.MacOS/Kkindle.Desktop.MacOS.csproj -c Debug`。
- Linux Debug 构建：`dotnet build src/Kkindle.Desktop.Linux/Kkindle.Desktop.Linux.csproj -c Debug`。

## Android 模拟器验证（macOS，2026-11-02）

- JDK 21 在 `~/.local/jdk-21/Contents/Home`（`JAVA_HOME` 要指向 `Contents/Home`，指向 `~/.local/jdk-21` 会报 “Could not find required file jar”）。
- SDK 在 `~/Library/Android/sdk`，AVD 为 `kkindle-api36`（API 36，x86_64）。
- 本机（Hackintosh，i5-13600KF，macOS 26）的 Hypervisor.framework 会透传 WAITPKG，guest 执行 TPAUSE 时触发 `invalid opcode`（`delay_halt_tpause`）内核 panic。**不能从 Android Studio / AVD Manager 直接启动**，必须带 `-qemu -append clearcpuid=517`：

  ```bash
  ~/Library/Android/sdk/emulator/emulator -avd kkindle-api36 \
    -no-snapshot -no-boot-anim -no-audio \
    -gpu swiftshader_indirect -memory 4096 -cores 4 \
    -qemu -append clearcpuid=517
  ```

- 构建 Debug APK（项目根目录）：
  `dotnet build src/Kkindle.Mobile.Android/Kkindle.Mobile.Android.csproj -c Debug -p:RuntimeIdentifier=android-x64`
- APK 输出：`src/Kkindle.Mobile.Android/bin/Debug/net10.0-android36.0/android-x64/com.kingstacker.kkindle-Signed.apk`
- 安装 / 启动：`adb -s emulator-5554 install -r <apk>`，然后 `adb shell am start -W -n com.kingstacker.kkindle/crc64fcb7126634234113.MainActivity`（LaunchActivity 名称以 `cmd package resolve-activity --brief com.kingstacker.kkindle` 为准）。
- 崩溃后残留的 `~/.android/avd/kkindle-api36.avd/*.lock` 会导致 “Running multiple emulators with the same AVD”，无模拟器运行时删除即可。
- 最近验证：Debug APK（android-x64）安装启动正常，EPUB 导入 → 书架 → 阅读 → 章节切换 → 阅读数据统计均通过，logcat 无 FATAL。

## Linux Debug 验证信息（2026-08-22）

- Linux Debug 入口：`src/Kkindle.Desktop.Linux/bin/Debug/net10.0/Kkindle`。
- 构建命令：`dotnet build src/Kkindle.Desktop.Linux/Kkindle.Desktop.Linux.csproj --no-restore`。
- 测试命令：`dotnet test tests/Kkindle.Tests/Kkindle.Tests.csproj --no-restore`。
- 最近一次结果：237 项测试通过，Linux Debug 构建 0 警告、0 错误；Debug UI 已实际验证 EPUB→AZW3 转换成功。
- 在 Linux 上开发时，默认验证 Linux Debug 产物；指定其他平台时按上述目标平台约定执行。

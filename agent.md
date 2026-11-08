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

## 本地 Git 提交与远端同步前的敏感信息审查

- **本地提交前必须审查**：每次本地 `git commit`（包括 `git commit -a`、`git commit --amend`）前，检查最终将写入提交的内容，包括实际暂存内容、拟纳入提交的修改和未跟踪文件，以及命令自动暂存的修改。即使只提交到本地、不执行 `git push` 或未配置远端，也必须审查；不得先提交再等推送时检查。
- **远端同步前再次审查**：每次 `git push` 或向远端上传源码、补丁、发布产物前，检查所有待推送提交及相关产物。本地提交和远端同步的审查范围均包含代码、配置、CI 脚本、文档、测试数据，以及本次提交或同步包含的日志、截图和二进制产物；不得只看文件名或依赖 `.gitignore`。
- **重点检查内容**：真实 API Key、AccessKey/SecretKey、密码、Token、Cookie、认证请求头、含凭据的连接串或 URL、SSH/TLS 私钥、签名密钥及证书密码，以及个人信息、内部业务数据和其他不应公开的内容。优先使用已有的敏感信息扫描工具并人工核实结果；扫描工具必须启用脱敏输出，无法检查的内容应明确说明，不得宣称审查通过。
- **禁止提交、同步与脱敏要求**：真实密钥、凭据、私钥及含敏感信息的本地配置、日志、数据库、备份不得进入提交或远端。将凭据改为环境变量、系统密钥库或不受版本控制的本地配置；示例和测试使用明确的占位符或虚构数据，例如 `<API_KEY>`、`<REDACTED>`，不得用真实值的截断片段作为示例。必要时补充 `.gitignore`，但忽略规则不能清除已跟踪文件或提交历史中的敏感内容。
- **发现问题立即阻断**：发现敏感信息或无法排除泄露风险时，暂停本次本地提交及远端同步，先提示开发者处理。提示仅包含文件路径、行号（可定位时）、风险类型和修改建议；不得在终端输出、回复、日志、提交说明或 PR 描述中复述真实敏感值。未解决的问题不得通过跳过检查、强制推送或直接上传绕过。
- **已进入历史的敏感信息**：仅在当前文件中删除或脱敏，不能消除本地提交历史中的敏感信息，后续推送仍可能泄露。必须检查相关本地提交及待推送提交，并提示开发者撤销或轮换可能泄露的凭据、处理受影响的历史；未经开发者授权，不得重写历史或强制推送。
- **修正并确认后才恢复提交、同步**：处理完成后，重新审查最终将写入本地提交的内容；远端同步前同时复查所有待推送提交及相关产物。确认问题已消除，并由开发者确认敏感内容已修正或脱敏后，才可继续本次已授权的本地提交和远端同步。复查后若又修改、重新暂存或新增待提交、同步内容，必须再次审查。
- **如实报告结果**：提交或同步的交付说明中简要记录审查范围、结果及未完成的检查；仍有未处理问题或检查未通过时，明确说明提交、同步尚未执行。

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
- 当前 Hackintosh 开发机的 Hypervisor.framework 会透传 WAITPKG，guest 执行 TPAUSE 时触发 `invalid opcode`（`delay_halt_tpause`）内核 panic。**不能从 Android Studio / AVD Manager 直接启动**，必须带 `-qemu -append clearcpuid=517`：

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

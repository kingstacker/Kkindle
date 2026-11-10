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

## 分区账号与用户加密数据约定

- 中国区和国际区的账号、会话、书库、同步文件与密钥配置分别保存；禁止跨区复制、回退或合并。切区后必须重新加载对应书库，在重新打开应用前阻止旧书库的同步和按需下载。
- 账号云同步必须设置同步密钥。密钥只在用户设备上受保护地保存；不上传、不写入备份、请求、日志或云对象路径，也不在云路径中暴露可快速验证口令的密钥摘要。
- 加密书籍及封面的云对象路径不得包含明文内容哈希；使用慢速派生且用途隔离的命名密钥计算 HMAC。账号命名隔离于区域和账号 ID，不能依赖服务器域名；自备加密存储的随机盐随加密快照合并保留。新对象使用 `KKINDLE-SYNC3` 补齐长度，真实长度须在认证密文内；协议／最低读取版本为 6，旧对象只读发现，不猜测哈希路径或自动清空历史云数据。
- 全量 `.kkindle` 数据导出和导入必须加密。未配置密钥时，在打开文件选择或分享界面前提示设置同步密钥。整份备份通过认证后才替换书库；错误密钥或损坏文件不能部分覆盖现有数据。
- 用户自行牢记和保管同步密钥，遗忘后无法解密旧云数据或备份。不得增加服务器保管密钥、找回密钥或通过账号密码重置解密用户数据的功能。
- 本次用户管理服务在独立的 `~/Desktop/Kkindle_server` 项目中，PostgreSQL 只保存用户、会话、额度、汇总用量、管理记录和同步对象索引（不透明同步路径 → 随机存储 ID），不保存书名、正文或密文。书籍与快照以不透明加密文件存储在服务器目录或 S3 兼容桶（如七牛 Kodo），不增加管理端查看或下载书籍的入口。账号云同步的字节通过服务端签发的短期链接在客户端与存储之间直传，链接不携带账号会话；S3 上传完成后服务端复制到独立不可变发布键再索引，删除/覆盖对象在链接过期前按大小计入瞬时占用（最多约两倍额度）。用户自定义 S3 / WebDAV 同步仍直连用户自己的存储，与账号服务器无关。客户端仓库中的 SQLite 服务保留为协议测试用途，需与正式服务的签名直传协议保持一致。
- 封禁后可删除用户的云文件或账号及云文件；删除须持久化为任务并阻断同步，处理并发上传与失败重试，不能在尚未清理完毕时显示成功。

## 会员、设备与额度约定

- 会员绑定账号；支持登录后兑换一次性的永久功能会员或 **1 年期 Plus** 激活码，不发行多年 Plus 激活码。三档：Free（免费）、功能解锁（¥88 永久，接口值 `lifetime`）、Plus（¥128/年，接口值 `pro`）。Plus 不附赠功能解锁，到期即撤销全部官方云同步与下载、自定义 S3 / WebDAV（已购功能解锁的自备存储权益保留）。
- Plus 剩余有效期从当前时刻起最多 5 个 UTC 自然年；付费、激活码与后台授予共用上限，待支付订单预留容量，超限不能创建订单、到账或兑换，不部分扣码。年费商品和激活码均为 365 天。旧超限有效期不截断。兑换从当前区域会话确定账号，校验响应的账号 / 区域 / 当前设备；网络失败保留同一次请求 UUID，重试仅刷新现有权益，不重新发放。权益撤销、到期或注销不能让已用码复用。完整激活码不进入日志、备份、云对象或客户端持久配置，退出 / 切区 / 关闭账户页清空输入。
- 所有账号最多同时登录 6 台设备，服务端在账号锁内检查数量。重复登录同一设备轮换会话；用户可以查看并踢出自己的设备。踢出立即撤销会话并释放名额，管理员停用的设备不能重新登录。
- 客户端后台每 24 小时检查一次，成功时间跨重启保留；网络失败保留登录并重试。失效后清除对应区域会话并提示重新登录。登录、手动刷新、自定义功能使用仍实时验证服务端权限。
- 所有档位免费：本地阅读、USB Kindle 书籍/批注双向同步、插件、自定义 AI 接口和加密备份。Free 没有任何多端同步。功能解锁或有效 Plus 可用自备 S3 / WebDAV 同步书籍、阅读进度、书签和批注，在对象存储创建前验证。全部官方云同步仅给有效 Plus，非 Plus 对所有同步对象及快照返回 `402 plus_required`，客户端停止整个同步。
- 设备身份由本机随机生成并受平台密钥库保护，不采集硬件序列号。账号会话、设备身份、检查时间和会员状态不进入便携备份或云同步，不加入生产绕过授权的开关。
- Kkindle 云空间和下载额度只给有效 Plus：10 GiB 空间、每个会员年度 150 GiB 下载（按购买周期发放，到期作废）。不限制月流量，上传没有流量额度；月度计数仅作统计。管理员只调整空间及当前年度下载总额，年度圆环包含调整后的本期总额，保留原已用量；跨月、撤销重开、提前续费不补回额度。额外空间包和下载包仍下架，旧余额保留，使用 Kkindle 云仍要求有效 Plus。
- Plus 过期须提示用户：官方云同步与下载立即停用，云端书籍等全部同步数据从到期时刻起保留一个 UTC 自然月，期间续费恢复访问，保留期结束后自动销毁。自动销毁任务须持久化、失败重试并在删除前持账号锁重新核对会员状态；不删除账号或本地书库。下载签名链接不能超过会员到期时间。
- 功能解锁、Plus 与扩容服务均不支持退款，包括预付未来年度和未使用余额；桌面与移动端在购买前清晰展示。用户只能取消未支付订单。后台可撤销功能解锁、Plus 全部剩余有效期或两项会员权益，要求原因和明确确认，取消同类待支付订单并保留金额、激活码和历史用量；撤销操作不产生资金退款。
- 桌面账户中心为主窗口右侧的内嵌功能页，从左侧账户入口打开，不能打开独立账户弹窗。云同步设置不显示账户行；已登录时仅在 Kkindle 云同步名称前显示小绿点，会员与区域异常仍须提示。复用功能区的自定义字体、标题、直角布局、细分隔线、黑白按钮与设置页折叠表单；紧凑展示账户摘要和年度额度，退出登录放在账户摘要内，不设固定底栏。切到其他功能页须清空密码和激活码输入，主窗口退出取消请求并解绑事件；保留真实额度、设备管理、会员购买与激活、账户资料和注销确认流程。
- Plus 被后台撤销后重新开通保留原会员年度已下载记录和额度标识，不重发已覆盖期间的额度；云空间占用按实际文件计算，付款不清零。撤销 Plus 后云数据保留一个 UTC 自然月，单独撤销功能解锁不会改变 Plus 状态或原保留期限。旧订单和已用码重放不能恢复权益。保留期限已过的旧云数据须先清理，不能通过晚到续费绕过销毁。
- 多年 Plus 授予的下载额度按最多 365 天的时期逐期生效，不能立即消费未来多年额度；拆分旧合并额度时保留总额度、已用总量和首期标识，避免迁移重复发放。
- 正式独立服务注册默认同一 IPv4 / IPv6 /64 来源滚动 24 小时 3 个、30 天 10 个成功账号，计数跨重启、并发、注销删除保留；后台支持 IP / 网段、邮箱、用户名黑名单。来源只取连接和明确可信代理；封禁产生的身份黑名单在删除账号后保留。客户端应明确提示注册限制和黑名单错误，邮箱目前只有格式及唯一性校验，不能宣称它已验证邮件所有权。
- 官方云下载计量，客户端缓存远端快照（按对象修改时间），内容未变的本机快照不重复上传（30 天内），避免空闲同步消耗额度。
- 本机订单只有演示价格和管理员确认，不发生真实支付。生产购买默认关闭，真实支付网关尚未接入；示例价格不代表开发者确认的正式定价。金额与权益来自服务端商品快照，到账必须事务执行、幂等并保留管理记录。

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

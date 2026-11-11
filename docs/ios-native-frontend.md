# iOS 原生前端

从 2026-11-07 起，iOS 项目默认使用 **Swift + SwiftUI** 界面。
`UseSwiftUIFrontend=true` 由 `Kkindle.Mobile.iOS.csproj` 设置。
Android 继续使用共享的 Avalonia 界面。

## 界面与业务的分工

SwiftUI 负责四个标签页、导航、列表/网格、滚动、输入框、按钮、开关、分段选择器、
滑块和弹窗。阅读设置的开关通过 `UIViewRepresentable` 使用独立的 UIKit `UISwitch`，
其尺寸和 Accessibility 范围跟随实际控件。系统文件选择器、分享面板、确认框由 UIKit 显示。
`IOSNativeSceneDelegate` 在窗口显示前创建 SwiftUI 根页面。
UIKit 恢复旧版 Avalonia 的场景会话时，`AppDelegate` 会在激活时补建原生窗口，
不删除书库或设置、不要求卸载重装。

`NativeTheme.swift` 复用原有 `MobileTokens.axaml` / `MobileTheme.axaml` 的视觉规则：
京华老宋界面字体、24 点页边距、轻灰按钮、6 点封面圆角和 16 点浮动工具栏。
普通文本、导航栏标题与按钮、分段控件标题和底部标签都使用该字体；系统确认框保持系统字体。
四个标签页的抬头共用 `NativeTheme.pageTitleFont`，字号和字形保持一致；
书库、阅读资料、阅读数据、我的不再各自设置标题大小。
`NativeBookshelf.swift` 保留紧凑的书库标题、三列书架、细进度条和收藏夹封面卡片；
“收藏夹视图”与网格视图、列表视图放在同一组，均可通过显示菜单切换。
详情页使用居中封面和底部固定的阅读按钮。“我的”和阅读资料保持自定义页面布局。
显示选项提供网格视图、列表视图、收藏夹视图和排序；画廊模式在“我的”中的书库设置里启用。
画廊模式在网格和收藏夹中隐藏标题、作者与进度并收紧行距；书库设置还可开启书籍同步状态图标。
同步图标默认关闭，开启后按本机最近的同步基线显示未同步、已同步或待下载，
不查询云端实时状态；偏好会随其他界面设置一起保存。
书库设置还提供默认关闭的炫彩模式：优先从当前可见书籍的封面各取一种代表色，
彩色封面优先选择有饱和度的区域并适度提亮，黑白封面保留中性色。
每种颜色以对应封面的实际位置为中心向周围晕染，滚动时同步移动，不按色相重新排列。
书库将最新颜色和归一化位置发布到共享状态，阅读资料、阅读数据、我的及后续打开的页面
均接续这套炫彩；初次尚未取得可见封面时，使用最近更新书籍的取色作为回退，
回退取色异步完成不会覆盖书库的实时颜色。无可用封面时使用普通背景。
阅读正文使用较淡的颜色叠层，以保留书籍内容和纸张主题的可读性。
原生交互不需要改成系统大标题或分组设置页的默认外观。
高级筛选使用独立的卡片式页面：顶部返回、标题和右上角“清除”，阅读状态与格式为胶囊选项，
作者、标签和分类进入可搜索的选择页；底部“查看 N 本书”按当前草稿、快捷筛选和搜索
计算数量，关闭或返回不会改动书库，只有确认后才应用并重新请求筛选结果。
iOS 26 起筛选卡片和按钮使用系统玻璃材质，导航按钮使用系统工具栏；旧系统使用回退样式。

展开收藏夹后使用与书库相同的 `NativeBookShelf`、封面尺寸、书名/作者字体和进度条，
两处也共用网格/列表偏好与排序。收藏夹添加、长按移出、清空、合并和解散继续走原有业务。
阅读页的搜索复用书库的 `NativeSearchField`，在顶部放大镜左侧展开；提交后，
结果显示在工具栏下方的紧凑浮层。搜索框保持在当前工具栏中，结果不再另开搜索页面。

阅读设置使用独立的紧凑底部面板。iOS 16 及以上的面板高度不超过 340 点（PDF 为 220 点），
并按可用屏幕高度缩小，留出上方正文供实时预览；iOS 15 使用 UIKit 的半屏面板。
纸张、字号、行距、页边距和方向使用紧凑行布局，更多选项在面板内滚动，
设置调整仍直接应用于正文。
段间距提供紧凑、标准、宽松三档，分别在书籍原有段落间距上增加 0、0.25、0.5 个字号的留白。
默认紧凑保留原来的排版。横排增加段落后的纵向间距，竖排增加段落之间的列间距，
不修改段内行距或正文偏移。该设置会保存，改变时重新排版并使页数缓存失效。

iOS 主图标与 macOS 共用 `scripts/macos/Kkindle.icon` 的 Icon Composer 图层，
由 ACTool 生成 `Kkindle` 图标栈及浅色、深色、着色外观。iOS 26 使用分层液态玻璃图标，
旧系统使用同源生成的平面回退图标。各平台“关于”均已移除咖啡支持入口及二维码弹层。

C# 的 `ApplicationModules` 继续负责书库、SQLite、阅读记录、同步、AI、词典、
Kindle 邮箱和备份。数据目录、书籍文件、同步协议和已有设置文件保持兼容。
Swift 与 C# 通过同一进程内的 C ABI 传递 JSON 请求、结果和状态事件，不运行本地服务器。
`IOSNativeJsonContext` 提供显式生成的 JSON 元数据，桥接序列化不依赖运行时反射。

阅读正文继续使用现有的 EPUB 排版器和 PDFium。`ReaderPage.UseNativeFrontend()`
隐藏 Avalonia 的阅读工具栏、弹窗、选择菜单等界面，只向 SwiftUI 输出状态和操作。
单独的 `AvaloniaView` 作为 SwiftUI 阅读页的子控制器显示；它不承担窗口导航。
长按选字、翻页、进度保存和内容定位仍由共享阅读器处理。

这一边界可以避免将 UIKit 按钮叠在整棵 Avalonia 场景之上时，按钮位置、滚动裁剪、
弹窗遮挡和键盘焦点需要分别同步的问题。所有可操作控件现在处于原生页面层级中。

## 已接入的页面和操作

- 书库：网格/列表、搜索、筛选、排序、继续阅读、文件导入和 TXT 规则预览。
- 书籍：详情、编辑信息/封面、修改书名、收藏、加入收藏夹、回收站和发送到 Kindle。
- 收藏夹：创建、重命名、添加/移出书籍、清空、合并和解散。
- 阅读资料与统计：书签/划线/笔记、按位置打开、导出、阅读时长、每日数据和勋章。
- 我的：外观、S3/WebDAV 同步、参数导入/导出、AI、邮箱、备份、回收站、词典和关于。
- 阅读器：原生工具栏、进度、目录、正文搜索、排版/PDF 缩放、朗读、批注、词典、翻译和 AI。

同步继续使用 `MobileSync` 的并发控制和删除确认流程；备份/恢复继续使用
`MobileDataCoordinator` 的数据操作屏障。恢复完成后会阻止继续操作并提示重启。
用户账号与同步设置的登录、退出、保存和立即同步按钮位于列表内容末尾，随页面滚动，
不再固定在底部安全区；登录状态切换后账号页回到顶部，显示用户名和会员状态。
Plus 账号打开“同步”时默认显示账号云同步；用户在同步页保存过 S3 / WebDAV 后，
再次打开会直接显示保存的同步方式，登录 Plus 也不会覆盖该选择。
从未开通过 Plus 的账号在会员与设备区显示“免费试用”卡片：只有手动打开领取开关才会领取一次
7 天 Plus 试用（100MB 云空间、200MB 累计下载）；试用期显示到期日，到期后云端数据保留 7 天。
正式 Plus 为 10GB 云空间、每会员年度 50GB 下载，到期后云端数据保留一个月。
登录后的账号页沿用电脑版账号中心的账户卡：头像、用户名、区域、会员徽标与有效期，
刷新和退出登录收在同一张卡内；已登录设备按当前设备优先、最近活跃排序，显示名额
计数、占用格、平台图标、相对活跃时间和“退出此设备”操作。云空间与流量统计卡提供
云端空间的已用、总额、百分比和剩余空间，下载流量的会员年度已用、额度、百分比、
年度剩余、本月下载、额外购买余额和本期有效期；Plus 未开通或到期时显示对应的锁定说明与续费提示。
云空间与流量使用与电脑端一致的环形图：80 点圆环、7 点描边，中心显示百分比，
左侧是已用与总额，分隔线下方是剩余、本月下载和额外流量。
登录与注册信息位于页面顶部，服务器区域只在未登录的登录/注册表单中显示，
位于登录按钮之前；登录后不再显示区域切换。
账号页展示注册邮箱验证状态，可发送 8 位验证码完成验证；未登录页提供“忘记密码”，
通过邮箱验证码重置密码后回到登录。Plus 到期前 30、7、1 天的提醒以账号页横幅显示。
会员方案在同一张卡片内以 Free、功能解锁、Plus 分段切换，也可在卡片上左右轻扫；
默认显示当前付费方案，Free 账户默认显示推荐的 Plus，切换时不会在页面里堆叠三张卡片。
方案标题旁标注当前账号区域；国际区会员计划暂未上线，方案区只显示“国际区会员计划暂未上线”，
不显示中国区价格或购买入口。
书签的章节路径、fragment 和 `ReaderContentPosition` 会完整传过原生桥接层。
关闭阅读会取消当前阅读会话的搜索、AI 和翻译请求。
界面偏好在点击时立即更新，再异步保存；请求版本检查避免旧回复覆盖新的选择。
原生开关的 Accessibility 标识位于开关自身，滚动后不会把标签所在的整行当成开关点击区。
工具栏搜索提交、关闭和重新搜索会取消过期请求；旧结果不会回写当前搜索。

阅读页四周的 SwiftUI 留白使用与 `ReaderPalette` 相同的纸张颜色（白、米、绿、黑），
正文上下不出现色带。选中文字后点击正文只取消选择，不翻页，也不打开工具栏。
目录打开时标记并滚动到当前章节；插图等没有独立目录项的页面归属前一个目录项。
阅读资料中的 EPUB 划线和笔记显示日期，导出仍写入章节路径或页码。
阅读页的提示显示在当前工具栏上方，不遮挡选择工具栏。

阅读状态、批注弹窗和 AI 流式文本由独立的 `NativeReaderSession` 发布，只有阅读页观察它。
翻页、朗读进度和 AI 输出不会让书库等标签页重新计算。书库刷新时只替换有变化的数据，
书库和阅读资料每次渲染只做一次筛选、排序或分组。

## 构建

需要 macOS、Xcode 命令行工具、仓库指定的 .NET SDK 和 iOS workload。
MSBuild 自动调用 `NativeUI/build-native-ui.sh`，按目标架构编译并链接 Swift 静态库。
最低系统版本为 iOS 15；iOS 26 的控件材质由系统提供。

Intel 模拟器 Debug：

```bash
dotnet build src/Kkindle.Mobile.iOS/Kkindle.Mobile.iOS.csproj \
  -c Debug -r iossimulator-x64 -p:UseSharedCompilation=false
```

Apple Silicon 将 RID 改成 `iossimulator-arm64`。

模拟器产物：

```text
src/Kkindle.Mobile.iOS/bin/Debug/net10.0-ios/iossimulator-x64/Kkindle.Mobile.iOS.app
```

真机使用 `ios-arm64`，签名、安装和启动见 [ios-development.md](ios-development.md)。
原生 Swift 编译脚本也支持 `ios-arm64`，但模拟器通过不能代替真机验证。

必要时可以显式构建原有界面：

```bash
dotnet build src/Kkindle.Mobile.iOS/Kkindle.Mobile.iOS.csproj \
  -c Debug -r iossimulator-x64 -p:UseSwiftUIFrontend=false -p:Registrar=static \
  -p:IntermediateOutputPath=obj/Debug/ios-legacy-verify/ \
  -p:OutputPath=bin/Debug/ios-legacy-verify/
```

这个开关用于兼容性排查。旧前端产物位于
`src/Kkindle.Mobile.iOS/bin/Debug/ios-legacy-verify/Kkindle.Mobile.iOS.app`。
使用独立中间目录和输出目录，避免两个前端混用 Registrar 和构建缓存。

## 回归测试

共享移动端测试包括原生 JSON 字段/枚举、设置默认值、精确阅读位置，以及打开失败时
不显示 Avalonia 控件、不覆盖已有进度的检查：

```bash
dotnet test tests/Kkindle.Mobile.Tests/Kkindle.Mobile.Tests.csproj -c Debug
```

实际触摸测试位于 `tests/Kkindle.Mobile.iOS.UITests`。测试启动独立的
`com.kingstacker.kkindle.nativepreview`，使用真实 App 二进制和单独的数据容器。
准备脚本只复制数据库、书籍、封面和字体，排除连接设置、密钥和真实界面偏好。
它会在副本中把一本 EPUB/PDF 标记为 `Native UI EPUB` / `Native UI PDF`，
并重置已有 preview 容器中的测试数据。不要在源 App 正在写数据库时复制数据；
使用已停止 App 的本机数据快照。

```bash
SIMULATOR_ID="<booted simulator UUID>"
APP="src/Kkindle.Mobile.iOS/bin/Debug/net10.0-ios/iossimulator-x64/Kkindle.Mobile.iOS.app"
SEED="<local snapshot>/Kkindle"

python3 tests/Kkindle.Mobile.iOS.UITests/prepare-preview.py \
  --app "$APP" --simulator "$SIMULATOR_ID" --seed "$SEED"

python3 tests/Kkindle.Mobile.iOS.UITests/run-tests.py \
  --simulator "$SIMULATOR_ID" --results /tmp/kkindle-native-uitest-results
```

`--results` 必须使用尚不存在的路径。运行器使用独立构建目录并对测试宿主做 ad-hoc 签名，
避免模拟器复用旧的未签名测试代码。测试覆盖标签页、连续打开输入弹窗、
系统确认框取消/重开、键盘收起/重新聚焦、排版滚动和持久化、PDF 显示设置，
以及原生工具栏周围的正文触摸/翻页。结果包含 `.xcresult`、构建日志和 `screenshots/`。
截图通过 CoreSimulator 导出，在 XCTest 截图因显卡驱动返回黑图的机器上也可核对实际显示。
这些测试不会点击真实云端同步、发送邮箱或恢复备份。
`--only` 可重复指定，用于一次运行多个相关用例；阅读设置/PDF 用例还检查面板
位于屏幕下半部，并验证滚动后的开关和字体、纸张、方向持久化。
书库长列表滚动用例需要在隔离副本中准备足够多的书籍，再通过 `--only` 单独运行。
另有收藏夹/书库网格和列表尺寸一致、添加与长按移出、工具栏搜索框向左展开、
无结果/有结果搜索及正文跳转、段间距三档切换与持久化的用例。
纸张留白用例由运行器截图后逐列取色核对；选择用例检查点击正文只取消选择；
目录用例检查当前章节可见；阅读资料用例先载入资料页，再划线并确认新条目显示日期。
账号与会员用例需要本地中国区和国际区账号服务，用 `--only` 单独运行；
免费试用用例确认新账号不会自动领取，只有打开领取开关后才开通 7 天试用并锁定开关，
云空间和同步流量随后显示 100MB 与 200MB。

覆盖升级验证使用第二个独立 Bundle `com.kingstacker.kkindle.nativeupgradepreview`。
先用 `--upgrade-preview` 安装并启动旧前端，再用相同参数加 `--preserve-data`
覆盖安装原生前端。脚本为该容器写入人工偏好（浅色、米色纸、125% 字号和首行缩进），
升级测试检查已有 EPUB 与这些设置是否保留：

```bash
python3 tests/Kkindle.Mobile.iOS.UITests/run-tests.py \
  --simulator "$SIMULATOR_ID" --results /tmp/kkindle-native-upgrade-results \
  --only testLegacyUpgradePreservesLibraryAndPreferences
```

常规运行器运行十四项交互用例，覆盖升级用例需要完成上述安装顺序后单独执行。

验证结束后，模拟器只保留最新的 `com.kingstacker.kkindle`，显示名为 `kkindle`。
先用最终构建覆盖安装该 Bundle，保留原书库和设置，然后清理测试副本和宿主：

```bash
xcrun simctl install "$SIMULATOR_ID" "$APP"
xcrun simctl uninstall "$SIMULATOR_ID" com.kingstacker.kkindle.nativepreview
xcrun simctl uninstall "$SIMULATOR_ID" com.kingstacker.kkindle.nativeupgradepreview
xcrun simctl uninstall "$SIMULATOR_ID" com.kingstacker.kkindle.nativefrontenduitests.xctrunner
xcrun simctl uninstall "$SIMULATOR_ID" com.kingstacker.kkindle.uitests.xctrunner
```

这些卸载命令仅用于对应的测试 Bundle 已安装时；正式 Bundle 通过覆盖安装更新。
CoreSimulator 覆盖安装时可能迁移数据目录，验证数据保留应比较文件内容，不依赖容器路径不变。

## 验证范围

本次迁移的共享移动端测试已通过 15 项。iOS 模拟器 Debug 已完成构建。
外观恢复后的标签页、连续输入弹窗、系统确认框、同步设置键盘、阅读正文触摸和 PDF
设置用例已通过；缩小阅读设置面板后的 EPUB、PDF、标签页三项回归也已通过。
进一步压低到 220 点后的 PDF 面板单项复测通过。
EPUB 用例验证开关滚动后可点击，纸张、字号、缩进和方向在关闭重开后保留，
并检查设置面板留出至少上半屏正文。
增加段间距并复用工具栏搜索框后，收藏夹样式/管理、工具栏搜索与正文跳转、段间距保存、
阅读设置滚动、正文触摸/翻页、PDF 显示设置、标签页导航和系统确认框共八项用例同轮通过。
共享移动端测试重新通过 15 项；横排/竖排排版和设置归一化测试通过 88 项，包含
段间距不改变段内行距/文字偏移、增大间距重新分页且不丢失文字的检查。
最终 `Assets.car` 已检查包含浅色、深色、着色的三个 `Kkindle` 图标栈及 Page/Spine 图层，
并包含旧系统需要的图标 PNG。iOS 模拟器和 macOS Debug 构建通过，
`ios-arm64` 的 Swift 模块交叉编译通过；后者不等于完整真机安装验证。
旧版到原生版的保数据覆盖升级已在独立模拟器容器中通过：启动日志确认恢复了旧场景会话，
UI 测试确认旧书库、浅色外观、米色纸、125% 字号和首行缩进保留。
2026-11-08 已完成 `ios-arm64` Debug 完整签名构建，并覆盖安装到 iPhone 16（iOS 27.2）。
正式 Bundle 为 `com.kingstacker.kkindle`，显示名为 `kkindle`。覆盖安装前已备份应用数据，
安装后、首次启动前的 317 个数据文件与备份逐文件 SHA-256 一致，共 70,742,621 字节，
原有书库、收藏夹、阅读进度、批注、书签和设置保留。普通启动成功，设备进程检查确认
新安装路径中的应用持续运行。本机验证记录位于 `artifacts/ios-device-review-20261108/`。
模拟器上的原生交互测试结果应以本次 `.xcresult` 为准；旧版真机测试记录不适用于新前端。
上述真机检查涵盖构建、覆盖安装、数据保留和启动，完整真机交互回归尚未执行。
目前没有把 SwiftUI 前端作为 Release/App Store 包验证。
云端同步、实际邮件发送、设备语音和备份恢复仍需使用测试账号/数据在真机上验收。

2026-11-08 审查修复后，共享移动端测试 15 项通过，iOS 模拟器 Debug 构建通过，
常规 UI 回归 14 项在 iPhone 17（iOS 26.5）模拟器上同轮通过。
米色、黑色、白色纸张截图的页面边缘从顶到底各为单一颜色。

阅读正文的渲染没有改写为 Swift。后续若要更换正文渲染器，应先验证跨平台内容定位、
批注、搜索和阅读统计，再替换该独立的显示层。

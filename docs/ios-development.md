# iOS 开发、真机构建与自动化测试

本文记录 `Kkindle.Mobile.iOS` 的真机构建、签名、安装、启动和自动化测试流程。
默认目标是从仓库直接生成可安装到已连接 iPhone 的 Debug 包，并使用真实触摸
验证界面，不使用 iPhone 镜像。

## 当前验证环境

以下是 2026-11-06 在 iPhone 16 + Xcode 26.5 上验证通过的流程。设备 ID、
Team ID、签名身份和描述文件 UUID 只应保存在本机环境，每次换机器、证书过期或
重新生成描述文件后都要重新读取，不要提交到仓库。

| 项目 | 获取方式 |
| --- | --- |
| 设备 | `xcrun devicectl list devices` 中选择已配对真机 |
| Device ID | 同一命令输出中的设备标识；下文用 `$DEVICE_ID` 表示 |
| Bundle ID | 构建后从 App 的 `Info.plist` 读取；下文用 `$BUNDLE_ID` 表示 |
| Xcode | `xcodebuild -version`；验证时为 `26.5`，Build `17F42` |
| .NET workload | `dotnet workload list`；验证时为 `ios 27.1.10018/10.0.100` |
| Team ID | Xcode target 的 Signing & Capabilities 中显示 |
| Codesign Key | `security find-identity -v -p codesigning` 的输出 |
| Provisioning UUID | 下方描述文件命令读取；只在本机使用 |

基础环境检查：

```bash
cd "$(git rev-parse --show-toplevel)"

xcodebuild -version
dotnet workload list
xcrun devicectl list devices
security find-identity -v -p codesigning
```

设备应显示为 `available (paired)`。如果设备未解锁、未信任或未开启开发者模式，
后面的安装和 XCUITest 都会失败。

## 查找签名和描述文件

签名身份：

```bash
security find-identity -v -p codesigning
```

描述文件位于：

```bash
PROFILE_DIR="$HOME/Library/Developer/Xcode/UserData/Provisioning Profiles"

for profile in "$PROFILE_DIR"/*.mobileprovision; do
  [ -f "$profile" ] || continue
  uuid="$(security cms -D -i "$profile" | plutil -extract UUID raw -)"
  name="$(security cms -D -i "$profile" | plutil -extract Name raw -)"
  printf '%s  %s\n' "$uuid" "$name"
done
```

应用描述文件应包含：

```text
iOS Team Provisioning Profile: <BUNDLE_ID>
```

XCUITest runner 使用另一个描述文件：

```text
iOS Team Provisioning Profile: <BUNDLE_ID>.uitests.xctrunner
```

## 构建真机包

本仓库当前只在 Debug 真机构建上完整验证过。使用静态 Registrar，并显式指定
`ios-arm64` 和真机 SDK：

```bash
DEVICE_ID="<DEVICE_ID>"
CODESIGN_KEY="$(
  security find-identity -v -p codesigning |
    sed -n 's/.*"\(Apple Development:.*\)"/\1/p' |
    head -n 1
)"
CODESIGN_PROVISION="<PROVISIONING_PROFILE_UUID>"

test -n "$CODESIGN_KEY"
test -n "$CODESIGN_PROVISION"

dotnet build src/Kkindle.Mobile.iOS/Kkindle.Mobile.iOS.csproj \
  -c Debug \
  -f net10.0-ios \
  -p:RuntimeIdentifier=ios-arm64 \
  -p:_SdkIsSimulator=false \
  -p:Registrar=static \
  -p:CodesignKey="$CODESIGN_KEY" \
  -p:CodesignProvision="$CODESIGN_PROVISION" \
  -v:q -clp:ErrorsOnly
```

产物：

```text
src/Kkindle.Mobile.iOS/bin/Debug/net10.0-ios/ios-arm64/Kkindle.Mobile.iOS.app
```

构建后从产物读取 Bundle ID，后续安装、启动和日志命令都使用这个变量：

```bash
APP="src/Kkindle.Mobile.iOS/bin/Debug/net10.0-ios/ios-arm64/Kkindle.Mobile.iOS.app"
BUNDLE_ID="$(plutil -extract CFBundleIdentifier raw "$APP/Info.plist")"
printf '%s\n' "$BUNDLE_ID"
```

构建成功时命令以 `0` 退出。当前项目会报告约 219 个既有编译警告；判断是否成功
应以退出码和错误数为准。

## 安装、启动和查看日志

安装：

```bash
xcrun devicectl device install app \
  --device "$DEVICE_ID" \
  "$APP"
```

普通启动并留在前台：

```bash
xcrun devicectl device process launch \
  --terminate-existing \
  --device "$DEVICE_ID" \
  "$BUNDLE_ID"
```

需要持续查看应用标准输出时附加 `--console`；该命令会占用终端，退出终端不会
自动卸载应用：

```bash
xcrun devicectl device process launch \
  --terminate-existing --console \
  --device "$DEVICE_ID" \
  "$BUNDLE_ID"
```

确认安装版本：

```bash
xcrun devicectl device info apps --device "$DEVICE_ID" \
  | rg -n -C 1 "$BUNDLE_ID"
```

### App 图标检查

构建包中的图标配置：

```bash
APP="src/Kkindle.Mobile.iOS/bin/Debug/net10.0-ios/ios-arm64/Kkindle.Mobile.iOS.app"
plutil -p "$APP/Info.plist" | rg 'CFBundleIcon|CFBundleDisplayName' -C 1
find "$APP" -maxdepth 1 \( -iname 'AppIcon*' -o -name 'Assets.car' \) -print
```

如果本机安装了 `pymobiledevice3`，还可以确认 SpringBoard 已登记图标：

```bash
pymobiledevice3 springboard state get --userspace \
  | rg -n -C 3 "$BUNDLE_ID"
```

当前图标是黑底白 K。图标文件存在但看起来“没有图标”时，也要检查是否位于
App 资源库、主屏下一页，或黑色图标与深色壁纸混在一起。

## 真机自动化测试原则

- 使用 `XCUITest` 在物理设备上合成真实触摸，不使用 iPhone 镜像。
- 测试 target 不嵌入 Kkindle，而是按 Bundle ID 启动已安装的
  被测 App。
- Avalonia 的大部分界面不会作为原生控件暴露给 Accessibility；不要把
  `app.buttons["标题"]` 当成稳定定位方式。优先使用窗口坐标、滑动和截图断言。
- 键盘不一定会被识别为 `Keyboard`。Avalonia 输入框的键盘使用
  `app.otherElements["inputView"]` 判断，而不是 `app.keyboards.count`。
- XCUITest 的 `tearDown` 会终止 App。测试结束后如需让 App 留在手机上，必须
  再执行一次普通启动命令。
- 测试 runner 会单独安装一个 `KkindleUITests-Runner`，这是正常现象。

## 临时 XCUITest 工程

本次会话的工程位于：

```text
/tmp/kkindle-uitest/
├── KkindleUITests.xcodeproj
├── KkindleUITests/KkindleUITests.swift
└── DerivedData/
```

这是临时测试工程，不会进入 App 包，也不会自动随仓库提交。机器重启或 `/tmp`
被清理后需要从下面的结构重新创建。

建议使用 Xcode 创建一个 **iOS UI Testing Bundle**：

- Project/Scheme 名称：`KkindleUITests`
- Target Bundle ID：`<BUNDLE_ID>.uitests`
- Team：在 Xcode 中选择当前开发者团队，不要把真实 Team ID 写入文档
- iOS Deployment Target：`15.0`
- Swift：`5`
- Automatic Signing：开启

测试 target 不需要添加 Kkindle 为 Host Application。测试代码直接启动已安装
应用：

```swift
let app = XCUIApplication(bundleIdentifier: "<BUNDLE_ID>")
app.launchEnvironment = environment
app.launch()
XCTAssertTrue(app.wait(for: .runningForeground, timeout: 30))
```

使用前把 `<BUNDLE_ID>` 替换为构建产物 `Info.plist` 中读取到的实际值。

坐标点击以窗口为基准，设备逻辑尺寸变化时仍需重新校准具体坐标：

```swift
private func tap(_ app: XCUIApplication, x: CGFloat, y: CGFloat) {
    let window = app.windows.firstMatch
    let size = window.frame.size
    window.coordinate(
        withNormalizedOffset: CGVector(dx: x / size.width, dy: y / size.height)
    ).tap()
}
```

截图应作为 `.keepAlways` attachment 保存，再从 `.xcresult` 导出：

```swift
private func attachScreenshot(_ app: XCUIApplication, name: String) {
    let attachment = XCTAttachment(screenshot: app.screenshot())
    attachment.name = name
    attachment.lifetime = .keepAlways
    add(attachment)
}
```

## 构建和执行 XCUITest

先确保被测 App 已安装。XCTest runner 不负责安装 Kkindle 本体。

```bash
UITEST_DIR="/tmp/kkindle-uitest"
DEVICE_ID="<DEVICE_ID>"

xcodebuild build-for-testing \
  -project "$UITEST_DIR/KkindleUITests.xcodeproj" \
  -scheme KkindleUITests \
  -configuration Debug \
  -destination "id=$DEVICE_ID" \
  -derivedDataPath "$UITEST_DIR/DerivedData" \
  -allowProvisioningUpdates \
  -quiet
```

`.xctestrun` 文件名会随 Xcode/iOS SDK 变化，不要硬编码：

```bash
XCTESTRUN="$(
  find "$UITEST_DIR/DerivedData/Build/Products" \
    -maxdepth 1 -name '*.xctestrun' -print -quit
)"
```

执行指定用例：

```bash
RESULT_BUNDLE="$UITEST_DIR/TestResults-$(date +%Y%m%d-%H%M%S).xcresult"

xcodebuild test-without-building \
  -xctestrun "$XCTESTRUN" \
  -destination "id=$DEVICE_ID" \
  -resultBundlePath "$RESULT_BUNDLE" \
  -allowProvisioningUpdates \
  -only-testing:KkindleUITests/KkindleUITests/testLibraryImportTap \
  -only-testing:KkindleUITests/KkindleUITests/testLibrarySearchTap \
  -only-testing:KkindleUITests/KkindleUITests/testCollectionsAndCreateTap \
  -only-testing:KkindleUITests/KkindleUITests/testCollectionKeyboardRefocus
```

导出截图：

```bash
ATTACH_DIR="$UITEST_DIR/attachments-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$ATTACH_DIR"

xcrun xcresulttool export attachments \
  --path "$RESULT_BUNDLE" \
  --output-path "$ATTACH_DIR"
```

每次都使用新的 `RESULT_BUNDLE` 路径；`xcodebuild` 不会覆盖已有 `.xcresult`。

## 最终包稳定用例

以下四个用例只使用已安装 App 的公开界面，不依赖诊断环境变量，适合在清理后的
最终包上执行：

| 用例 | 验证内容 |
| --- | --- |
| `testLibraryImportTap` | 书库 `+` 打开系统文件选择器，App 保持前台 |
| `testLibrarySearchTap` | 搜索框和键盘出现，App 保持前台 |
| `testCollectionsAndCreateTap` | 切换收藏夹、点 `+`、打开新建收藏夹弹窗 |
| `testCollectionKeyboardRefocus` | 键盘弹出、收起，再点输入框能重新弹出 |

2026-11-06 的最终干净包验证结果为 `4/4` 通过：

```text
Executed 4 tests, with 0 failures
** TEST EXECUTE SUCCEEDED **
```

## 阅读器深测：临时诊断钩子

目录、图片页和 PDF 的深测曾通过临时环境变量和诊断方法完成。这些钩子已经从
生产代码清理，不应对最终包宣称仍然可用：

```text
KKINDLE_UI_SCENARIO
KKINDLE_UI_BOOK
KKINDLE_UI_TOC
KKINDLE_UI_CHAPTER
KKINDLE_UI_IMAGE
KKINDLE-TEST 日志
```

当时使用的场景包括：

- `toc-open`：打开指定书籍并停在目录。
- `images`：打开指定章节及图片页。
- `typography`：打开指定 EPUB/PDF 的排版/显示设置。

如果下次需要重新做这类测试：

1. 只在临时 Debug 构建加回 `MobileShell`/`ReaderPage` 的诊断入口。
2. 诊断入口通过环境变量触发，不能进入正常用户路径。
3. 测试完成后删除诊断入口和 `KKINDLE-TEST` 日志。
4. 用下面的命令确认清理干净，再重新构建最终包：

```bash
rg -n "KKINDLE_UI_|KKINDLE-TEST|Diagnostic|OpenCompletion" \
  src/Kkindle.Mobile src/Kkindle.Reader src/Kkindle.Mobile.iOS
```

本次真机验证覆盖过：

- 目录自动滚到当前章节。
- 目录上滑后用物理点击跨章节跳转。
- 含图 EPUB 的图片页渲染。
- 两本 PDF 打开并进入显示设置。
- 排版设置 Liquid Glass 分段控件。

## 最终清理和交付顺序

1. 删除所有临时诊断入口和日志。
2. 运行 `git diff --check`。
3. 重新构建 `ios-arm64` 真机包。
4. 使用 `devicectl` 安装覆盖旧版本。
5. 执行最终包稳定 XCUITest 四件套。
6. 测试结束后执行普通启动，让 App 留在手机上：

```bash
xcrun devicectl device process launch \
  --terminate-existing \
  --device "$DEVICE_ID" \
  "$BUNDLE_ID"
```

不要在交付时保留旧的诊断构建，也不要用 XCUITest 的 `tearDown` 状态当作应用已
启动；tearDown 默认会终止 App。

## 常见问题

| 现象 | 处理 |
| --- | --- |
| `No devices found` | 解锁手机，确认已信任电脑，再运行 `xcrun devicectl list devices` |
| 安装时报设备锁定 | 解锁 iPhone 后重试 |
| `No signing certificate` | 在 Xcode Settings → Accounts 登录开发者账号，重新检查签名身份 |
| 找不到 `*.xctestrun` | 先成功执行 `build-for-testing`，再用 `find` 查找，不要写死 SDK 版本 |
| 键盘断言失败 | 使用 `app.otherElements["inputView"]`，不要用 `app.keyboards.count` |
| 弹窗/按钮点击无效 | 先导出当前截图，确认坐标；设备尺寸或布局变化后坐标会偏移 |
| 测试后 App 不在前台 | XCUITest tearDown 终止了 App，重新用 `devicectl process launch` 启动 |
| 结果包无法覆盖 | 每次使用带时间戳的新 `.xcresult` 路径 |

# macOS 签名与 Apple 公证

Kkindle 的 macOS 发布流程已经包含 Developer ID 签名、公证（notarization）和
stapling。需要准备的只有 Apple Developer 账号和一组 CI secrets。

## 1. 前提

- 加入 [Apple Developer Program](https://developer.apple.com/programs/)（个人或公司账号）。
- 在开发者账号的 **Certificates, Identifiers & Profiles** 中确认 App ID
  `io.github.kingstacker.kkindle`（或你自定义的 Bundle ID）已存在。
- 公证需要能联网访问 Apple 服务，CI runner 必须能访问
  `appstoreconnect.apple.com` 和 `notarytool` 端点。

## 2. 创建 Developer ID Application 证书

在 Mac 上：

1. 打开“钥匙串访问” → 证书助理 → 从证书颁发机构请求证书，生成 CSR。
2. 登录 [developer.apple.com](https://developer.apple.com/account/resources/certificates/list)，
   新建 **Developer ID Application** 证书并上传 CSR。
3. 下载 `.cer`，双击导入钥匙串，确认证书和私钥都在“登录”钥匙串中。
4. 在钥匙串中右键该证书 → 导出，格式选 `.p12`，设置一个密码。

检查证书名称：

```sh
security find-identity -v -p codesigning
# 应看到 "Developer ID Application: Your Name (TEAMID)"
```

## 3. 创建公证凭据（推荐 App Store Connect API Key）

1. 登录 [App Store Connect](https://appstoreconnect.apple.com) →
   **Users and Access** → **Integrations**（部分账号在 **Keys** 页）。
2. 创建 Team Key，权限至少为 **Developer**。
3. 记下 **Key ID** 和 **Issuer ID**，下载 `AuthKey_<KEYID>.p8`。
   `.p8` 只能下载一次。

也可以使用 Apple ID + App 专用密码 + Team ID，通过
`xcrun notarytool store-credentials` 在本机保存一个 profile；CI 更推荐 API Key。

## 4. 配置 GitHub Actions secrets

在仓库 **Settings → Secrets and variables → Actions** 添加：

| Secret | 内容 |
| --- | --- |
| `APPLE_CERTIFICATE_P12_BASE64` | `.p12` 文件的 Base64 |
| `APPLE_CERTIFICATE_PASSWORD` | 导出 `.p12` 时设置的密码 |
| `APPLE_NOTARY_KEY_BASE64` | `AuthKey_<KEYID>.p8` 的 Base64 |
| `APPLE_NOTARY_KEY_ID` | API Key 的 Key ID |
| `APPLE_NOTARY_ISSUER` | App Store Connect 的 Issuer ID |
| `APPLE_SIGNING_IDENTITY` | 可选，例如 `Developer ID Application: Your Name (TEAMID)`；不填则自动从证书中查找 |
| `APPLE_KEYCHAIN_PASSWORD` | 可选，临时 keychain 密码；不填则自动生成 |

在 macOS 上生成 Base64：

```sh
base64 -i certificate.p12 | gh secret set APPLE_CERTIFICATE_P12_BASE64
base64 -i AuthKey_XXXXXXXXXX.p8 | gh secret set APPLE_NOTARY_KEY_BASE64
gh secret set APPLE_NOTARY_KEY_ID
gh secret set APPLE_NOTARY_ISSUER
gh secret set APPLE_CERTIFICATE_PASSWORD
```

## 5. 本地手动验证

本机也要先有 Developer ID 证书和公证 profile：

```sh
xcrun notarytool store-credentials "kkindle-notary" \
  --apple-id "you@example.com" \
  --team-id "TEAMID" \
  --password "app-specific-password"

APPLE_SIGNING_IDENTITY="Developer ID Application: Your Name (TEAMID)" \
APPLE_NOTARY_PROFILE=kkindle-notary \
  bash scripts/build-macos-release.sh 1.1.3 artifacts/macos osx-arm64
```

脚本会：

1. 用 hardened runtime + entitlements 签名所有 Mach-O；
2. 生成并签名 `.app`；
3. 公证并 staple `.app`；
4. 生成带 `Applications` 符号链接的 DMG；
5. 签名、公证并 staple DMG；
6. 对 app 执行 `codesign --verify --deep --strict`。

## 6. 验证结果

```sh
spctl -a -vvv -t exec /Applications/Kkindle.app
# accepted
# source=Notarized Developer ID

spctl -a -vvv -t open --context context:primary-signature Kkindle-1.1.3-osx-arm64.dmg
xcrun stapler validate Kkindle-1.1.3-osx-arm64.dmg
```

在未安装过 Kkindle 的干净 Mac 上首次双击 DMG 并拖入“应用程序”，应该不再出现
“无法验证开发者”的提示。

## 7. 常见失败原因

- **The signature of the binary is invalid**：有嵌套 Mach-O 没有签名，或签名后又修改了文件。
- **The executable does not have the hardened runtime enabled**：漏了
  `--options runtime`。
- **The executable requests the com.apple.security.get-task-allow entitlement**：
  使用 Debug 构建或带调试 entitlement 的证书发布；Release 构建不应包含它。
- **Invalid Bundle Identifier**：`CFBundleIdentifier` 与证书/描述文件不匹配。
- **Package Invalid** 但日志没有细节：用
  `xcrun notarytool log <submission-id> --key ...` 下载具体日志。
- CI 中 `security import` 后 codesign 找不到钥匙：检查
  `security set-key-partition-list` 是否执行，以及 `APPLE_KEYCHAIN_PASSWORD` 是否一致。

Ad-hoc 构建（未配置上述 secrets）不会公证，仍需要
[macos-adhoc-install.md](macos-adhoc-install.md) 中的手动放行步骤。

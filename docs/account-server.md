# Kkindle 分区账号与同步服务

桌面端左侧的“登录 / 注册”和移动端“我的 → 登录 / 注册”支持选择**中国区 / 国际区**。
每个区域分别记住服务地址和登录会话。两区的账号数据库、会话和同步文件独立；相同用户名
可以在两区分别注册，另一地区的令牌无法登录或读取文件。

注册成功后直接登录。用户名为 3–32 位字母、数字、点、下划线或连字符，不区分大小写；
密码为 8–128 位，区分大小写。当前不提供邮箱验证、密码找回和第三方登录。

## 独立服务器项目与本机验证

用户管理服务位于桌面的独立项目 `~/Desktop/kkindle_server`，使用 ASP.NET Core / .NET 10、
PostgreSQL 和中文管理网页，不依赖客户端源码路径。操作与部署资料在该项目的 README 中。

```sh
cd ~/Desktop/kkindle_server
python3 scripts/local.py start
python3 scripts/local.py test
```

本地脚本使用专属的回环 PostgreSQL 集群，为两区分别创建数据库、数据库账号、文件目录与
管理员会话。初始管理员为 `local-admin`，两区的不同随机密码在被忽略且仅本机用户可读的
`.local/local-config.json` 中；不会写进源码或打印到日志。

| 区域 | 客户端服务地址 | 管理网页 | 账号数据库 | 加密文件 |
| --- | --- | --- | --- | --- |
| 中国区 | `http://127.0.0.1:5088` | `http://127.0.0.1:5088/admin` | PostgreSQL `kkindle_china` | `.local/storage/china/sync/<account-id>/` |
| 国际区 | `http://127.0.0.1:5089` | `http://127.0.0.1:5089/admin` | PostgreSQL `kkindle_international` | `.local/storage/international/sync/<account-id>/` |

PostgreSQL 只保存账号、会话哈希、封禁、额度、流量、存储汇总和管理记录。书籍与同步快照
由客户端加密后存入独立文件目录；数据库没有书名、书籍清单、正文或解密密钥。管理网页
支持封禁/解封、撤销会话、调整额度、重置本月流量和删除任务，不提供查看或下载书籍的入口。
删除须先封禁，之后可选择只删除云数据或同时删除账号。删除账号后同名用户可以重新注册；
需要保持拉黑状态时，选择只删除云数据。离线备份按服务器的独立保留计划清理。

`GET /health` 返回服务标识 `kkindle-accounts`、区域（China 为 0，International 为 1）和
`administration: true`。数据库和文件目录首次初始化时记录所属区域，禁止跨区复用。

macOS 桌面端、移动端预览和 iOS 模拟器直接使用 `127.0.0.1`；Android 模拟器的开发机
地址为 `10.0.2.2`，端口仍分别为 5088 / 5089。真机连接可访问的 HTTPS 服务。

在登录页选择区域、切换到注册、填写测试用户名、密码和确认密码。再到同步设置选择
“账号云同步”，设置并保存同步密钥，再点击测试连接或立即同步。账号同步复用现有书库快照合并协议，包含书籍、
批注、书签和阅读记录；书籍文件默认按需下载，也可开启同步时下载。

同步密钥只在用户设备本地保存，不上传服务器，不导出到备份。用户须自行牢记；遗忘后
无法解密已有云数据或 `.kkindle` 加密备份，服务器没有密钥找回能力。完整数据备份的
导入和导出也必须使用已配置的密钥，详见[加密备份说明](backup-encryption.md)。

## 客户端区域隔离

中国区沿用已有安装根目录下的书库；国际区使用 `<安装根目录>/regions/international/`。
数据库、阅读记录、同步配置、设备 ID、同步基线和备份目录分别保存。切换区域后可以立即
注册、登录或退出该区域账号；界面提示重新打开应用，以加载对应区域的独立书库。
重新打开前，当前进程禁止同步和按需下载，包括后台同步，避免向另一地区发送现有书库。
切回原区域并重新打开后，原来的本地书库和登录状态仍保留。

“账号云同步”绑定当前登录的区域、服务地址和用户 ID；服务端仅从认证会话确定文件所有者。
更换账号、服务地址、退出或会话失效时，旧的同步配置不能继续使用。登录后重新选择并保存
当前账号的同步设置即可。登录本身不会自动启用云同步。

应用中的 S3 / WebDAV 同样使用区域命名空间：`regions/china/<目录前缀>/` 和
`regions/international/<目录前缀>/`。即使填写相同 Bucket 或 WebDAV 地址，两个区域也不会
合并数据。已有未分区的远端 `<目录前缀>/` 数据不会自动迁入，新版首次同步使用分区目录；
原有本地书库会上传到当前区域。每个区域独立保存连接凭据。连接参数文件仍只支持 S3 / WebDAV；
账号会话不能导出。普通备份不包含账号会话，恢复备份不会自动启用账号云同步。

## 本地自动化验证

独立服务的集成测试使用真实 PostgreSQL 临时数据库与 HTTP，覆盖管理操作、区域隔离、
流量、CSRF、封禁、删除重试和并发上传期间的删除。现有 Chrome 验证脚本位于独立项目的
`scripts/browser-check.mjs`，直接使用已安装浏览器，不要求下载另一个测试浏览器。

客户端仓库保留 `src/Kkindle.AccountServer` 作为 SQLite 协议测试服务，用于验证客户端
协议和两台设备的同步兼容性；它不具备独立项目的用户后台、额度与加密上传限制，不应
作为本次服务器管理方案部署。相关测试使用临时目录和随机端口，不占用 5088 / 5089：

```sh
dotnet test tests/Kkindle.AccountServer.Tests/Kkindle.AccountServer.Tests.csproj -c Debug
dotnet test tests/Kkindle.Mobile.Tests/Kkindle.Mobile.Tests.csproj -c Debug
dotnet test tests/Kkindle.Tests/Kkindle.Tests.csproj -c Debug
```

覆盖注册、错误密码、重复用户名、会话恢复/撤销/过期、限流、两个区域的账号和对象隔离、
不同账号的对象隔离、区域切换后的同步阻断，以及每个区域内两台客户端的实际 EPUB、批注
和可累加阅读时长同步。共享测试还验证外部 S3 存储的区域命名空间和阅读周期边界。

iOS 原生界面另有完整的分区账号测试。安装隔离预览应用并启动上述两个本地服务后运行：

```sh
python3 tests/Kkindle.Mobile.iOS.UITests/run-tests.py --simulator <模拟器ID> --results <新的结果目录> --only testRegionalAccountRegistrationAndCloudSync
```

该场景可连接上述两个 PostgreSQL 本地服务，验证两区分别注册、退出、错误密码提示、重新登录和加密云同步，以及切区重开后的
书库隔离与会话恢复。测试使用虚构密码，并处理 iOS 系统的强密码建议弹层。
由于依赖两个服务，默认的原生界面测试会跳过此场景，需要显式选择。

## 服务器配置

独立服务器需要 .NET 10 ASP.NET Core Runtime 和 PostgreSQL。正式环境将两区服务运行在各自区域的主机上，
使用独立的数据卷、备份和 HTTPS 域名。网关按域名固定代理对应区域，不配置跨区回退、
账号复制或同步数据复制。客户端分别填写两个区域对应的 HTTPS 地址。

代理须转发 `/api/accounts/`、`/api/sync/` 和 `/health`。Nginx 示例（中国区服务）：

```nginx
location / {
    client_max_body_size 512m;
    proxy_pass http://127.0.0.1:5088;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $remote_addr;
    proxy_set_header X-Forwarded-Proto $scheme;
}
```

将此段放入已配置 TLS 的 `server` 中；国际区代理到其独立服务。代理在其他主机上时，用
`Server__KnownProxies__0` 指定实际可信代理 IP，并使用 `ConnectionStrings__Accounts` 环境变量
提供此区域的 PostgreSQL 连接信息。运行进程使用专用用户，数据目录仅允许
该用户读写。服务拒绝远端明文 HTTP；客户端仅允许回环地址及 Android 开发机别名使用 HTTP。
登录和同步均拒绝 HTTP 重定向，账号同步始终校验 TLS。

## API 与会话

所有账号和同步请求必须携带 `X-Kkindle-Region: china` 或 `international`，并与服务的固定
区域相同，否则返回 403。认证 JSON 中的 `region` 同样必须匹配。区域标签由服务器配置
决定；请求头用于防止误连，不能改变服务器区域。

| 方法 | 路径 | 行为 |
| --- | --- | --- |
| POST | `/api/accounts/register` | JSON `region`、`username`、`password`；创建账号并返回会话 |
| POST | `/api/accounts/login` | 同一 JSON；验证后返回会话 |
| GET | `/api/accounts/session` | `Authorization: Bearer <SESSION_TOKEN>`；验证当前会话 |
| POST | `/api/accounts/logout` | 同一认证头；撤销当前会话，返回 204 |
| GET | `/api/sync/objects?prefix=<PREFIX>` | 列出当前账号的对象键和修改时间 |
| GET / HEAD | `/api/sync/objects/<KEY>` | 读取当前账号的对象；支持范围下载 |
| PUT | `/api/sync/objects/<KEY>` | 仅接受客户端加密封装，流式上传后原子替换，最多 512 MiB，受流量/存储额度限制 |
| DELETE | `/api/sync/objects/<KEY>` | 删除当前账号的对象 |

注册/登录返回 `region`、`user: { id, username }`、`token`、`expiresAt`。
错误密码和不存在的账号统一返回 401；重复注册返回 409；同一来源每分钟最多 10 次
注册/登录，超过后返回 429。认证请求体最多 16 KiB。

密码使用随机盐和 PBKDF2-SHA256（600,000 次迭代），数据库不保存原始密码。会话使用
32 字节随机令牌，有效期 30 天；服务器只保存令牌的 SHA-256 哈希。账号和会话在服务重启
后保留。退出仅撤销当前设备、当前区域的会话，不影响其他设备或另一地区账号。
客户端通过平台 `ISecretProtector` 独立加密两区会话，不保存账号密码；会话不进入任何云同步
或 `.kkindle` 备份。服务器不可用时仍可在本机退出，界面提示远端撤销尚未完成。

## 阅读周期

阅读数据按钮为“周 / 月 / 年”，默认选中周。本周从周一开始；本月按实际天数显示；
今年按 12 个月汇总，包含闰年的 2 月 29 日。未来日期显示为空，不计入时长。统计沿用现有
阅读记录的 UTC 日期；切换周期不改变累计时长、近七天日均、最近阅读和勋章数据。

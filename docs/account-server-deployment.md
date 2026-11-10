# 账号同步：本机验证与正式部署配置

本文只列配置入口，不执行公网部署，也不提供任何真实凭据。两区数据库、对象存储、账号、
会话和书库必须独立；不能用切换域名的方式把中国区回退到国际区。

## 1. 客户端：只改一处两区默认地址

所有客户端前端共用 `src/Kkindle.Core/AccountServerEndpoints.cs`：

| 字段 | 当前本机默认值 | 正式部署时修改为 |
| --- | --- | --- |
| `ChinaDefaultServerUrl` | `http://127.0.0.1:5088` | 中国区账号服务的 HTTPS 基址 |
| `InternationalDefaultServerUrl` | `http://127.0.0.1:5089` | 国际区账号服务的 HTTPS 基址 |

修改这两个常量后重新构建要分发的客户端即可；不需要在桌面、共享移动端和原生 iOS 前端
分别搜索替换地址。它们是直接编译的 C# 值，不依赖程序集反射或运行时读取外部配置文件。
示例仅为不可访问的虚构域名：

```csharp
public const string ChinaDefaultServerUrl = "https://accounts-cn.example.invalid";
public const string InternationalDefaultServerUrl = "https://accounts-intl.example.invalid";
```

`LocalDevelopmentServerUrl` 是客户端仓库里 SQLite **协议测试服务**的固定监听默认值，
不要跟随正式域名修改。正式 PostgreSQL 服务在独立的 `~/Desktop/Kkindle_server` 项目中。

### 不重建的运行覆盖

进程环境变量优先于编译默认值，分别覆盖对应区域：

- `KKINDLE_ACCOUNT_SERVER_CHINA`
- `KKINDLE_ACCOUNT_SERVER_INTERNATIONAL`

例如在 macOS 终端启动本机 Debug 客户端：

```sh
env KKINDLE_ACCOUNT_SERVER_CHINA=http://127.0.0.1:5088 \
    KKINDLE_ACCOUNT_SERVER_INTERNATIONAL=http://127.0.0.1:5089 \
    ./src/Kkindle.Desktop.MacOS/bin/Debug/net10.0/Kkindle
```

Finder 启动的应用通常不会继承终端变量；iOS/Android 分发版应修改编译默认值，开发运行
也可在其启动环境里设置变量。这里没有单独的构建属性或开关，不会加入生产授权绕过。

地址只允许 HTTP/HTTPS，不允许嵌入用户名、密码、查询参数或片段。公网必须 HTTPS；
明文 HTTP 仅限回环开发地址和 Android 开发机别名 `10.0.2.2`。变量**未设置**才使用编译
默认值；设置为空或设置非法地址会明确报配置错误，不会静默回退或打印错误值。

### 已安装客户端如何更新地址

本机 `data/account-session.json` 的每区连接可包含非敏感标记 `usesDefaultServerUrl`：

- 未登录且使用默认地址：跟随本次启动的对应区域默认值；退出登录后的下一次状态读取也会更新。
- 已保存的自定义地址：继续使用该地址，不被部署默认值覆盖。
- 已登录会话：始终绑定保存的原服务器，验证会话、退出登录和官方云请求都不会把旧令牌
  发到新域名。先退出旧服务，再连接新服务；新默认值不能迁移账号数据库或云端内容。
- 旧记录没有标记时，只把该区域已知的本机开发默认地址视为默认；自定义 HTTPS 地址、
  非默认端口或带基路径的连接保留。显式 `false` 的本机连接也保留。

标记与账号会话一样只留在设备上，不进入便携备份或云同步。即使新默认配置非法，本机
退出登录仍先清除令牌，再在后续状态读取时报告配置错误。

官方云对象名只依赖区域、账号 ID 和用户同步密钥，不依赖服务器 URL。**同区且保留
原账号 ID、对象索引和存储数据的域名迁移**不会改变对象名；新建账号或丢失数据库不是
等价迁移。同步密钥仍由用户自行保管，服务端不接收、不保管、不提供找回。

## 2. 独立服务端：部署字段

Docker Compose 的入口是服务端项目的 `.env.example` 和 `compose.yaml`。将示例复制到
被忽略的 `.env` 后由部署环境填入真实值，不把 `.env`、连接串或存储凭据提交到仓库。
`CHINA_` 与 `INTERNATIONAL_` 字段必须分别填写；域名字段仅填主机名，不含 `https://`。

| Compose `.env` 字段 | 对应作用 |
| --- | --- |
| `CHINA_HOST` / `INTERNATIONAL_HOST` | 各区 HTTPS 网关域名，同时用于 `AllowedHosts`；须与客户端基址对应 |
| `CHINA_DB_PASSWORD` / `INTERNATIONAL_DB_PASSWORD` | 各自 PostgreSQL 账号密码；不得共用数据库或凭据 |
| `CHINA_STORAGE_PROVIDER` / `INTERNATIONAL_STORAGE_PROVIDER` | `Local` 或 `S3`；默认 `Local` |
| `*_STORAGE_SERVICE_URL` | S3 兼容的 HTTPS API 端点，不是公共下载地址 |
| `*_STORAGE_REGION` | 对象存储签名区域 |
| `*_STORAGE_BUCKET` | 该区私有桶名 |
| `*_STORAGE_ACCESS_KEY` / `*_STORAGE_SECRET_KEY` | 仅具有该区私有桶所需权限的凭据 |
| `*_REGISTRATIONS_PER_IP_PER_DAY` / `*_REGISTRATIONS_PER_IP_PER_30_DAYS` | 各区注册限制，默认 3 / 10 |

不使用 Compose 时，以下是 .NET 的实际环境变量名称；双下划线表示配置层级：

| 服务进程环境变量 | 说明 |
| --- | --- |
| `Server__Region` | `China` 或 `International`，与数据库、数据目录及网关固定绑定 |
| `ConnectionStrings__Accounts` | 当前区域 PostgreSQL 连接串，由私有部署环境提供 |
| `Server__DataDirectory` | 当前区域独立、持久化的数据根目录 |
| `ASPNETCORE_URLS` | 内部 HTTP 监听地址；对外由 HTTPS 网关代理，不等同于客户端基址 |
| `AllowedHosts` | 当前区域允许的公开主机名，不使用任意主机通配符代替正式配置 |
| `Server__KnownProxies__0` | 确实可信的反向代理 IP；更多地址依次使用 `__1` 等 |
| `Storage__Provider` | `Local` 或 `S3` |
| `Storage__ServiceUrl` / `Storage__SigningRegion` / `Storage__Bucket` | S3 端点、签名区域和私有桶 |
| `Storage__AccessKey` / `Storage__SecretKey` | 对象存储凭据，绝不能写入客户端 |
| `Storage__Prefix` | 桶内前缀，默认 `kkindle/`，非空时须以 `/` 结尾 |
| `Storage__ForcePathStyle` | 是否使用路径式寻址，按存储供应商要求设置，默认 `false` |
| `Storage__UrlLifetimeMinutes` | 签名链接有效期，默认 15，范围 1–60；还会受 Plus 到期时间限制 |
| `Billing__Enabled` | 生产购买默认关闭；真实支付网关尚未接入，不应仅靠开启此项就声称支持真实支付 |
| `Server__LocalDevelopment` | 只供 `Development` 环境下的回环本机验证；生产不得启用 |

Compose 的可信代理 IP 与其独立网络绑定；换网关或网络后应同步调整，不能信任用户
随意传来的 `X-Forwarded-For` / `X-Forwarded-Proto`。TLS、数据库备份及对象存储权限仍按
服务端 README 执行。`Local` 模式须代理 `/sync-blobs/` 并容纳加密书籍上传；`S3` 模式
书籍字节在客户端与私有桶之间直传，账号服务只处理授权、额度和索引。

## 3. 上线前验证范围

本机测试覆盖：真实 PostgreSQL 两区服务、真实 Plus 权限、签名传输、两设备双向同步、
按需下载、书籍和封面字节比对、不透明 HMAC 对象名与 `KKINDLE-SYNC3` 长度补齐。
运行方法见[账号服务验证](account-server.md#真实-postgresql-服务端与客户端验证)。

上线前仍须在目标服务和真实私有桶验证 TLS、签名上传/下载、范围读取、额度、代理配置
和备份恢复。当前本机端到端验证使用 `Local` 对象后端，不能代替对真实 S3 桶的验证。
同步协议最低读取版本为 6，参与同步的客户端应一起升级。旧客户端遇到新快照会停止写入；
新客户端保留旧快照及旧对象的只读兼容，不会清空旧云数据，也不能追溯隐藏旧名字已暴露的元数据。

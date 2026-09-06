# Kavita + Casdoor 一体化部署文档

## 阅读指南

**本文的可靠度**：Kavita 侧的全部行为描述均对照源码核实，主要依据 `Kavita.Services/OidcService.cs`、`Kavita.Server/Extensions/IdentityServiceExtensions.cs`、`Kavita.Server/Helpers/OpenIdConnectEventsHelper.cs`、`Kavita.Server/Middleware/AuthKeyAuthenticationHandler.cs`、`Kavita.Models/Constants/PolicyConstants.cs`。网上流传的说法与实现不符之处，本文均以源码为准并标注。

**占位符**：`casdoor.yourdomain.com`、`kavita.yourdomain.com` 需全局替换为实际域名。

**建议阅读顺序**：

| 你的状态 | 建议 |
|---|---|
| 首次部署 | 按顺序通读 1 → 8，第 2 章务必读 |
| 只想改权限模型 | 直接看 1.2，再跳到 5.2 或 5.3 |
| 部署完出问题 | 直接看第 9 章 |
| 日常查参数 | 附录 A、附录 B |

## 目录

- [1. 方案概览](#1-方案概览)
  - [1.1 架构与登录链路](#11-架构与登录链路)
  - [1.2 授权模式选型（先决策，再动手）](#12-授权模式选型先决策再动手)
  - [1.3 前置条件](#13-前置条件)
- [2. 核心机制](#2-核心机制)
  - [2.1 十件必须先搞清楚的事](#21-十件必须先搞清楚的事)
  - [2.2 用户识别：sub 优先、email 兜底](#22-用户识别sub-优先email-兜底)
  - [2.3 Scope 与 Claim 的索取](#23-scope-与-claim-的索取)
  - [2.4 认证 Cookie 与自动跳转](#24-认证-cookie-与自动跳转)
  - [2.5 API Key 与角色缓存](#25-api-key-与角色缓存)
- [3. 部署 Casdoor](#3-部署-casdoor)
- [4. Casdoor 平台配置](#4-casdoor-平台配置)
- [5. Kavita OIDC 配置](#5-kavita-oidc-配置)
- [6. Nginx 反向代理](#6-nginx-反向代理)
- [7. 验证与验收](#7-验证与验收)
- [8. 运维：备份、回退、升级](#8-运维备份回退升级)
- [9. 故障排查](#9-故障排查)
- [10. 风险与约束](#10-风险与约束)
- [附录 A：配置参数速查表](#附录-a配置参数速查表)
- [附录 B：角色/库/年龄分级映射速查](#附录-b角色库年龄分级映射速查)

---

## 1. 方案概览

### 1.1 架构与登录链路

```
浏览器访问 Kavita
   → 未登录：Kavita 前端根据 Auto Login 跳转 /oidc/login
   → 302 Casdoor 授权页
   → Casdoor 认证（密码 / QQ / B 站，均需先绑定）
   → OIDC 回调 https://kavita.yourdomain.com/signin-oidc
   → Kavita 用 sub 匹配 OidcId：命中则复用；未命中按 email 匹配；都不命中则 JIT 创建
   → 签发 .AspNetCore.Cookies，进入 Kavita
```

达成的目标：

1. Kavita 业务账号由 Casdoor 托管，默认跳转 Casdoor 登录；保留本地 admin 用于应急。
2. QQ、B 站等社交账号**只能绑定到已有账号**，禁止扫码直接注册。
3. 同一 Casdoor 账号，无论密码、QQ 还是 B 站登录，进入 Kavita 都是**同一个用户**，阅读进度与收藏共用。
4. 权限可由 Casdoor 用户组下发，或留在 Kavita 手工管理（见 1.2）。
5. Casdoor 数据持久化，容器重建后配置与用户数据不丢。

### 1.2 授权模式选型（先决策，再动手）

这是全流程中**唯一需要你提前做的设计决策**，它决定了后续 Casdoor 是否建 `kavita-*` 组、Kavita 哪些参数要配。请在此二选一，再进入第 3 章。

| | **同步模式** | **仅认证模式** |
|---|---|---|
| Casdoor 职责 | 认证 + 授权 | **仅认证** |
| 权限来源 | Casdoor 用户组 | Kavita `设置 → 用户` 手工维护 |
| Kavita 开关 | `Sync user settings` **开启** | `Sync user settings` **关闭** |
| 需要 `kavita-*` 组 | 是 | 否 |
| 需要 `groups` 声明 | 是 | 否（配了也被忽略） |
| 每次登录是否重写权限 | 是（覆盖式） | 否（手工设置是稳态） |
| 改邮箱影响 | 会被同步，有冲突风险（见 10.1） | 无影响 |
| 适合场景 | 用户多、需按组批量管控 | 用户少、权限变动不频繁 |
| 配置入口 | [5.2](#52-同步模式专有配置) | [5.3](#53-仅认证模式专有配置) |

> 若拿不定主意，**用户少于 20 人且权限不常变，选仅认证模式**，运维负担明显更低，且规避了同步模式的几个坑（10.1、10.4）。后续想切换只需改开关，Casdoor 侧的组配置留着也不会碍事。

### 1.3 前置条件

- 两个域名已完成 ICP 备案，全程 HTTPS（OIDC 回调与 OAuth 在 HTTP 下均会报错）。
- 两个域名**建议使用同一父域**（原因见 2.4）。
- QQ 互联、B 站开发者平台已创建应用，取得 Client ID / Secret。
- 已在 Kavita `设置 → 服务器` 确认 Base URL 与反向代理一致（默认 `/`，子路径部署填如 `/kavita/`）。
- OPDS 阅读器不支持 OIDC，用户需在网页端生成 API Key（见 2.5）。

---

## 2. 核心机制

### 2.1 十件必须先搞清楚的事

以下每一条都对应一个真实的部署失败场景，且与网上常见说法相左：

| 项目 | Kavita 实际行为 | 常见错误说法 |
|---|---|---|
| 认证 Cookie 名称 | `.AspNetCore.Cookies` | 不是 `kavita-auth`，Nginx 用错会导致"登录后反复跳转" |
| 用户匹配键 | **优先 `sub` → `AppUser.OidcId`**，email 仅兜底 | "Kavita 只用 email 匹配" —— 错误 |
| 改邮箱的后果 | **不会**新建账号（`sub` 不变）；同步模式下同步新邮箱，被占用则报 `email-in-use` 且登录失败 | "改邮箱必然分裂账号" —— 不准确 |
| 真正会分裂账号的操作 | 删除并重建 Casdoor 用户（`sub` 变化）、或"清除外部 ID"后未清理干净 | — |
| 默认请求的 Scope | `openid profile offline_access roles email` | 漏掉 `offline_access` 将无法刷新令牌、无法后台同步 |
| Scope 过滤规则 | 默认 Scope 按 IdP 的 `scopes_supported` 自动过滤；**自定义 Scope 无条件追加，不过滤** | — |
| 同步模式的新用户门槛 | 必须携带 `Login` 或 `Admin`，否则拒绝创建 | 文档普遍未提，是"能登录但报错"的头号原因 |
| Authority 要求 | 非 Development 环境必须以 `https` 开头，否则 OIDC 被整体禁用 | — |
| Cookie 的 SameSite | `SameSite=Strict`、`Secure=SameAsRequest`、7 天滑动续期 | 跨不同父域时跨站跳转会丢登录态 |
| 自动跳转 Casdoor | Kavita **内置** Auto Login 开关 | Nginx `if ($http_cookie ...)` hack 是可选方案，非必需 |

### 2.2 用户识别：sub 优先、email 兜底

Kavita 回调后的查找顺序：

```
1. sub (NameIdentifier) → AppUser.OidcId      ← 主键
2. 未命中 → email 匹配一次                      ← 仅首次兜底
3. 都未命中 → JIT 创建（受 Provision Accounts 控制）
```

推论：

- 改 Casdoor 邮箱**不会**产生新 Kavita 账号，因为 `sub` 没变。
- 删除并重建 Casdoor 用户会**产生新账号**，因为 `sub` 变了；旧账号的进度与收藏留在原处。
- 执行 `Reset external IDs`（`ClearOidcIds`）后，所有用户下次登录都走 email 兜底；此时若邮箱与他人冲突会报 `Email already in use`。

### 2.3 Scope 与 Claim 的索取

```
实际请求的 Scope = 默认 Scope ∩ IdP 的 scopes_supported  +  自定义 Scope（无条件追加）
```

- 默认 Scope：`openid profile offline_access roles email`。
- `offline_access` 决定能否拿到 refresh token —— Kavita 依赖它在后台续期并同步用户设置，**不能省**。
- 若 IdP 的 `scopes_supported` 不含 `roles`，该项被自动丢弃并打 Warning，属正常现象。
- 自定义 Scope（本文是 `groups`）不参与过滤，会原样追加到请求中。

Kavita 侧 `MapInboundClaims = true`，依赖**标准 claim 名**（`sub`、`email`、`email_verified`），因此 Casdoor 应用的 Token format 必须设为 `JWT-Standard`。

### 2.4 认证 Cookie 与自动跳转

| 属性 | 值 |
|---|---|
| 名称 | `.AspNetCore.Cookies` |
| SameSite | `Strict` |
| Secure | `SameAsRequest` |
| 有效期 | 7 天，滑动续期 |

`SameSite=Strict` 意味着**跨站请求不会携带 Cookie**。若 Casdoor 与 Kavita 不在同一父域，从 Casdoor 跳回 Kavita 会被视为跨站，登录态可能丢失 —— 这是建议同父域部署的原因。

自动跳转有两种做法，推荐用 Kavita 内置的 `Auto login`（详见 6.1）。

### 2.5 API Key 与角色缓存

> 常见误解：角色变化会让 API Key 失效。**不会。**

角色变化时 Kavita 会调用 `InvalidateAllForUserAsync`，但它只清缓存：

```
SyncRoles → UpdateRolesForUser 返回 hasMadeChanges（角色集合有差异才为 true）
         → InvalidateAllForUserAsync
         → cache.RemoveAsync("authKey_<key>")   ← 仅此一步，不碰数据库
```

清缓存的原因：`AuthKeyAuthenticationHandler` 会把 `GetUserDtoByAuthKeyAsync` 的结果（**含用户角色**）缓存 **15 分钟**，并用其中的角色生成授权 claims。角色变了却不清缓存，已收回的权限最长还能继续用 15 分钟。清缓存是为了让权限**立即生效**。

实际效果：

| 项目 | 结果 |
|---|---|
| Key 字符串 | **不变** |
| 数据库中的 Key 记录 | **不变** |
| 阅读器是否需要重新配置 | **不需要** |
| 下一个请求 | 回源查库、按新角色重建缓存 |

真正会让阅读器"用不了"的是另外两种情况：

1. **Key 自身过期** —— 创建 Key 时可设过期时间。系统自动生成的 OPDS / ImageOnly 两个 Key `ExpiresAtUtc` 为 `null`，永不过期；手动创建的给阅读器长期使用时**留空过期时间**。
2. **权限被收缩** —— 同步模式下库权限是覆盖式的（见 5.2.3），未列出的库会被移除访问权，表现为阅读器能连上但书架空了。

---

## 3. 部署 Casdoor

### 3.1 镜像选型

使用 **`casbin/casdoor:latest`**，官方标注"适用于生产环境"，数据库采用镜像内置的 SQLite 驱动，库文件写入挂载的 `./conf/casdoor.db`。

> 另有 `casbin/casdoor-all-in-one` 镜像，虽同样内置 SQLite，但官方明确标注**不适用于生产**（内置库被称为 toy database，且数据路径未写入文档）。本文不采用，也不建议生产使用。

### 3.2 目录结构与 Compose

```
/opt/kavita-stack/
├── docker-compose.yml
├── conf/                 # Casdoor 配置 + SQLite 数据库（必须持久化）
│   └── casdoor.db
├── logs/
├── kavita-data/          # Kavita 配置与数据库
└── media/                # 漫画/书籍媒体库
```

```yaml
services:
  casdoor:
    image: casbin/casdoor:latest
    container_name: casdoor
    restart: unless-stopped
    environment:
      RUNMODE: prod
      driverName: sqlite
      dataSourceName: "file:/conf/casdoor.db"
      origin: https://casdoor.yourdomain.com
    volumes:
      - ./conf:/conf          # app.conf、casdoor.db、证书等，删除容器也不丢
      - ./logs:/logs
    networks:
      - kavita-net

  kavita:
    image: jvmilazz0/kavita:latest
    container_name: kavita
    restart: unless-stopped
    volumes:
      - ./kavita-data:/kavita/config
      - ./media:/media
    networks:
      - kavita-net

networks:
  kavita-net:
    driver: bridge
```

启动并验证落盘：

```bash
mkdir -p conf logs kavita-data media
sudo chown -R 1000:1000 ./conf ./logs     # 见下方警告
docker compose up -d
ls -l ./conf/casdoor.db                   # 应能看到文件，属主为 1000
```

> **容器内 Casdoor 进程的 uid/gid 为 1000**。绑定挂载的宿主机目录必须对 uid 1000 可写，否则出现 `permission denied`，表现为容器反复重启或无法创建数据库。

### 3.3 配置注入方式（二选一）

上面 compose 用**环境变量**注入，这是官方推荐方式（`driverName` / `dataSourceName` / `origin`）。

若发现 `RUNMODE` 等环境变量未生效（界面仍是调试模式、配置值未被采纳），或你更习惯配置文件，则改用挂载 `app.conf`。

#### 3.3.1 官方原始 `app.conf`

以下为 Casdoor 官方仓库 `conf/app.conf` 的完整内容（获取地址：<https://raw.githubusercontent.com/casdoor/casdoor/master/conf/app.conf>，可作为日后核对新版本差异的参考）：

```ini
appname = casdoor
httpport = 8000
runmode = dev
copyrequestbody = true
driverName = mysql
dataSourceName = root:123456@tcp(localhost:3306)/
dbName = casdoor
tableNamePrefix =
showSql = false
redisEndpoint =
defaultStorageProvider =
isCloudIntranet = false
authState = "casdoor"
socks5Proxy = "127.0.0.1:10808"
verificationCodeTimeout = 10
initScore = 0
logPostOnly = true
isUsernameLowered = false
origin =
originFrontend =
staticBaseUrl = "https://cdn.casbin.org"
isDemoMode = false
batchSize = 100
showGithubCorner = false
forceLanguage = ""
defaultLanguage = "en"
defaultApplication = "app-built-in"
maxItemsForFlatMenu = 7
enableErrorMask = false
enableGzip = true
inactiveTimeoutMinutes =
ldapServerPort = 389
ldapsCertId = ""
ldapsServerPort = 636
radiusServerPort = 1812
radiusDefaultOrganization = "built-in"
radiusSecret = "secret"
quota = {"organization": -1, "user": -1, "application": -1, "provider": -1}
logConfig = {"adapter":"file", "filename": "logs/casdoor.log", "maxdays":99999, "perm":"0770"}
initDataNewOnly = false
initDataFile = "./init_data.json"
frontendBaseDir = "../cc_0"
```

> 注意默认值：**`driverName = mysql`、`dataSourceName = root:123456@tcp(localhost:3306)/`**。直接挂这个文件而不改，Casdoor 会连不上数据库而反复重启 —— 容器内没有 MySQL。

#### 3.3.2 本文场景下的 `conf/app.conf`

在上面的原版基础上改 4 处即可（`⬅` 为改动标记，实际文件中不要写）：

```ini
appname = casdoor
httpport = 8000
runmode = prod                              ⬅ 原 dev
copyrequestbody = true
driverName = sqlite                         ⬅ 原 mysql
dataSourceName = file:/conf/casdoor.db      ⬅ 原 root:123456@tcp(localhost:3306)/
dbName = casdoor
tableNamePrefix =
showSql = false
redisEndpoint =
defaultStorageProvider =
isCloudIntranet = false
authState = "casdoor"
socks5Proxy = "127.0.0.1:10808"
verificationCodeTimeout = 10
initScore = 0
logPostOnly = true
isUsernameLowered = false
origin = https://casdoor.yourdomain.com     ⬅ 原为空
originFrontend =
staticBaseUrl = "https://cdn.casbin.org"
isDemoMode = false
batchSize = 100
showGithubCorner = false
forceLanguage = ""
defaultLanguage = "en"
defaultApplication = "app-built-in"
maxItemsForFlatMenu = 7
enableErrorMask = false
enableGzip = true
inactiveTimeoutMinutes =
ldapServerPort = 389
ldapsCertId = ""
ldapsServerPort = 636
radiusServerPort = 1812
radiusDefaultOrganization = "built-in"
radiusSecret = "secret"
quota = {"organization": -1, "user": -1, "application": -1, "provider": -1}
logConfig = {"adapter":"file", "filename": "logs/casdoor.log", "maxdays":99999, "perm":"0770"}
initDataNewOnly = false
initDataFile = "./init_data.json"
frontendBaseDir = "../cc_0"
```

保存到 `conf/app.conf` 后重启：

```bash
docker compose restart casdoor
```

#### 3.3.3 四项关键配置说明

| 配置项 | 说明 |
|---|---|
| `runmode` | `dev` → `prod`。dev 模式会输出调试信息、放宽部分限制，生产必须改 |
| `driverName` | `mysql` → `sqlite` |
| `dataSourceName` | **必须指向 `/conf/` 之下**（`file:/conf/casdoor.db`）。指向未挂载的路径时，库文件落在容器可写层，容器一删数据就没了 |
| `origin` | 决定 Casdoor 对外地址与 OIDC **issuer**。留空或仍是 `http://localhost:8000` 时，ID Token 的 `iss` 与 Kavita 的 Authority 不一致，校验直接失败 |

#### 3.3.4 其余常用项（按需）

| 配置项 | 默认值 | 说明 |
|---|---|---|
| `defaultLanguage` | `"en"` | 改成 `"zh"` 让管理界面默认中文 |
| `inactiveTimeoutMinutes` | 空 | 空 = 会话不过期。设定分钟数后，用户长时间不操作需重新登录 |
| `isUsernameLowered` | `false` | `true` = 用户名强制小写。与 Kavita 用户名策略有关 |
| `initDataFile` | `"./init_data.json"` | 首次启动导入的初始化数据。若不想每次都重置初始数据，配合 `initDataNewOnly = true` |
| `logConfig` | 见上 | 日志落盘位置，`filename` 相对容器内工作目录，已随 `./logs` 持久化 |
| `ldapServerPort` / `radiusServerPort` | 389 / 1812 | 本文不用 LDAP、RADIUS，保持默认即可 |
| `frontendBaseDir` | `"../cc_0"` | 前端静态资源目录。`casbin/casdoor` 镜像内已内置前端，留默认值即可；`all-in-one` 镜像才需要关注 |

> 环境变量与 `app.conf` 同时存在时以环境变量为准，混用容易搞不清生效值，建议统一用一种。
> SQLite 适合单机小规模，但并发写入能力有限，且**备份时必须停容器**（见 8.1）。

### 3.4 首次登录

- 默认账号：组织 `built-in` / 用户 `admin` / 密码 `123`。
- 登录后**立即修改超级管理员密码**。
- 容器不映射宿主端口，全部经 Nginx 访问；临时调试可加 `ports: - "8000:8000"`。

---

## 4. Casdoor 平台配置

访问 `https://casdoor.yourdomain.com`。

### 4.1 创建 OIDC 应用 `kavita-app`

`应用（Applications）→ 添加`：

| 配置项 | 值 | 说明 |
|---|---|---|
| Name / Display name | `kavita-app` | |
| Organization | `built-in`（或你的组织） | |
| Redirect URLs | 见下方 | **登出回调也必须登记**，否则 RP-initiated logout 被拒绝 |
| Token format | `JWT-Standard` | Kavita 侧 `MapInboundClaims = true` 依赖标准 claim 名 |
| Sign-in methods | `Password` + `QQ` + `Bilibili` | 去掉不需要的方式 |
| 自定义 Scope | 见 4.2 | 同步模式需要；仅认证模式可跳过 |
| 自定义声明 | 见 4.2 | 同上 |

Redirect URLs（三条都要）：

```
https://kavita.yourdomain.com/signin-oidc
https://kavita.yourdomain.com/signout-callback-oidc
https://kavita.yourdomain.com/login
```

> 第三条是登出后跳转地址。Casdoor 的 RP-initiated logout（`GET /api/logout`）会**强制校验** `post_logout_redirect_uri` 是否在此列表中，未登记则拒绝跳转。

保存后记录：**Client ID**、**Client secret**、Issuer = `https://casdoor.yourdomain.com`。

快速校验：访问 `https://casdoor.yourdomain.com/.well-known/openid-configuration`，确认 `issuer` 与 `end_session_endpoint`（应为 `.../api/logout`）正确。

### 4.2 ID Token 声明配置

**同步模式必须配置；仅认证模式可整节跳过**（配了也无害，Kavita 直接忽略）。

**自定义 Scope**：`应用 → kavita-app → 自定义 Scope`，添加 `groups`。

**自定义声明（Custom token claims）**：

| Claim name | Value | Token type |
|---|---|---|
| `email` | `{{user.email}}` | ID token |
| `groups` | `{{user.groups}}` | ID token |
| `email_verified` | `{{user.emailVerified}}` | ID token |

> 部分 Casdoor 版本的 ID Token 已自带 `groups` 与 `email_verified`，可先跳过；若 Kavita 提示角色未生效或 `Your email must be verified`，再回来补配。

验证方式：完成一次登录后，在 Casdoor `令牌（Tokens）` 中查看签发的 ID Token，或用 jwt.io 解析，确认存在 `groups` 数组与 `email_verified: true`。

### 4.3 社交登录 Provider（QQ、Bilibili）

`提供商（Providers）→ 添加`，两个 Provider 同样配置：

- Category：`OAuth`
- Type：`QQ` / `Bilibili`
- Client ID / Client Secret：开放平台应用凭证
- **关闭 `Auto sign-up`（自动注册）**

> 关闭 `Auto sign-up` 后，陌生社交账号扫码**不会**新建 Casdoor 账号，只能绑定到已有用户。这是"禁止社交直接注册"的唯一可靠开关。

开放平台侧的回调地址填：

```
https://casdoor.yourdomain.com/callback
```

### 4.4 邮箱策略

`应用 → kavita-app → 设置 → User profile edit items`，**取消勾选 Email**。

- 可勾选：Password、Account bindings、Display name
- 邮箱仅管理员可改；已对接 Kavita 的用户，管理员也应避免修改（见 10.1）

### 4.5 用户与用户组

**用户组**：同步模式必配，仅认证模式不需要。组命名规则见[附录 B](#附录-b角色库年龄分级映射速查)，最小可用配置是每人一个 `kavita-login`，管理员加 `kavita-admin`。

> Kavita 只看**组名（name）**，"显示名"随意。

**用户创建与绑定流程**：

1. 管理员新建用户，**邮箱确定后不再变更**，勾选 `Email Verified`（见 10.2）。
2. 同步模式下，将用户加入对应用户组。
3. 用户用账号密码登录 Casdoor 个人中心。
4. 进入账号绑定页面，手动绑定 QQ、B 站。
5. 绑定完成后，后续可直接扫码登录。

> 未绑定的社交账号扫码会被拒绝，不会新建账号。
> 用户名的取值顺序：`preferred_username` → `name` → `given_name` → `surname`，都不可用则退化为邮箱。想让 Kavita 用户名好看，建议在 Casdoor 填好显示名。

---

## 5. Kavita OIDC 配置

先访问应急入口 `https://kavita.yourdomain.com/login?skipAutoLogin=true`，用本地 admin 登录，再进入 `设置 → OpenID Connect`。

> `?skipAutoLogin=true` 是 Kavita 前端原生支持的跳过自动跳转参数；OIDC 出错时 Kavita 也会带此参数回跳 `/login` 并显示错误。**请记住这个入口**（应急回退见 8.2）。

### 5.1 通用参数（两种模式共用）

| 界面参数 | 配置值 | 备注 |
|---|---|---|
| Authority | `https://casdoor.yourdomain.com` | 必须 https，且不带多余路径 |
| Client ID | `kavita-app` 的 Client ID | |
| Client secret | `kavita-app` 的 Client secret | |
| Provider name | `Casdoor`（自定义） | 仅用于登录页按钮文案 |
| **Auto login** | 开启 | 未登录自动跳转 Casdoor，**推荐它替代 Nginx Cookie 判断** |
| **Provision Accounts** | 开启（JIT 创建用户） | 关闭时，Casdoor 中不存在的用户回调后报 `No matching account found` |
| **Require verified email** | 见 10.2 | **默认开启**；Casdoor 用户未勾选 Email Verified 将无法登录 |
| **Sync user settings with OIDC roles** | **模式开关** | 开启 → [5.2](#52-同步模式专有配置)；关闭 → [5.3](#53-仅认证模式专有配置) |
| Disable password authentication | 按需 | 开启后**本地 admin 也无法用密码登录**，应急前三思 |

改完**必须重启 Kavita 容器**（OIDC 在启动时装配，不重启不生效）：

```bash
docker compose restart kavita
```

### 5.2 同步模式专有配置

#### 5.2.1 必改的两个参数

| 参数 | 值 | 原因 |
|---|---|---|
| **Role claim（角色声明）** | `groups` | 默认值是 .NET 的 role claim 全名，而 Casdoor 输出的是 `groups`，不改则读不到 |
| **Role prefix（角色前缀）** | `kavita-` | 默认空，不改则所有组都被当作系统角色而匹配失败 |

同时 `Custom Scopes` 填 `groups`。

> 这是"组权限不生效"的最常见原因 —— 两个参数保持默认值时，Kavita 一个组都认不出来。

#### 5.2.2 角色映射规则

```
ID Token 的 groups[]
  → 只保留以 kavita- 开头的项
  → 剥离前缀
  → 与内置角色表做忽略大小写的全等匹配
  → 若同时存在 Admin 与 Read Only，剔除 Read Only（Admin 优先）
```

可映射角色（`PolicyConstants.ValidRoles`）：

| Kavita 角色 | Casdoor 组名 |
|---|---|
| `Login` | `kavita-login` |
| `Pleb` | `kavita-pleb` |
| `Admin` | `kavita-admin` |
| `Download` | `kavita-download` |
| `Bookmark` | `kavita-bookmark` |
| `Promote` | `kavita-promote` |
| `Change Password` | `kavita-change-password` ⚠️ |
| `Change Restriction` | `kavita-change-restriction` ⚠️ |
| `Read Only` | `kavita-read-only` ⚠️ |

> ⚠️ 含空格的三个角色需剥离前缀后**全等**匹配带空格的值，而 Casdoor 组名通常不允许空格，实际无法映射。OIDC 场景下一般用不到（密码由 Casdoor 管理），忽略即可。
> ⚠️ 前缀匹配**区分大小写**（`StartsWith` 无忽略大小写），组名请统一小写 `kavita-` 开头。

**新用户准入校验**：

```
若 groups 中既无 kavita-login 也无 kavita-admin
  → 拒绝创建，提示 "You do not have the required roles assigned to access this application"
```

因此**每个需要登录 Kavita 的用户都必须加入 `kavita-login` 或 `kavita-admin`**。

#### 5.2.3 库访问与年龄分级（二级前缀）

| 用途 | 组名格式 | 示例 |
|---|---|---|
| 库访问 | `kavita-library-<Kavita 库名>` | `kavita-library-漫画`、`kavita-library-Manga` |
| 年龄分级 | `kavita-age-restriction-<评级>` | `kavita-age-restriction-teen` |
| 包含未分级 | `kavita-age-restriction-include-unknowns` | 与上者叠加 |

- 库名匹配忽略大小写，但需与 Kavita 库名一致。
- 未配置任何年龄分级组时，用户被置为 `NotApplicable` 且包含未分级。
- `Admin` 自动获得全部库与全部年龄分级。

> **库同步是"覆盖"而非"追加"**：每次登录按当前 groups 重算可访问库集合，任何**未列出的库都会被移除访问权**。若某种登录方式下 `{{user.groups}}` 输出为空，用户会瞬间失去全部库——表现为阅读器能连上但书架空了，易被误判为 Key 失效。
> 不需要按组控库时，**不要配置 `kavita-library-*` 组**。

#### 5.2.4 让角色集合保持稳定

`hasMadeChanges` 由"角色集合前后不一致"触发，想让阅读器体验无感，需保证每次登录算出的角色集合恒定：

- **配好后不要频繁增删组** —— 每次增删都触发同步，Key 虽不变但权限会变。
- **`kavita-pleb` 写了就保持恒定** —— 不写也没关系，角色不含 `Admin` 时 Kavita 会自动补 `Pleb`；最忌讳"写了又删"来回横跳。
- **确认不同登录方式下 groups 输出一致** —— 若密码登录返回完整 groups，而 QQ/B 站扫码时 `{{user.groups}}` 为空，用户一换登录方式就掉光角色与库权限。
  验证：分别用三种方式登录，在 Casdoor `令牌（Tokens）` 中比对 ID Token 的 `groups`。

### 5.3 仅认证模式专有配置

#### 5.3.1 本质：关闭 Sync user settings

一句话：把 **Sync user settings with OIDC roles** 关掉。关闭后被跳过的代码路径：

| 函数 | 关闭同步时的行为 |
|---|---|
| `SyncUserSettings(...)` | 首行 return → email、用户名、角色、库、年龄分级、头像**全部不同步** |
| `CreateNewAccount(...)` 的角色准入 | 跳过 → 新用户**不再要求** `kavita-login` |
| `SetDefaults(...)` | 生效 → 按默认值给新用户分配权限 |
| `RefreshCookieToken(...)` 中的同步 | 同样被拦 → 后台刷新令牌时也不动权限 |

**仍然生效**（与同步开关无关）：`sub` 匹配、email 要求、`Require verified email`、`Provision Accounts`、认证 Cookie、`Auto login`、单点登出。

#### 5.3.2 配置步骤

**Kavita 侧**

1. 关闭 `Sync user settings with OIDC roles`
2. 配置"默认值"区 —— **必做**，见 5.3.3
3. `Role claim` / `Role prefix` 保持默认即可（不再读取任何 claim）
4. `Provision Accounts`、`Require verified email` 按需保持开启
5. **重启 Kavita 容器**

**Casdoor 侧**

- **不需要**创建 `kavita-*` 用户组
- **不需要**配置 `groups` 自定义声明与自定义 Scope
- 仍需保留：OIDC 应用、Redirect URLs、社交 Provider（关闭 Auto sign-up）、邮箱禁止自助修改

#### 5.3.3 默认值配置（必做，否则新用户进不去）

Kavita 设置页的提示已经写明「如果禁用此选项，用户在帐户创建后将无法访问任何内容」。这不是吓唬人：

| 默认值项 | 不配的后果 | 建议 |
|---|---|---|
| **Default roles** | 角色中既无 `Login` 也无 `Admin` 的用户被判定为禁用账户（`/api/account` 返回 `disabled-account`），登录后什么都做不了 | 普通用户勾 `Login`；管理员勾 `Admin` |
| **Default libraries** | 覆盖式写入，空列表等于移除全部库，书架空 | 勾选默认可见的库 |
| **Default age restriction** | 默认 `Unknown`，会过滤掉大量内容 | 通常设为 `NotApplicable` |
| **Default include unknowns** | 默认关闭 | 按需 |

> **关于 `Login` 角色**：`UpdateRolesForUser` 会在角色不含 `Admin` 时自动补 `Pleb`，所以勾 `Login` 后最终是 `[Login, Pleb]`。但 `Login` **不会**被自动补上，必须显式勾选。

#### 5.3.4 日常权限管理流程

1. 用户在 Casdoor 登录 → Kavita JIT 创建用户，套用 5.3.3 的默认值
2. 管理员在 Kavita `设置 → 用户` 手工调整角色、可访问库、年龄分级
3. 用户后续登录 → **Kavita 不会覆盖这些手工设置**
4. 需要提权/降权时，重复第 2 步

第 3 点就是这个模式的核心价值：手工权限是**稳态**的。

#### 5.3.5 收益与局限

| 收益 | 说明 |
|---|---|
| 改邮箱零风险 | 不同步 email，Casdoor 侧改邮箱对 Kavita 完全无影响（对比 10.1） |
| 不怕 groups 输出不稳定 | 不读 claim，三种登录方式的 groups 不一致也无所谓 |
| 不会误删库权限 | 无 5.2.3 的覆盖问题 |
| 用户名稳定 | 不同步 username，改显示名不影响 Kavita 用户名 |
| 权限不被 IdP 误操作波及 | Casdoor 改组、删组都不会传导到 Kavita |

| 局限 | 说明 |
|---|---|
| 无法批量授权 | 每个新用户都要管理员手工过一遍 |
| 默认值是全局的 | 所有新用户套用同一套，无法按人区分 |
| 没有细粒度库/分级下发 | 该能力依赖同步模式 |
| **删用户不会同步** | Casdoor 删用户后，Kavita 中对应账号仍存在，凭 Cookie 与 API Key 仍可访问，需管理员手工处理 —— 比同步模式更需要主动运维 |

---

## 6. Nginx 反向代理

### 6.1 自动跳转方案

**方案 A（推荐）：Kavita 内置 Auto Login**

开启 `Auto login` 即可，前端未登录时自行跳转 `/oidc/login`，Nginx 只做纯反向代理。优点：不依赖 Cookie 名、不会误判、支持 `?skipAutoLogin=true` 应急入口。

**方案 B：Nginx 判断 Cookie**

仅在需要"未登录直接跳 Casdoor 授权页、连 Kavita 登录页都不展示"时使用。**Cookie 名必须是 `.AspNetCore.Cookies`**：

```nginx
if ($http_cookie !~* "\.AspNetCore\.Cookies") { ... }
```

> 正则中的 `.` 需转义，否则误匹配；同时务必放行 `/login`、`/signin-oidc`、`/signout-callback-oidc`，否则回调陷入重定向死循环。

### 6.2 完整配置模板

以下是方案 A 的模板（已含 WebSocket 与必要转发头）：

```nginx
map $http_upgrade $connection_upgrade {
    default upgrade;
    ''      close;
}

# HTTP → HTTPS
server {
    listen 80;
    server_name casdoor.yourdomain.com kavita.yourdomain.com;
    return 301 https://$host$request_uri;
}

# ---------- Casdoor ----------
server {
    listen 443 ssl;
    http2 on;
    server_name casdoor.yourdomain.com;

    ssl_certificate     /etc/letsencrypt/live/casdoor.yourdomain.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/casdoor.yourdomain.com/privkey.pem;

    client_max_body_size 20M;

    location / {
        proxy_pass http://127.0.0.1:8000;   # 或用 Docker 网络: http://casdoor:8000
        proxy_http_version 1.1;

        proxy_set_header Host              $host;
        proxy_set_header X-Real-IP         $remote_addr;
        proxy_set_header X-Forwarded-For   $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header X-Forwarded-Host  $host;
    }
}

# ---------- Kavita ----------
server {
    listen 443 ssl;
    http2 on;
    server_name kavita.yourdomain.com;

    ssl_certificate     /etc/letsencrypt/live/kavita.yourdomain.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/kavita.yourdomain.com/privkey.pem;

    client_max_body_size 200M;   # 上传漫画/书籍

    location / {
        proxy_pass http://kavita:5000;
        proxy_http_version 1.1;

        proxy_set_header Host              $host;
        proxy_set_header X-Real-IP         $remote_addr;
        proxy_set_header X-Forwarded-For   $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header X-Forwarded-Host  $host;

        # Kavita 使用 SignalR（/hubs/messages、/hubs/logs），缺 Upgrade 头会导致实时功能异常
        proxy_set_header Upgrade    $http_upgrade;
        proxy_set_header Connection $connection_upgrade;

        proxy_read_timeout  3600s;
        proxy_send_timeout  3600s;
        proxy_buffering     off;
    }
}
```

```bash
nginx -t && systemctl reload nginx
```

### 6.3 转发头说明

Kavita 启用了 `ForwardedHeaders.All`，并依据 `X-Forwarded-Proto` / `X-Forwarded-Host` 生成 OIDC 的 `redirect_uri`。

- `proxy_set_header Host $host;` **不能省略**，否则回调地址可能生成为 `http://kavita:5000/...`。
- 若配置了 `KnownProxies`（appsettings.json），必须填入 Nginx 的真实 IP，否则转发头被丢弃，同样导致 `redirect_uri` 错误。
- Kavita 回调时会强制把 `redirect_uri` 中的 `http://` 替换为 `https://`，但这**不能替代**正确的转发头配置。

---

## 7. 验证与验收

### 7.1 通用验收项（两种模式都要过）

| # | 操作 | 预期结果 |
|---|---|---|
| 1 | Casdoor 新建测试用户，勾选 `Email Verified` | 创建成功 |
| 2 | 用户密码登录 Casdoor 个人中心，绑定 QQ、B 站 | 绑定成功 |
| 3 | 无痕窗口访问 `https://kavita.yourdomain.com` | 自动跳转 Casdoor 授权页 |
| 4 | Casdoor 密码登录 | 回调 Kavita，JIT 创建用户并登录 |
| 5 | 退出，改用 QQ 扫码登录 | 进入**同一个** Kavita 用户，阅读进度与收藏共用 |
| 6 | 改用 B 站扫码登录 | 同上，仍是同一用户 |
| 7 | `docker compose restart casdoor` | 用户、应用、社交配置、绑定关系全部保留 |
| 8 | 未绑定的 QQ 账号扫码 | 被拒绝，不新建账号 |

### 7.2 同步模式专项

| # | 操作 | 预期结果 |
|---|---|---|
| 9 | 新建用户时加入 `kavita-login` 与目标业务组 | — |
| 10 | 分别用三种方式登录，在 Casdoor `令牌（Tokens）` 中查看 ID Token | 三者 `groups` **完全相同**（见 5.2.4） |
| 11 | Kavita `设置 → 用户` 查看该用户 | 角色与库权限与 Casdoor 组一致 |
| 12 | Casdoor 中改用户组为 `kavita-admin`，用户重新登录 | 角色同步为 Admin；**API Key 保持不变**，阅读器无需操作 |

### 7.3 仅认证模式专项

| # | 操作 | 预期结果 |
|---|---|---|
| 9 | 新建用户时**不加入任何 `kavita-*` 组** | 创建成功 |
| 10 | 三种方式登录 | 都能正常进入 Kavita（groups 内容无关紧要） |
| 11 | Kavita `设置 → 用户` 查看新用户 | 角色与库等于 5.3.3 配置的默认值 |
| 12 | 管理员在 Kavita 中修改该用户权限，用户重新登录 | 权限**未被覆盖** |
| 13 | 在 Casdoor 中修改该用户的组 | Kavita 权限**不受影响** |

---

## 8. 运维：备份、回退、升级

### 8.1 备份与恢复

SQLite 下**必须先停容器**再打包，否则可能拿到写入中间态的库文件（备份能用，恢复后却数据错乱）。

```bash
# Casdoor（casdoor.db 就在 conf/ 内，整目录打包）
docker compose stop casdoor
tar -zcvf casdoor-conf-$(date +%Y%m%d).tar.gz ./conf
docker compose start casdoor

# Kavita
docker compose stop kavita
tar -zcvf kavita-data-$(date +%Y%m%d).tar.gz ./kavita-data
docker compose start kavita
```

> 不要直接复制/编辑 `casdoor.db` 单文件，务必整目录打包。
> 恢复：停容器 → 解压覆盖 → 启动容器。
> 想不停机备份可用 `sqlite3 ./conf/casdoor.db ".backup /tmp/casdoor.db.bak"`（需宿主机装 sqlite3），比 `cp` 安全。

### 8.2 应急回退：恢复本地密码登录

Casdoor 不可用时，让本地 admin 重新可用：

1. **能进 Kavita**（`?skipAutoLogin=true`）：`设置 → OpenID Connect` 关闭 `Auto login` 与 `Disable password authentication` → 保存 → 重启容器。
2. **进不去**：清空 Kavita `appsettings.json`（容器内 `/kavita/config/appsettings.json`）的 `Authority` 字段并重启，等效于完全禁用 OIDC。
3. **需要重新绑定 OIDC**：用 `设置 → OpenID Connect` 的 **Reset external IDs**（`ClearOidcIds`）清空全部用户的 `OidcId`，下次登录走 email 兜底匹配。

> 注意第 3 种：清空后若某用户 `OidcId` 非空（未被清空）而邮箱与他人冲突，会报 `Email already in use`。

### 8.3 升级

```bash
docker compose pull
docker compose up -d
```

跨大版本升级前先执行 8.1 的完整备份（尤其 Casdoor，其数据库迁移不可逆）。

---

## 9. 故障排查

日志位置：`./kavita-data/logs/kavita.log`。OIDC 相关为 `Debug/Trace` 级，排错时可在 `设置 → 日志` 临时提高级别。

### 9.1 报错文案 → 原因对照

| 页面/日志提示 | 源码错误码 | 原因与处理 |
|---|---|---|
| `OIDC did not return a valid identifier` | `missing-external-id` | ID Token 缺 `sub`。检查 Casdoor 应用 Token format 是否为 `JWT-Standard` |
| `OIDC did not return a valid email` | `missing-email` | ID Token 缺 `email`。补配 4.2 的自定义声明 |
| `Your email must be verified to allow login via OIDC` | `email-not-verified` | 勾选 Casdoor 用户的 Email Verified，或关闭 `Require verified email` |
| `No matching account found` | `no-account` | 未开启 `Provision Accounts`，或同步刷新后找不到用户 |
| `You do not have the required roles assigned to access this application` | `role-not-assigned` | 同步模式下用户缺少 `kavita-login` / `kavita-admin` |
| `Email already in use by another account` | `email-in-use` | 邮箱与其他 Kavita 用户冲突，见 10.1 |
| `Failed to create a new user, please contact an administrator` | `creating-user` | 用户名冲突等，查 Kavita 日志定位 |
| `Failed to sync your account from OIDC, please contact an administrator` | `syncing-user` | 角色/库写入失败，查 Kavita 日志 |

### 9.2 现象 → 排查方向

| 现象 | 排查方向 |
|---|---|
| 登录后反复跳转 Casdoor，死循环 | ① Nginx Cookie 名错误（应为 `.AspNetCore.Cookies`）；② `/signin-oidc` 被 Nginx 规则拦截；③ 两服务不同父域导致 Cookie 跨站被丢弃 |
| 回调报 `redirect_uri` 不匹配 | Casdoor Redirect URLs 未登记或写错；Nginx 缺 `Host` / `X-Forwarded-*` 头；`KnownProxies` 配错 |
| Kavita 完全不出现 OIDC | Authority 非 https 被禁用；改配置后未重启；查日志 `OpenIdConnect authority is not using https` |
| 组权限不生效 | `Role claim` 是否为 `groups`；`Role prefix` 是否为 `kavita-`；组名是否带前缀；剥离前缀后是否合法；`Sync user settings` 是否开启 |
| 阅读器连得上但书架空了 | 不是 Key 失效，是库访问权被覆盖。检查 `kavita-library-*` 是否配全（5.2.3） |
| 阅读器提示 Key 无效 | 多为 Key 自身到期，或用户被删重建（`sub` 变了）。角色变化不会导致此项（2.5） |
| 退出后立即自动登录回来 | `Auto login` 的预期行为；配置 Casdoor 登出后会先清 Casdoor 会话 |
| 单点登出不生效 | Redirect URLs 未登记 `https://kavita.yourdomain.com/login`；确认 `end_session_endpoint` 存在 |
| 社交扫码直接建号 | Provider 的 `Auto sign-up` 未关闭 |
| Casdoor 重启后配置丢失 | `./conf` 未挂载，或 `dataSourceName` 未指向 `/conf/` 之下；检查属主是否 uid 1000 |
| Casdoor 反复重启 / `permission denied` | 挂载目录属主不是 uid 1000，见 3.2 |
| `database is locked` / 登录变慢 | SQLite 写入瓶颈，见 10.9 |
| QQ/B 站回调报域名错误 | 开放平台回调应为 `https://casdoor.yourdomain.com/callback`，且两域名均已备案 |

---

## 10. 风险与约束

### 账号类

**10.1 邮箱变更**
Kavita 以 `sub` 为主匹配键，修改 Casdoor 邮箱本身不会新建账号。但同步模式下 Kavita 会同步新邮箱，若被其他 Kavita 用户占用，报 `Email already in use` 并**导致该用户无法登录**。
确需换邮箱的顺序：先在 Kavita 改对应用户邮箱 → 再改 Casdoor 邮箱 → 用户重新登录。
仅认证模式无此问题（不同步 email）。

**10.2 Email Verified 必须勾选**
`Require verified email` 默认**开启**。Casdoor 新建用户未勾选 Email Verified，登录失败并提示 `Your email must be verified to allow login via OIDC`。
二选一：勾选 Casdoor 用户的 Email Verified，或关闭 Kavita 的 `Require verified email`。

**10.3 禁止社交自动注册**
Provider 的 `Auto sign-up` 必须关闭，否则任意陌生社交扫码即可创建 Casdoor 与 Kavita 账号。

### 授权类

**10.4 同步模式下必须有 `kavita-login`**
缺此组的新用户在回调时被拒绝创建。改用仅认证模式可绕开此要求（见 5.3）。

**10.5 Role claim 与 Role prefix 必须手动设置**
默认值分别是 .NET role claim 全名与空字符串，不改成 `groups` / `kavita-` 时组权限完全不生效。

**10.6 库同步是覆盖式的**
一旦启用 `kavita-library-*`，未列出的库会被移除访问权（见 5.2.3）。

### 部署类

**10.7 保留本地 admin 与应急入口**
日常业务账号全部在 Casdoor 创建；不要轻易开启 `Disable password authentication`，它会连本地 admin 的密码登录一并禁用。记住 `/login?skipAutoLogin=true`。

**10.8 全程 HTTPS**
HTTP 会导致 OIDC、QQ/B 站 OAuth 报错；非 Development 环境下 Kavita 检测到 Authority 非 https 会直接禁用 OIDC。

**10.9 SQLite 的并发限制**
适合单机小规模。出现 `database is locked` 或并发登录明显变慢时，说明已触及写入瓶颈，应改用外部 MySQL/PostgreSQL（改 `driverName` / `dataSourceName` 即可，容器内已含驱动）。

**10.10 确保 `dataSourceName` 指向挂载目录**
必须写成 `file:/conf/casdoor.db`。指向未挂载路径时库文件落在容器可写层，容器一删数据就没了。

**10.11 不要手工改动 Casdoor 数据库文件**
备份与恢复以整目录为单位；SQLite 下必须停容器后再打包。

### 集成类

**10.12 OPDS 不走 SSO**
阅读器需在网页端 `账户设置` 生成 API Key。角色变化只清缓存、不吊销 Key（见 2.5）；但创建 Key 时应**留空过期时间**，否则到期会真正失效。

---

## 附录 A：配置参数速查表

### Casdoor

| 位置 | 参数 | 值 |
|---|---|---|
| 镜像 | 生产用镜像 | `casbin/casdoor:latest` |
| 配置 | `origin` | `https://casdoor.yourdomain.com` |
| 配置 | `httpport` | `8000` |
| 配置 | `driverName` / `dataSourceName` | `sqlite` / `file:/conf/casdoor.db` |
| 挂载 | 卷 | `./conf:/conf`、`./logs:/logs`（属主 1000） |
| 应用 | Redirect URLs | `https://kavita.yourdomain.com/signin-oidc`<br>`https://kavita.yourdomain.com/signout-callback-oidc`<br>`https://kavita.yourdomain.com/login` |
| 应用 | Token format | `JWT-Standard` |
| 应用 | Sign-in methods | Password / QQ / Bilibili |
| 应用 | 自定义 Scope | `groups`（仅同步模式） |
| 应用 | 自定义声明 | `email`→`{{user.email}}`、`groups`→`{{user.groups}}`、`email_verified`→`{{user.emailVerified}}`（均 ID token，仅同步模式） |
| 应用 | User profile edit items | 取消勾选 Email |
| Provider | Auto sign-up | **关闭** |
| 用户 | Email Verified | 勾选 |
| 组 | 命名 | `kavita-login`、`kavita-admin`、`kavita-library-<库名>` …（仅同步模式） |

### Kavita

| 参数 | 值 |
|---|---|
| Authority | `https://casdoor.yourdomain.com` |
| Client ID / Secret | Casdoor `kavita-app` 的值 |
| Auto login | 开启 |
| Provision Accounts | 开启 |
| Require verified email | 视 Casdoor 是否勾选 Email Verified |
| **Sync user settings** | **开启** = 同步模式（5.2）；**关闭** = 仅认证模式（5.3） |
| Role claim | `groups`（仅同步模式） |
| Role prefix | `kavita-`（仅同步模式） |
| Custom Scopes | `groups`（仅同步模式） |
| Default roles | 仅认证模式**必配**：至少勾选 `Login`（5.3.3） |
| Default libraries | 仅认证模式**必配**：勾选默认可见的库（5.3.3） |
| Default age restriction | 仅认证模式建议 `NotApplicable`（5.3.3） |

### Nginx

| 项 | 值 |
|---|---|
| Cookie 名（仅方案 B） | `.AspNetCore.Cookies` |
| 必需头 | `Host`、`X-Forwarded-Proto`、`X-Forwarded-Host`、WebSocket `Upgrade`/`Connection` |

---

## 附录 B：角色/库/年龄分级映射速查

前缀统一为 `kavita-`，由 Kavita 的 `Role prefix` 决定。

### 角色

| Casdoor 组名 | Kavita 角色 |
|---|---|
| `kavita-login` | Login（登录必需） |
| `kavita-pleb` | Pleb（普通用户） |
| `kavita-admin` | Admin（管理员，与 Read Only 互斥） |
| `kavita-download` | Download |
| `kavita-bookmark` | Bookmark |
| `kavita-promote` | Promote |
| `kavita-read-only` | Read Only |

### 库访问

```
kavita-library-<Kavita 库名>
```

库名匹配忽略大小写；Admin 自动拥有全部库。

### 年龄分级

```
kavita-age-restriction-<评级>
kavita-age-restriction-include-unknowns
```

`include-unknowns` 单独出现表示允许未分级内容、不限制年龄；与其他评级共存时叠加生效。Admin 自动跳过全部年龄限制。

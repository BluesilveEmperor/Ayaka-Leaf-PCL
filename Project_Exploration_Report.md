# Ayakaleaf Pro · 项目全景探索报告

> 生成方式：team-leader 统筹，5 名探索员分模块并行深入探索后汇总。
> 覆盖范围：约 500 个源文件，涵盖基础设施、Minecraft 核心、UI、Ayakaleaf 定制与服务部署、工程与测试五大板块。

---

## 一、一句话定位

本仓库是 **PCL CE**（Plain Craft Launcher 2，Minecraft 启动器，上游版本 2.12.1）经 **Ayaka 定制**（本地版本 2.15.1）改造而成的 **AyakaLauncher**——一个「**本地个人 Overleaf / ShareLaTeX 一体化桌面管理器**」。

技术栈：**.NET 10 / C# 14 / WPF**。

其产品本质是把 Minecraft 启动器外壳改造为**部署并管控本地 LaTeX 协作平台的控制台**：内核通过 WSL 互操作在内嵌 Linux 发行版里用 Docker Compose 运维一整套 Overleaf Server Pro（MongoDB 副本集 + Redis + ShareLaTeX），并配套本地多用户账号体系与内嵌浏览器工作台。原始 Minecraft 启动/联机功能已被大幅裁剪。

---

## 二、技术栈与工程体系

| 项目 | 说明 |
| --- | --- |
| 目标框架 | `net10.0-windows` |
| 语言版本 | C# 14（`LangVersion 14.0`） |
| 平台矩阵 | `AnyCPU; x64; ARM64` |
| 构建配置 | `Debug; CI; Release; Beta` |
| 解决方案 | `Plain Craft Launcher 2.slnx`（新式 slnx 格式） |
| 代码规范 | `Nullable enable`、`AllowUnsafeBlocks`、`EnableDefaultItems=false`（手动 Include）、`NoWarn CS9113` |

### 解决方案组成（4 个项目）

- **`Plain Craft Launcher 2`** — WPF 应用主体（UI 与业务逻辑）
- **`PCL.Core`** — 内核库（`PCL.Core.*` 命名空间）
- **`PCL.Core.SourceGenerators`** — Roslyn 源代码生成器
- **`PCL.Core.Test`** — MSTest 单元测试

### 版本管理

- `metadata.json`：名称 `AyakaLauncher`，`base 2.15.1`、`upstream 2.12.1`、`code 524`
- 主项目 `AssemblyVersion / FileVersion`：`2.15.1.0`
- release 工作流输入 `version`：用于 git tag 与 CHANGELOG
- ⚠️ **版本号分散在三处手工维护，存在不一致隐患**

### CI/CD

- **构建**：`build-test.yml`（push `dev` / PR 触发）→ 矩阵 `CI × {x64, ARM64}` → `reusable-build.yml`（`setup-dotnet 10.0.x` + 注入 `PCL_WRITE_SECRET` 等 secrets + `dotnet publish`）
- **发布**：`release-publish.yml`（手动，Beta/Release）→ `git-cliff` 生成 CHANGELOG → `softprops` 创建 draft release → `sigstore` 签名 exe → 触发镜像站上传
- **镜像**：`mirrorchyan_*.yml` 按 stable/beta、x64/arm64 上传至 Mirror 酱
- `cliff.toml`：git-cliff + conventional commits，中文分组模板
- ⚠️ **CI 从不执行 `dotnet test`，且 `build-test` 忽略 `PCL.Core.Test/**`，测试实际未被验证**

---

## 三、架构分层

```
Plain Craft Launcher 2  (WPF UI 主体)
  ├─ Pages/      功能页（Launch / Download / Instance / Setup / Tools / ...）
  ├─ Controls/   自绘 My* 控件家族（MyButton / MyCard / MyHint / MyIconButton / ...）
  ├─ Modules/    业务逻辑（ModLaunch / ModDownload / ModComp / ModLink / ModWebServer / ...）
  └─ FormMain    双栏导航 + 页面栈
        │  引用
        ▼
PCL.Core  (内核库, PCL.Core.* 命名空间)
  ├─ App/        IoC · 生命周期 · 配置 · 事件总线 · 任务 · 本地化 · 地区 · 密钥
  ├─ Utils/      加密 · 哈希 · SemVer · OS · 线程 · 校验 · Codecs · Diff
  ├─ IO/         网络(NetworkService/HttpServer/DoH/代理) · 下载 · 哈希与缓存存储
  ├─ Logging/    Channel 异步日志 + Microsoft.Extensions.Logging 适配
  ├─ Minecraft/  新版游戏核心(Profile / Java / IdentityModel / Saves / ResourceProject)
  ├─ UI/         主题 · 动画(AnimationService) · 特效 · 控件 · 媒体
  └─ Ayakaleaf/  定制层(服务栈管理 · 网页账户 · WSL 互操作)
        │  SourceGenerator 在编译期生成 配置/DI/生命周期/密钥注入 代码
        ▼
编译器生成代码 (ConfigService.g.cs / DependencyGroups.g.cs / ...)
```

---

## 四、核心模块要点

### 4.1 基础设施（PCL.Core）

- **自研特性驱动框架**：`[LifecycleService]` + `[ConfigItem]` + `[DependencyCollector]` 配合 SourceGenerator，**取代传统 DI 容器**。
- **配置系统**（能力最强）：partial 类 + 特性声明，SourceGenerator 生成访问器；多来源 `ConfigSource`（Shared / SharedEncrypt / Local / GameInstance）；Provider 链 `IKeyValueFileProvider`(Json/Yaml/CatIni) → `ConfigStorage` → `EncryptedFileConfigStorage`(AES-GCM)；支持异步合并写、可拦截事件（preview observer 可取消/改值）、旧格式 `ConfigMigration`。
- **IoC / 生命周期**：扫描 `[LifecycleService(StartState, Priority)]`，状态机按优先级并行启停、栈逆序停止；DI 由 `[DependencyCollector]` / `[DependencyInjectionPoint]` + 生成代码完成。
- **EventBus**：channel + 事件类型 + Guid 订阅，支持 `IEventHandler<T>` / 委托，WeakReference owner 可随退订释放。
- **日志**：`LogWrapper`（静态门面）→ `LogService`；`Logger` 用 Channel 异步落盘，等级映射 `ActionLevel`（联动 Hint/弹窗）；`LoggerAdapter` 兼容 `Microsoft.Extensions.Logging`；`ExceptionDetails` 做用户友好化 + 脱敏。
- **数据库**：主用 **LiteDB**（文档型 NoSQL，非 SQLite/Dapper）；缓存与哈希层另用 `Microsoft.Data.Sqlite`（WAL）。
- **IO/网络**：
  - Download：仅有抽象接口（`IDlConnection` / `IDlWriter` / `IDlResourceMapping` / `NDlFactory` / `NDlSourceReport`），支持分块/并行写/源质量报告，实现位于主项目。
  - Net：`NetworkService`（HttpClientFactory + Polly + 命名客户端）；`HttpServer`（HttpListener + `[HttpRoute]`）；`HttpProxyManager`（监听注册表系统代理）；DoH + `WeightedDnsRacerClient` 加权竞速。
  - Storage：`HashStorage` 哈希寻址（前缀分目录 / 压缩 / 纠错）；`CacheService`（SQLite 元数据 + 文件映射，<256KB inline，标签/分组/淘汰）。
- **Utils 亮点**：Encryption（AES-GCM/CBC、ChaCha20Poly1305、软件 ChaCha20，按硬件能力选择）；Hash（多算法 Provider + MurmurHash2 + HashCache）；SemVer/VersionRange、Diff(BsDiff)、Result、ConcurrentSet、AtomicVariable；OS（SystemInfo/HardwareInfo(WMI)/Kernel/NtInterop/Aumid/Window/SystemTheme/RegistryChangeMonitor）；Threading（LimitedTaskPool/DualThreadPool/AsyncDebounce）；Validate（FluentValidation 校验器集）；Codecs 编码检测；WinRT 互操作。

### 4.2 Minecraft 游戏核心

> ⚠️ **最重要发现：双架构并存**。新版 `PCL.Core/Minecraft`（async/DI 重构）与旧版 `Modules/Minecraft`（巨类静态实现，实际驱动 UI）职责重叠，处于**迁移中间态**。

- **实例与版本管理**（`ModFolder` / `ModInstanceList` / `McInstance`）：
  - `ModFolder` 扫描 exe 目录、官启 `%APPDATA%\.minecraft`，分类 Original/RenamedOriginal/Custom，生成 `launcher_profiles.json`。
  - `ModInstanceList` 以 `hash(缓存版本 + 文件夹列表)` 作 InstanceCache 存于 `PCL.ini`，命中则跳过 JSON 解析。
  - `McInstance` 支持 `inheritsFrom` **继承链合并**（Forge/Fabric），`IsOldJson` 区分新旧参数格式；`PathIndie` 实现版本隔离与旧 `IndieV1` 迁移。
- **启动全链路**（`ModLaunch.cs`，2796 行）：`McLaunchStart` → 预检测 → `McLaunchJava` → 登录 → `DlClientFix` 补文件 → `McLaunchArgumentMain` 构建参数 → 解压 natives → 预启动 → 自定义命令 → `McLaunchRun` → `McLaunchWait` → 结束。
  - JVM 参数含 `-Xmx/-Xmn/-Xms`、`java.library.path`、`-cp ${classpath}`、authlib-injector、渲染器（mesa llvmpipe/d3d12/zink）、代理、Java Wrapper、mainClass 等。
- **下载与安装**（`ModDownload.cs` 2535 行 / `ModDownloadLib.cs` 4622 行）：`DlClientFix` 三阶段（libraries / assets / asset index）；官方 + BMCLAPI 多源择优；`ForgelikeInjector` 启动 `forge_installer.jar` 落地，要求 Java ≥ 8.0.60，缺失自动下载；`ModLibrary` 解析 rules/natives-windows/classifiers 生成 classpath。
- **Java 检测与下载**（`JavaManager` / `PeHeaderParser` / `ModJava`）：多扫描器聚合（注册表/环境变量/where/Store/默认路径）+ PE 头识别品牌与位数；`JavaSelect` 三级优先（实例偏好 → 全局 → 自动搜索）；缺失时从 piston-meta `java-runtime/all.json` 下载。
- **登录与身份**：新版 `ProfileService`（lifecycle + 加密存储 + 旧 `profiles.json` 迁移）→ `MicrosoftProvider`（OAuth device code→Xbox Live→XSTS→Minecraft 六步）、`YggdrasilConnectProvider`（OIDC，内建 littleskin clientId=1514）、`AuthlibProvider`；旧版走 `ModLaunch` 内登录 + authlib-injector 注入。
- **工具链**：`NbtFileHandler`（fNbt 异步）；Saves 解析器按版本从高到低；`MavenArtifact` / `MavenComparableVersion`；`McVersionClassifier`（识别愚人节版本）；`ServerAddressResolver`（SRV + Happy Eyeballs）。

### 4.3 UI 层

- **架构**：**MVVM 仅在新闻模块落地**，主体仍为 code-behind 事件驱动（混合式、重构中间态）。
- **自定义控件**：两套并存——`PCL.Core/UI/Controls`（`BlurBorder` / `Tooltip` / `WaterfallPanel` / 自研 `SvgIcon` 图标引擎）与主项目 `Controls/`（`MyButton` / `MyCard` / `MyHint` / `MyIconButton` / `MyListItem` / `MyScrollViewer` 等自绘家族）。
- **主题/换肤**：`ThemeService` 基于 Lab/Lch 色彩空间（`ToneProfile`）动态生成 Gray/Color/Bg 资源，支持 4 套主题 + 深浅自动；旧的 `GrayProfile` 并存为技术债。
- **动画与特效**：**新旧两代并存**——新版 `PCL.Core/UI/Animation`（`AnimationService`：WinMM 时钟 + Channel + 多线程 ValueProcessor），但**页面实际全部使用旧版 `ModAnimation`**；`BackgroundPresenter` 反射 WPF 私有字段实现磨砂玻璃；`Effects/` 带 HLSL 着色器。
- **导航**：`FormMain.PageChange` → `PageChangeActual` → `PageChangeAnim`；`MyPageRight` 9 态状态机驱动加载环与逐元素进出动画；支持页面栈 `PageBack`。

### 4.4 Ayakaleaf 定制与服务部署（本仓库核心）

- **品牌化**：AssemblyName → `AyakaLauncher`、logo 文案、设置页文案全替换。
- **本地用户体系**：SQLite（`users.db`）+ **PBKDF2-HMAC-SHA256 600k 迭代**、每用户独立盐；首用户自动管理员；每用户独立游戏数据目录；启动前登录门禁。
- **拆除上游存在**：联机页、公告/捐赠、Sentry 遥测、在线更新检查、MC 入口（Launch/Java/GameManage 导航项）。
- **新增三页面**：**服务台 / 镜像 / 备份**（`PageServiceLeft/Right`、`PageMirror`、`PageBackup`），FormMain 四 Tab（服务/镜像/备份/设置）。
- **`AyakaleafService`**：`DeployAsync`（wsl --import `runtime.tar` + `docker load` 全部镜像）、`Start/StopAsync`（`docker compose up/down`）、`ProbeAsync`（HTTP 探活 `/login`）、容器/日志/镜像列表、数据卷 `Backup/Restore`（直接 tar 操作 `/var/lib/docker/volumes`）。
- **`AyakaleafAccountService`**：为每个本地用户生成账号 `用户名@ayaka.local` + 24 位随机密码，DPAPI(CurrentUser) 加密存 `data/ayakaleaf/accounts/<user>.json`；调用 Overleaf `/register`、`/login` 取 `overleaf.sid` Cookie。
- **`WorkbenchWindow`**：内嵌 WebView2 注入 sid 自动登录 `/project`，会话失效自动重登 ≤2 次。
- **`WslInterop`**：封装 `wsl.exe`，`WSL_UTF8=1` 强制 UTF-8；`ToWslPath`、超时强杀进程树、`ListDistros` / `DistroExists`；配合 `sleep infinity` 保活防止 WSL 挂起杀 dockerd。
- **runtime 部署栈**（`runtime/docker-compose.yml`）：Overleaf **Server Pro（CE fork）** 套栈
  - `mongo:8.0`（副本集 overleaf，支持事务）+ `mongo-init`（一次性初始化副本集）
  - `redis:7`
  - `overleaf: sharelatex/sharelatex:server-pro`（端口 8080:80，卷 overleaf_data/logs）
  - 镜像 tar：`sharelatex-server-pro`(1.3G)、`sharelatex-base-server-pro`(360M，TeXLive2026+Node24)、`mongo-8.0`(314M)、`redis-7`(43M)，另有 `runtime.tar`(685M，WSL rootfs)
- **Link 模块**（上游保留，UI 入口被拆）：EasyTier 内网穿透（`ETController`）、联机大厅（`Lobby` + `Scaffolding` 自定义协议）、局域网广播发现（组播 `224.0.2.60:4445`）、`McPing`、`Natayark` NAID OAuth。
- **`ModWebServer` / `ModVideoBack`**：内嵌 HttpListener 提供 OAuth 回调（`/callback` 等）；WPF `MediaElement` 视频背景。二者均为上游遗留，非 Ayaka 新增。
- **artifact 与分发**：`artifact/` 含 `AyakaLauncher.exe`(23.7M)、WebView2 依赖 xml、`PCL/`（config.v1.yml、Log/Musics/Pictures）、`data/`（空）、`runtime`→符号链接指向 `runtime/`。`runtime/`、`artifact/` 均被 gitignore，非版本跟踪产物。

### 4.5 工程与测试

- **5 个 Roslyn SourceGenerator**（`IIncrementalGenerator`，netstandard2.0）：
  - `ConfigGenerator`：由 `[ConfigItem]/[AnyConfigItem]/[ConfigGroup]/[RegisterConfigEvent]` 生成配置项/组/作用域与 `ConfigService.g.cs`
  - `DependencyCollectorGenerator`：生成 `DependencyGroups.g.cs` 与注入方法
  - `EnvironmentInteropGenerator`：编译期读取 `PCL_*` 环境变量（`PCL_WRITE_SECRET` 开关）嵌入密钥
  - `LifecycleScopeGenerator`：生成 `ILifecycleService`（StartAsync/StopAsync、CLI 命令、DI）
  - `LifecycleServiceTypesGenerator`：生成按 `LifecycleState` 分类的服务类型索引
- **测试**：MSTest 4.3 + Test.Sdk 18.7，48 个 `[TestClass]`，覆盖 App/Encryption/Hash/IO/Minecraft/Network/Project/Validate 等；**无通用基类**（`BaseXTest` 实为 base32/36 转换测试）。
- **规范**：`.editorconfig`（IDE0028）、`LangVersion 14.0`、`Nullable enable`；贡献流程 fork → `feat/fix` 分支 → Angular commits（`<type>(scope): subject`）→ PR 指向 `dev`。

---

## 五、关键亮点

1. **编译期 SourceGenerator** 消除配置 / DI / 生命周期 / 密钥样板代码，密钥经生成代码注入，规避运行时暴露。
2. **WSL 内嵌 Docker** 实现「零依赖」本地化部署 Overleaf，用户无需自建 Docker/Node 环境。
3. **哈希寻址存储 + SQLite 元数据**的混合缓存设计；多源竞速下载（官方 / BMCLAPI）保障可用性。
4. **自研特性驱动 IoC + 生命周期框架**，替代传统 DI 容器，且与生成器深度协同。
5. **配置系统**多来源、加密、异步合并写、可拦截事件、旧格式迁移，能力完整。

---

## 六、关键风险与技术债

| 类别 | 问题 | 影响 |
| --- | --- | --- |
| **安全 / 凭据** | compose 硬编码占位密钥 `please-change-me...` 且 `ALLOW_PUBLIC_REGISTRATION=true`，无 SMTP/管理员 | 任意人可注册；生产不可用 |
| **安全 / 凭据** | `EncryptHelper` 密钥硬编码于源码 | 仅防明文，非强安全 |
| **破坏性运维** | 数据卷恢复 `rm -rf /var/lib/docker/volumes/...`，未见二次确认 | 误操作可致数据丢失 |
| **架构债** | PCL.Core 与主项目 Minecraft 双架构重叠，迁移未完成 | 认知与维护成本 |
| **架构债** | 巨类：`ModLaunch`(2796)、`ModDownloadLib`(4622)、`ModComp`(3829) 静态 + UI 强耦合 | 难测试、易回归 |
| **架构债** | 动画/颜色两代体系并存（`ModAnimation` vs `AnimationService`；`GrayProfile` vs `ToneProfile`） | 技术债累积 |
| **流程债** | CI 不执行测试、无覆盖率；版本号三处手工维护 | 质量与发布一致性无保障 |
| **脆弱实现** | `BackgroundPresenter` 反射 WPF 私有字段；`ToastNotification` 裸 WinRT COM；参数靠字符串拼接/Replace | 升级 WPF/运行时易断裂 |
| **完整性** | 非跟踪运行时产物（`runtime/`、`artifact/`）无哈希校验 | 分发篡改无感知 |
| **其它** | `ModModpack.cs:1420` patch 处理不严谨；`ApiLocation.cs:22` 未校验 scheme | 潜在安装/请求缺陷 |

---

## 七、后续可深入方向

1. **LaTeX 工作流**：深入 `runtime/docker-compose.yml` + `AyakaleafService`（服务部署）与 `WorkbenchWindow`（Overleaf 工作台），评估题库/习题册排版与编译工作流。
2. **启动链路**：`ModLaunch` 参数构建、Java 版本矩阵、多源下载。
3. **认证体系**：`ProfileService` 迁移与 `MicrosoftProvider` / `YggdrasilConnectProvider`。
4. **UI 重构**：主题（`ToneProfile`）与动画（`AnimationService`）迁移可行性。
5. **工程治理**：CI 启用测试、版本号单一来源、敏感凭据外部化。

---

*报告结束。如需针对任一模块做更深入的下钻探索，可指定模块继续。*
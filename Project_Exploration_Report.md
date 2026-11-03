# AyakaLauncher · 项目全景报告（MC 遗留清理后）

> 本报告反映 **2026-10 MC 遗留清理完成后** 的仓库状态。
> 此前版本记录的 Minecraft 双架构、启动/下载/联机模块、新闻模块等内容已在本轮及前序提交中移除。
> 清理验证：`dotnet build`（Debug）**0 错误**。

---

## 一、一句话定位

本仓库是 **PCL CE**（Plain Craft Launcher 2，上游 2.12.1）经 **Ayaka 定制**（本地 2.15.1）改造而成的 **AyakaLauncher**——一个「**本地个人 Overleaf / ShareLaTeX 一体化桌面管理器**」。

技术栈：**.NET 10 / C# 14 / WPF**。

产品本质：以 WPF 桌面外壳，通过 WSL 互操作在内嵌 Linux 发行版里用 Docker Compose 运维一整套 Overleaf Server Pro（MongoDB 副本集 + Redis + ShareLaTeX），并配套本地多用户账号体系与内嵌浏览器工作台。**原 Minecraft 启动器功能（游戏启动/下载/联机/实例管理/新闻/登录）已全部移除**，仅保留通用基础设施（配置、网络、下载、本地化、更新、UI 框架）。

---

## 二、技术栈与工程体系

| 项目 | 说明 |
| --- | --- |
| 目标框架 | `net10.0-windows` |
| 语言版本 | C# 14（`LangVersion 14.0`） |
| 平台矩阵 | `AnyCPU; x64; ARM64` |
| 构建配置 | `Debug; CI; Release; Beta` |
| 解决方案 | `Plain Craft Launcher 2.slnx`（新式 slnx 格式） |
| 代码规范 | `Nullable enable`、`AllowUnsafeBlocks`、`EnableDefaultItems=false`（手动 Include） |

### 解决方案组成（3 个项目）

- **`Plain Craft Launcher 2`** — WPF 应用主体（UI 与业务逻辑）
- **`PCL.Core`** — 内核库（`PCL.Core.*` 命名空间）
- **`PCL.Core.SourceGenerators`** — Roslyn 源代码生成器

> `PCL.Core.Test` 项目目录与 slnx 条目均已移除（测试从未被 CI 执行，属遗留空壳）。

### 版本管理

- `metadata.json`：名称 `AyakaLauncher`，`base 2.15.1`、`upstream 2.12.1`、`code 524`；许可清单 13 项（已剔除 6 项 MC 专属第三方许可）
- 主项目 `AssemblyVersion / FileVersion`：`2.15.1.0`
- ⚠️ 版本号分散在两处手工维护

### CI/CD（上游遗留，静默状态）

- `build-test.yml` → `reusable-build.yml`（矩阵 CI × x64/ARM64）
- `release-publish.yml`（git-cliff CHANGELOG + draft release + sigstore 签名）
- `mirrorchyan_*.yml` 镜像站上传
- ⚠️ CI 为上游遗留，指向原 PCL Community 仓库体系，**未针对 Ayaka 仓库重配**，实际不可用

---

## 三、架构分层

```
Plain Craft Launcher 2  (WPF UI 主体)
  ├─ Pages/
  │    ├─ PageLaunch/   服务台(PageServiceLeft/Right + PageLaunchRight)
  │    │                镜像管理(PageMirror) · 数据备份(PageBackup) · WorkbenchWindow(WebView2)
  │    ├─ PageSetup/    设置 10 页(Left/UI/Log/About/Update/Feedback/Language/Misc/...)
  │    ├─ PageSpeedLeft.xaml(.cs)      任务管理
  │    └─ PageHelpDetail.xaml(.cs)     帮助详情(Markdig)
  ├─ Controls/          自绘 My* 控件家族(MyButton/MyCard/MyHint/MyListItem/...)
  ├─ Modules/
  │    ├─ Base/         ModAnimation/ModBase/ModLoader/ModSetup/MyBitmap
  │    ├─ Event/        自定义主页事件(CustomEvent/EventType[15 值]/XamlEventSanitizer)
  │    ├─ Network/      下载器(FileDownloader/AdaptiveRangeDownloader)·HTTP(Requester/RequestSigning)
  │    ├─ Updates/      更新(Minio/MirrorChyan/随机源/版本公告)
  │    ├─ Users/        LocalUserUi(本地用户切换 UI)
  │    └─ ModMain/ModMusic/ModVideoBack/UI(Hint/Theme)
  └─ FormMain           四主 Tab: 服务台(Launch) / 镜像管理(Download) / 数据备份(Tools) / 设置(Setup)
                        隐藏页: 任务管理(TaskManager) / 帮助详情(HelpDetail)
        │  引用
        ▼
PCL.Core  (内核库)
  ├─ App/        IoC · 生命周期 · 配置(Config/ConfigEnums/States) · 事件 · CLI · 本地化(7 语言) · Secrets
  ├─ Ayakaleaf/  AyakaleafService · AyakaleafAccountService · WslcInterop
  ├─ Users/      本地用户体系(SQLite + PBKDF2 + API Server/Client)
  ├─ IO/         网络(NetworkService[Default/Cache]·HttpServer·DoH·代理) · 下载抽象 · 哈希与缓存存储
  ├─ Utils/      加密 · 哈希 · SemVer · OS · 线程 · 校验 · 通用正则(RegexPatterns)
  ├─ UI/         主题(ToneProfile) · 动画 · 特效 · 控件 · 媒体
  └─ Logging/    Channel 异步日志 + Microsoft.Extensions.Logging 适配
        │  SourceGenerator 编译期生成 配置/DI/生命周期/密钥注入 代码
        ▼
编译器生成代码 (ConfigService.g.cs / DependencyGroups.g.cs / ...)
```

---

## 四、核心模块要点

### 4.1 基础设施（PCL.Core）

- **特性驱动框架**：`[LifecycleService]` + `[ConfigItem]` + `[DependencyCollector]` + SourceGenerator 取代传统 DI 容器。
- **配置系统**：partial 类 + 特性声明，SourceGenerator 生成访问器；多来源 `ConfigSource`（Shared / SharedEncrypt / Local）；Provider 链 Json/Yaml → `ConfigStorage` → `EncryptedFileConfigStorage`(AES-GCM)；异步合并写、可拦截事件、旧格式迁移。
  - 当前配置组：`System` / `Network` / `Debug` / `Update` / `Download`（3 项：ThreadLimit、FileConnectionLimit、SpeedLimit+HttpMode）/ `Preference`（Theme/Hide 15 项/Homepage）/ `Ayakaleaf`。
  - 已删除：`Tool`、`Link`、`Launch`、`Instance`、`InstanceAuth` 组及 `Download.Comp`。
- **States 持久键**（精简后）：`CustomVariables`、`Hint.HomepageCommand`、`Game.SelectedFolder/Folders`、`UI.WindowHeight/Width`、`System` 组（LaunchUuid/ExportConfigPath/LauncherEula/StartupCount/Last*Version/AnnounceSolution）。
- **网络**：`NetworkService` 仅注册 `default` / `cache` 两个命名 HttpClient（MC 各命名客户端已删）；`HttpProxyManager`、DoH + 加权竞速保留。
- **Secrets**（保守清理后保留）：`TelemetryKey`、`NatayarkClientId/Secret`、`CommitHash`；已删 `MSOAuthClientId`、`CurseForgeAPIKey`、`LinkServers`。
- **日志/存储/工具**：Channel 异步日志；LiteDB 主存储 + SQLite 缓存（WAL）；Encryption（AES-GCM/ChaCha20）；HashStorage 哈希寻址；`RegexPatterns` 仅保留通用正则（10 项，MC 正则全部删除）。

### 4.2 UI 层

- **导航**：`FormMain.PageChange` 四主 Tab + 页面栈；`PageType` 6 值（Launch=0/Download=1/Setup=2/Tools=3/TaskManager=6/HelpDetail=9），`PageSubType` 8 值。
- **自定义控件**：主项目 `Controls/`（MyButton/MyCard/MyHint/MyIconButton/MyListItem 等）+ `PCL.Core/UI/Controls`（BlurBorder/WaterfallPanel/SvgIcon）。
- **主题**：`ThemeService` 基于 Lab/Lch 色彩空间（`ToneProfile`）动态生成资源，支持 4 套主题 + 深浅自动。
- **设置页**：`PageSetupUI` 含外观/字体/背景/音乐/Logo/功能隐藏卡片；功能隐藏网格保留 14 个有效复选框（Download/Settings/Tools/Ui/Language/Misc/Update/About/Feedback/Log/MainPage/SubSetup/SpecificFeature/FeatureHide）。

### 4.3 Ayakaleaf 服务栈（本仓库核心）

- **本地用户体系**：SQLite（`users.db`）+ **PBKDF2-HMAC-SHA256 600k 迭代**、每用户独立盐；首用户自动管理员；本地 API Server/Client（`LocalUserApiServer/Client`）；「服务」页「账户」按钮 → `LocalUserUi.ShowSwitchUserAsync`。
- **`AyakaleafService`**：`DeployAsync`（wsl --import `runtime.tar` + `docker load`）、`Start/StopAsync`（`docker compose up/down`）、`ProbeAsync`（HTTP 探活 `/login`）、数据卷 `Backup/Restore`。
- **`AyakaleafAccountService`**：每用户生成 `用户名@ayaka.local` + 24 位随机密码，DPAPI(CurrentUser) 加密存 `data/ayakaleaf/accounts/`；调用 Overleaf `/register`、`/login` 取 `overleaf.sid`。
- **`WorkbenchWindow`**：内嵌 WebView2 注入 sid 自动登录，会话失效自动重登 ≤2 次。
- **`WslcInterop`**：封装 `wsl.exe`（UTF-8 强制、超时强杀、`ToWslPath`），配合保活防 WSL 挂起。
- **runtime 部署栈**（`runtime/docker-compose.yml`）：`mongo:8.0`（副本集）+ `redis:7` + `sharelatex/server-pro`（8080:80）+ mongo-init；镜像 tar 约 2.4GB + `runtime.tar`(685M WSL rootfs)。

### 4.4 工程体系

- **5 个 Roslyn SourceGenerator**（netstandard2.0，`IIncrementalGenerator`）：ConfigGenerator / DependencyCollectorGenerator / EnvironmentInteropGenerator / LifecycleScopeGenerator / LifecycleServiceTypesGenerator。
- **本地化**：7 语言文件（zh-CN/zh-TW/en-US/en-GB/ja-JP/fr-FR/es-ES），以 zh-CN 为基准；ja-JP/fr-FR/es-ES 为部分翻译。
- **依赖**（清理后）：主项目 Dapper/DotNet.Glob/Downloader/Markdig.Wpf/Sqlite/WebView2/NAudio；PCL.Core 社区 MVVM/SharpZipLib/Sqlite/LiteDB/YamlDotNet/Ae.Dns/Polly/FluentValidation 等。已删：fNbt、Stun.Net、System.IdentityModel.Tokens.Jwt、protobuf-net。

---

## 五、本轮 MC 遗留清理记录（2026-10）

> 分支 `pcl-ce`，工作树未提交，供审查。

### 删除的 git 跟踪文件（52 个）

- **Resources 10 个 MC 二进制**：forge-installer.jar、java-wrapper.jar、legacyfix.jar、linkd.exe、log4j2-debug.xml、log4j2-legacy-debug.xml、lwjgl-unsafe-agent.jar、mcmod.buf、oauth-complete.html、transformer.jar
- **Blocks 9 个**：Cleanroom/Egg/Fabric/GoldBlock/GrassPath/LabyMod/NeoForge/OptiFabric/Quilt（保留 7 个被 PageSetupFeedback 引用的方块图）
- **Heads\ 全部 11 个、Skins\ 全部 10 个、Backgrounds\server_bg.png**
- **页面/控件**：PageToolsLeft(.xaml/.cs)、PageLocalUser.cs、PageHomepageNewsView(.xaml/.cs)、MyMsgAuthServer(.xaml/.cs)、MotdRenderer.xaml(.cs)
- **PCL.Core**：NewsViewModel.cs、Model/Homepage/News/ 三模型（NewsItem/ApiResponse/Result）

### 代码精简

- **主项目**：ModMain（删 frmToolsLeft/frmLocalUser 字段、AuthServerDefault/Presets、MyMsgBoxType.AuthServer/Login 及对应分支）；FormMain（删 GameWindowMode 迁移 shim、PageType/PageSubType 精简）；PageSetupUI（删 Motd 字体行、FeatureHide 网格重构为 14 复选框、删 6 个无引用死方法）；FileDownloader（GetHttpClient 简化）；RequestSigning（删 curseforge 签名块）；PageLaunchRight 欢迎卡改写。
- **PCL.Core**：Config.cs（6 组整删 + Download/Preference/Hide 精简）；States.cs 整体重写（删 Identifier/SetupVersion*/LaunchCount/SavedHomepage*/Tool/Link/Instance 组）；ConfigEnums.cs 删 6 枚举；NetworkService 删 7 个 MC 命名客户端与常量；Secrets 删 3 个 MC 密钥；RegexPatterns 删 36 个 MC 正则；Basics 注释修正。
- **事件系统**：EventType 删 6 个死值（LaunchGame/DailyFortune/ClearTrash/ImportModpack/InstallModpack/DownloadFile，15 值保留），对应中文映射移入 UnsupportedTypes（旧主页 XAML 出现时被 XamlEventSanitizer 优雅移除并提示）。
- **依赖/元数据**：metadata.json 删 6 项 MC 许可（LegacyFix/Java Launch Wrapper/Stun.Net/Mesa Loader/protobuf-net/LWJGL Unsafe Agent）；slnx 删 Test 条目；两个 csproj 删 fNbt/Stun.Net/Jwt/protobuf-net。
- **本地化**：4 个完整语言文件各删 19 个孤儿键（Setup.Ui.Font.Motd、FeatureHide.SubTools/SubInstance、FeatureHide.Item.Launch/GameManage/Toolbox/Edit/Export/Save/Screenshot/ResourcePack/Shader/Schematic/Server/InstanceSelect(.ToolTip)/ModUpdate(.ToolTip)），共 76 行，XML 校验通过。
- **空目录**：PCL.Core\Model、PCL.Core\ViewModel、Pages\PageHomepage、Pages\PageTools。

### 本地产物清理（释放约 2GB+，均未被 git 跟踪）

- 三个项目 obj/bin（6 目录）；dist\AyakaLauncher-Portable 目录 + 2.0GB zip；artifact\ 中过时 AyakaLauncher.exe 与 3 个 WebView2 xml。
- **保留**：artifact\PCL\（配置）、artifact\data\（用户数据）、artifact\runtime（junction → runtime\，部署必需）。

### 验证

- `dotnet build "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" -c Debug`：**0 错误**（946 个警告全部为存量可空性/风格警告，非本轮引入）。

---

## 六、关键风险与技术债

| 类别 | 问题 | 影响 |
| --- | --- |
| **安全 / 凭据** | **git remote 内嵌明文 GitHub PAT**（指向 BluesilveEmperor/Ayaka-Leaf-PCL） | 凭据泄露风险，需用户自行撤销并改 SSH |
| **安全 / 凭据** | compose 硬编码占位密钥 `please-change-me...`、`ALLOW_PUBLIC_REGISTRATION=true`，无 SMTP/管理员 | 任意人可注册；生产不可用 |
| **安全 / 凭据** | `EncryptHelper` 密钥硬编码于源码 | 仅防明文，非强安全 |
| **破坏性运维** | 数据卷恢复 `rm -rf /var/lib/docker/volumes/...` 无二次确认 | 误操作可致数据丢失 |
| **流程债** | CI 为上游遗留且从不执行测试；版本号两处手工维护 | 质量与发布一致性无保障 |
| **脆弱实现** | `BackgroundPresenter` 反射 WPF 私有字段；`ToastNotification` 裸 WinRT COM | 升级 WPF/运行时易断裂 |
| **架构债** | 动画两代并存（`ModAnimation` 实际使用 vs `AnimationService` 闲置） | 技术债累积 |
| **存量警告** | 946 个编译警告（CS86xx 可空性为主） | 建议逐步清偿 |
| **完整性** | 非跟踪运行时产物（`runtime/`）无哈希校验 | 分发篡改无感知 |

### 遗留候选（本轮范围外，供后续清理参考）

- 语言文件中早期提交孤儿化的键：`Setup.Left.Item.GameLink/Java/Launch/GameManage` 等
- 旧动画体系 `ModAnimation` 与新 `AnimationService` 二选一收敛
- CI 工作流整体重写或删除

---

## 七、后续可深入方向

1. **服务栈加固**：compose 密钥外部化、关闭公开注册、管理员与 SMTP 配置。
2. **工程治理**：重配 CI（构建+发布指向 Ayaka 仓库）、版本号单一来源、清理 946 个存量警告。
3. **安全整改**：撤销泄漏 PAT、remote 改 SSH；`EncryptHelper` 密钥外部化。
4. **备份可靠性**：数据卷恢复流程加二次确认与备份校验。

---

*报告结束。本轮清理工作树未提交，可 `git diff`/`git status` 审查后自行提交。*

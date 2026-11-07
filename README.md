**简体中文** | [English](README-EN.md) | [繁體中文](README-ZH_TW.md)

<div align="center">

<img src="Plain Craft Launcher 2/Images/icon.ico" alt="Logo" width="80" height="80">

# AyakaLauncher

**Ayakaleaf 一体化桌面端** · 基于 [PCL Community Edition](https://github.com/PCL-Community/PCL-CE) 二次开发

</div>

AyakaLauncher 是 [Ayakaleaf](https://github.com/ayaka-notes/ayakaleaf-pro)（Overleaf 服务端定制版）的一体化桌面客户端，基于 PCL Community Edition 的框架二次开发而来。

本项目已与 Minecraft 启动功能**完全解耦**：不再包含任何 MC 相关代码、页面与资源。

## ✨ 功能特性

- **本地用户系统** —— 注册 / 登录 / 多用户切换（SQLite + PBKDF2），每用户独立数据目录，启动登录门禁
- **服务台** —— 一键部署 / 启动 / 停止内置 Overleaf 服务（MongoDB 8.0 + Redis 7 + Overleaf；启用 Git-Bridge 时另含 git-bridge 容器），自动初始化 MongoDB 副本集，服务就绪自动探测
- **容器运行时** —— 基于 Windows WSL 3.0+ 内置容器能力（WSLC），**无需 Docker Desktop**，数据卷为独立 vhd 文件
- **工作台** —— 内嵌 WebView2 打开 Overleaf，自动开户 + 自动登录，免手动输入
- **公式联动** —— 左侧选中 LaTeX 公式代码，右侧 PDF 自动高亮对应位置并滚动定位（复用 Overleaf 自带 SyncTeX）
- **符号面板拓展** —— 内置符号面板拓展为 LaTeX-Workshop 全量符号集：10 个中文分类、760+ 符号（在 Overleaf 原生符号之上合并增量），支持按名称 / 关键字 / Unicode 搜索，服务启动时自动生效，无需手动操作
- **镜像管理** —— 内置镜像 tar.gz（`runtime/images`），部署时自动加载，重复加载幂等
- **备份恢复** —— 数据卷即 vhd 文件，页面内一键快照 / 恢复
- **局域网共享（团队协作）** —— 服务台实时显示「队友访问地址」，同一局域网的队友用浏览器即可访问、注册账号并实时协同编辑；网络切换后地址自动刷新，自动排除 VPN / TUN 虚拟网卡（Mihomo / Clash 等），详见「团队协作」章节
- **远程协作** —— 基于 EasyTier 的异地组网：房主一键生成邀请码开放本机 Overleaf 服务，异地队友输码自动加入虚拟网络并转发端口，无需公网 IP 或端口映射，详见「远程协作」章节
- **账号数据隔离** —— 账号库、凭据与用户数据全部随启动器目录存放，整个目录拷走即成便携版，多个副本互不串号
- **Pro 功能对齐** —— 设置 → Ayakaleaf：Git-Bridge（git 操作 + MCP Server）、AI 助手、模板画廊、GitHub / GitLab / Gitea 双向同步、Zotero / Mendeley 文献引用、注册策略（开放 / 域名白名单 / 关闭）、reCAPTCHA、泄露密码检查、品牌化、高级环境变量（SSO 等任意透传）；全部开关默认关闭，与 ayakaleaf-pro 行为对齐
- **多语言界面** —— 镜像内置 18 种界面语言按需启用（英语固定主端口，其余语言依次 +1），工作台内与页脚语言选择器均可切换
- **主题色** —— 天空蓝、龙猫蓝、死机蓝、篝火橙（Campfire 标志色 #E2680C）四款内置主题色随选随切，浅色 / 深色模式均适配
- **管理员与账号联动** —— 首个本地用户自动开通服务端管理员；账户中心直达管理面板、一键重置 Ayakaleaf 密码；删除本地用户时同步清理服务端账号
- **全量备份** —— 全部数据卷 + 账号凭据 + 本地用户库 + 启动器配置一键打包为单个 zip，一键恢复
- **镜像版本管理** —— 镜像页显示当前使用镜像与版本；导入新版本镜像包后重启服务，自动以新镜像重建容器（数据卷跨版本保留）

## 📐 编辑器联动

在工作台里用鼠标选中一段公式代码，右侧 PDF 会自动跳到公式渲染位置并高亮，不必再点工具栏的 Sync to PDF：

- 支持 `$…$`、`$$…$$`、`\(…\)`、`\[…\]`，以及 `equation`、`align`、`gather`、`multline`、`split`、`cases`、`matrix` 等 `\begin{}` 环境
- 只对公式选区生效；选中普通文字、复制粘贴不受影响
- 右侧需已成功编译出 PDF 才会触发（未编译时静默跳过，不弹错误提示）
- 高亮框来自 SyncTeX 记录的位置，多行环境可能只覆盖该处首行

## 🤝 团队协作与远程协作

按队友与你的网络关系选择协作方式：

| 场景 | 功能 | 要求 |
|---|---|---|
| 同一局域网（宿舍 / 实验室 / 办公室内网） | 团队协作（局域网共享） | 队友仅需浏览器，无需安装任何软件 |
| 异地 / 不同网络 | 远程协作（EasyTier 虚拟组网） | 双方都能访问互联网；队友需运行本启动器 |

### 🏠 团队协作（局域网共享）

同一局域网的队友用浏览器即可访问你的 Overleaf 服务、注册账号并实时协同编辑。

**房主（你）**

1. 在「服务」页启动服务
2. 勾选「网络与协作 → 允许局域网访问（团队协作）」，页面随即显示「队友访问地址」（如 `http://192.168.1.100:8080`；启用简体中文界面时会一并显示中文端口地址）
3. 重启服务使开关生效（该开关修改后需重启服务）
4. 把「队友访问地址」发给同一局域网的队友

**队友**

1. 在同一局域网的任意设备（电脑 / 平板 / 手机均可）用浏览器打开房主分享的地址
2. 首次访问点击注册创建账号（或登录已有账号）
3. 之后即可创建 / 加入项目，与房主及其他队友实时协同编辑

**说明**

- 地址取房主电脑的局域网 IP，网络切换（Wi-Fi ↔ 热点等）后自动刷新；已自动排除 VPN / TUN 虚拟网卡（Mihomo / Clash 等），不会误显示代理网卡 IP
- 关闭开关并重启服务后，服务恢复为仅本机可访问
- 队友共享房主的服务与项目空间，人数上限取决于房主电脑性能

### 🌐 远程协作（异地组网）

异地队友无需公网 IP 或端口映射，凭邀请码即可加入你的 Overleaf 服务一起协作。虚拟网络基于 [EasyTier](https://github.com/EasyTier/EasyTier)（Apache-2.0）；首次使用时自动从官方发布渠道下载协作组件，无需手动安装。

**房主**

1. 在「服务」页启动服务后进入「协作」页，点击「开启协作」
2. 点击「复制邀请码」，把生成的邀请码（格式 `A/XXXX-XXXX-XXXX-XXXX`）发给队友
3. 等待队友加入；页面实时显示已连接的公共中转节点数，成员列表每 2 秒自动刷新，逐个显示各成员的连接方式（直连 / 中继）与延迟

**队友**

1. 打开本启动器进入「协作」页，粘贴邀请码（直接粘贴包含邀请码的整段文本也可以，会自动识别其中生效的邀请码），点击「加入协作」
2. 加入过程全自动：连接公共节点 → 寻找房主 → 按房主启用的界面语言逐个建立本地端口转发（本地沿用房主同端口号，被占用时自动改用空闲端口）
3. 加入成功后页面列出各语言的访问地址（如 `简体中文：http://localhost:8081`）；点击「打开工作台」进入房主的服务（可在房主已启用的界面语言之间切换），也可以直接用浏览器打开列出的地址
4. 首次访问需在房主的服务中注册或登录账号，之后即可与房主实时协同编辑

**双方通用**

- 协作会话切页不中断；「停止协作」随时可断开，启动器退出时自动清理网络组件
- 房主停止协作后，所有队友立即失去访问
- 若长时间停留在「正在连接公共节点…」，通常是网络受限打不通内置节点，可配置自定义中继节点（见下）

**隐私与安全**

- 流量端到端加密，公共中转节点只转发密文、无法解密内容；能直连时优先 P2P 直连，中继仅作兜底
- 房主侧白名单仅开放 Overleaf 服务端口，本机其他端口对队友不可达
- 邀请码即网络密钥（16 位、含校验位防输错），请勿公开分享；若有泄露，房主停止协作后重新「开启协作」会生成新邀请码，旧邀请码随之作废
- 支持「隐藏 IP」（双方互不可见虚拟 IP）、「仅直连」（禁用中继兜底）等高级选项（`AyakaleafCollab*` 配置项）

**网络节点**

默认自动连接 5 个社区公共中转节点；网络受限时可配置自定义节点（`tcp://` / `udp://` / `wss://` / `https://` 格式，多个以逗号分隔，追加在内置节点之后）。

## 💻 支持平台

| 操作系统 | 支持情况 | 环境要求 |
|---|---|---|
| Windows 10 21H2+ / Windows 11 (x64 / ARM64) | ✅ 完整支持 | [.NET 10 Desktop Runtime](https://get.dot.net/10)（架构与主程序一致）+ WSL 3.0+ |
| Windows 10 1809 - 21H2 | ⚠️ 需手动升级 WSL 至 3.0+ | 同上 |
| Windows 7 / 8 / 8.1 | ❌ 不支持 | / |
| macOS / Linux | ❌ 不支持 | WPF、WebView2 与 WSLC 容器均为 Windows 专属 |

## 🚀 快速开始

1. 从 [Releases](https://github.com/BluesilveEmperor/Ayaka-Leaf-PCL/releases) 下载主程序 `AyakaLauncher_Release_x64.exe`（ARM64 设备选 `AyakaLauncher_Release_ARM64.exe`）；运行时包 `runtime.7z` 从下文「主下载源（网盘）」获取；主程序另有备用下载，见下文「备用下载：主程序」
2. 把 exe 与运行时包放到同一目录，解压得到 `runtime\images\`（约 4.2GB，详见下节）
3. 安装 [.NET 10 Desktop Runtime](https://get.dot.net/10)（架构与主程序一致）
4. 启用 WSL：管理员 PowerShell 执行 `wsl --install --no-distribution`，完成后执行 `wsl --update`
5. 启动 `AyakaLauncher_Release_<平台>.exe` → 注册本地用户并登录
6. 服务台页面依次点击：**开始部署** → **启动服务** → **打开工作台**（服务地址 `http://localhost:8080`，端口可配置）

> ⚠️ `runtime\` 是容器镜像运行时，必须与 exe 保持**同级、不可拆分或改名**。exe 本身不含镜像，缺少 `runtime\images\` 时无法部署服务。

## ⚠️ 功能限制

- **邮件不可用**：本地实例未配置 SMTP，确认邮件 / 密码找回邮件无法发送（注册已自动跳过邮箱确认）；注册策略选「域名白名单」时激活邮件同样无法送达，请知悉后再选用
- **沙箱编译与 Pandoc 导入**：WSL 容器环境无法运行同级（sibling）容器，TeX Live 全量沙箱编译与 Pandoc 文档导入不可用，设置页中置灰展示
- **OAuth 回调可达性**：GitHub / GitLab / Gitea / Zotero / Mendeley 授权回调要求访问者能访问站点地址；局域网模式使用本机 IP，公网不可达时授权可能失败
- **凭据安全**：AI 密钥、OAuth Client Secret 等敏感配置保存在本机加密配置源中，容器重建指纹仅取 SHA256 哈希，不写日志

## 📦 内嵌运行时（runtime）

`runtime\images\` 存放 WSLC 容器镜像包，部署时由应用自动加载（幂等，重复加载无副作用），结构如下：

```
AyakaLauncher.exe
runtime\
└─ images\
   ├─ sharelatex-server-pro.tar.gz   # Overleaf 服务端定制镜像（约 3.46GB，自包含 TeX Live 2026 完整宏包集）
   ├─ git-bridge.tar.gz              # Git-Bridge / MCP 服务镜像（约 387MB，必选，Git 操作与三方同步依赖）
   ├─ mongo-8.0.tar.gz               # MongoDB 8.0（约 300MB）
   └─ redis-7.tar.gz                 # Redis 7（约 42MB）
```

四个包合计约 4.2GB，**不入 git 仓库**（`.gitignore` 已排除），正式分发走下文「主下载源（网盘）」中的 `runtime.7z`（Release 不再附带运行时包）。**v2.0.0 版本**分发的 `sharelatex-server-pro.tar.gz` **自包含 TeX Live 2026 收录的完整宏包集**（scheme-full 全量安装，约 4900 个宏包，texlive 目录增至约 7–8GB、含文档，已覆盖 ctex / xeCJK 中文支持与 IEEEtran / revtex / elsarticle 等期刊模板；规模与构建参数以 `tools/runtime/texlive-full/Dockerfile` 为准）；镜像内已预装全部宏包，部署后无需联网用 `tlmgr` 补装，开箱即可编译中文论文。需要自行重建该镜像时，仓库提供两种 Dockerfile：`tools/runtime/texlive-full/Dockerfile`（scheme-full 全量）与 `tools/runtime/texlive-packages/Dockerfile`（精选集合，体积更小），构建后 `wslc tag` + `wslc save` 导出覆盖即可。获取方式任选其一：

| 方式 | 适用镜像 | 说明 |
|---|---|---|
| 网盘主下载源 `runtime.7z` | 全部 | 从下文「主下载源（网盘）」任选一个分享链接下载，解压到 exe 同级即为 `runtime\images\` |
| `wslc` 自助导出 | mongo / redis | 标准镜像：`wslc pull mongo:8.0` 后 `wslc save -o runtime\images\mongo-8.0.tar.gz mongo:8.0`（redis 同理） |
| ayakaleaf-pro 构建 | sharelatex / git-bridge 定制镜像 | 从 [ayakaleaf-pro](https://github.com/ayaka-notes/ayakaleaf-pro) 构建镜像后 `wslc save` 导出 |

**放置位置**：解压后的 `runtime.7z` 或整合包内，均放在 exe 同级；源码开发放在**仓库根** `runtime\images\`（构建后自动挂到输出目录，见下节）。

## 🛠️ 本地开发

### 前置要求

| 依赖 | 版本 | 安装方式 |
|---|---|---|
| .NET SDK | 10 | [get.dot.net/10](https://get.dot.net/10) |
| WSL | 3.0+ | 管理员 PowerShell：`wsl --install --no-distribution`，再 `wsl --update` |
| 容器运行时 | WSLC | 随 WSL 3.0+ 内置，**无需 Docker Desktop** |

### 搭建步骤

```powershell
# 1. 克隆
git clone https://github.com/BluesilveEmperor/Ayaka-Leaf-PCL.git
cd Ayaka-Leaf-PCL

# 2. 摆放 runtime：把上节获取的 4 个镜像包放到仓库根
#    runtime\images\sharelatex-server-pro.tar.gz
#    runtime\images\git-bridge.tar.gz
#    runtime\images\mongo-8.0.tar.gz
#    runtime\images\redis-7.tar.gz

# 3. 构建并运行
dotnet run --project "Plain Craft Launcher 2"
```

也可以用 Visual Studio / Rider / VS Code 打开解决方案直接 F5。

**runtime junction 机制**：仓库根 `runtime\` 是镜像包的真实存放处；每次构建后，csproj 中的 `LinkAyakaleafRuntime` Target 会把输出目录 `bin\<配置>\net10.0-windows\runtime` 以目录联接（junction）指向它，`dotnet publish` 输出目录由 `LinkAyakaleafRuntimeOnPublish` 同样处理——避免每次构建复制 4.2GB，应用运行时从 exe 旁的 `runtime\images` 读取。对仓库根 `runtime\` 的改动在下次运行时即时生效。

**首次运行流程**：注册本地用户并登录 → 服务台点击 **开始部署**（自动加载镜像、初始化 MongoDB 副本集）→ **启动服务** → **打开工作台**。可用 `wslc images` 确认 4 个镜像、`wslc list` 确认 3 个容器（mongo / redis / overleaf；启用 Git-Bridge 时另见 git-bridge 容器）均已就绪。

## 📦 发布与分发（Release）

主程序统一从 [GitHub Releases](https://github.com/BluesilveEmperor/Ayaka-Leaf-PCL/releases) 下载，运行时包 `runtime.7z` 从下方「主下载源（网盘）」获取；**Release 仅附带主程序，不再附带运行时包**。附件如下：

| 附件 | 说明 |
|---|---|
| `AyakaLauncher_Release_x64.exe` | x64 主程序，Framework-dependent 单文件，需 [.NET 10 Desktop Runtime](https://get.dot.net/10) |
| `AyakaLauncher_Release_ARM64.exe` | ARM64 主程序，Framework-dependent 单文件，需求同上 |

> 📌 **v2.0.0 运行时说明**：本版本配套的 `sharelatex-server-pro.tar.gz` 已**自包含 TeX Live 2026 收录的全部宏包**（scheme-full 全量），部署后无需联网补装任何宏包；中文支持（ctex / xeCJK）与各期刊模板同样已内置。

### 主下载源（网盘）

运行时包 `runtime.7z` 通过网盘分享分发，以下**全部为主下载源**，任选其一即可；后续会陆续补充其他网盘的分享链接。

| 网盘 | 分享链接 | 说明 |
|---|---|---|
| 阿里云盘 | [阿里云盘分享链接](https://www.alipan.com/s/NYaZyrvbRmX) | 由维护者后续上传 `runtime.7z`，以分享页实际文件为准 |
| 其他网盘 | 待补充 | 后续会补上更多网盘分享链接 |

> 📁 **按版本分文件夹**：网盘分享页按启动器版本建立文件夹，请先进入对应版本目录（当前 `v2.0.0`）再下载 `runtime.7z`。主程序的备用网盘分享同样按版本分文件夹，两者必须取自**同一版本目录**，不要跨版本混用。

若暂时没在网盘里找到，那多半是网络有点慢还没传完，或者作者有点懒还没上传——稍后再来看看。用支持 7z 的解压工具把 `runtime.7z` 解压到 exe 同级目录，得到 `runtime\images\`；随后按下节「部署步骤」继续。

### 备用下载：主程序（阿里云盘）

GitHub Releases 是主程序的**主渠道**；下载不便时，可从以下备用分享获取 `AyakaLauncher_Release_x64.exe` / `AyakaLauncher_Release_ARM64.exe`（以分享页实际文件为准）：

- [阿里云盘分享链接](https://www.alipan.com/s/XBcaUK75hrU)

> 📁 分享页**按启动器版本分文件夹**存放，请先进入对应版本目录（当前 `v2.0.0`）再取 exe，并与上文「主下载源（网盘）」的 `runtime.7z` 保持同一版本。

> ⚠️ 这里只提供**主程序 exe**。运行时包 `runtime.7z` 是另一份资源，仍须从上文「主下载源（网盘）」获取；两个链接用途不同，不能互相替代。

### 部署步骤

1. 把 `AyakaLauncher_Release_<平台>.exe` 与运行时包 `runtime.7z` 放到同一目录
2. 在该目录解压运行时包，得到 `runtime\images\`（含 `sharelatex-server-pro`、`git-bridge`、`mongo-8.0`、`redis-7` 四个镜像包）
3. 运行 exe，按「快速开始」完成首次部署

### 运行时包的分发方式

运行时镜像不入库，干净的 CI 构建机无法凭空生成运行时包。因此**运行时包统一由网盘主下载源 `runtime.7z` 分发，Release 不再附带**。本机执行 `pwsh release.ps1` / `python release.py` 仍会在 `release\` 下生成 `runtime.zip`，供本机整合包与离线分发使用，但不会上传到 Release。

### 为什么 runtime 不进入代码仓库

单个镜像包最大约 3.46GB（sharelatex-server-pro，自包含 TeX Live 2026 完整宏包集），远超 GitHub 普通仓库 100MB 的单文件限制，因此运行时镜像不打入 git，改由网盘主下载源 `runtime.7z` 分发（Release 不再附带运行时包）。仓库内的 `runtime\` 目录仅供本机开发使用，已在 `.gitignore` 中忽略。

### 本地打包

打包脚本有 PowerShell 与 Python 两个等价实现（`release.ps1` / `release.py`），任选其一：

```powershell
# PowerShell 版：x64 与 ARM64 分别构建
pwsh -ExecutionPolicy Bypass -File release.ps1 -Configuration Release -Platform x64
pwsh -ExecutionPolicy Bypass -File release.ps1 -Configuration Release -Platform ARM64
```

```bash
# Python 版（需要 Python 3.9+，仅标准库）：x64 与 ARM64
python release.py --configuration Release --platform x64
python release.py --configuration Release --platform ARM64
```

```powershell
# 可选：同时产出整合包（Python 版把 -WithBundle 换成 --with-bundle）
pwsh -ExecutionPolicy Bypass -File release.ps1 -Configuration Release -Platform x64 -WithBundle
pwsh -ExecutionPolicy Bypass -File release.ps1 -Configuration Release -Platform ARM64 -WithBundle
```

> `runtime.zip` 与平台无关，ARM64 重跑仅重新打包覆盖，内容不变；Release 不再附带运行时包，对外统一由网盘 `runtime.7z` 分发。

打包前脚本会先检查项目根是否存在 `runtime\` 目录：若不存在，直接提示先从网盘下载 `runtime.7z`、解压到项目根目录后再运行脚本；若存在，则继续校验 `runtime\images\` 下的 `sharelatex-server-pro.tar.gz`、`git-bridge.tar.gz`、`mongo-8.0.tar.gz`、`redis-7.tar.gz` 四个镜像是否齐备，缺失则报错并提示补齐（可从网盘 `runtime.7z` 解压恢复）。

| 产物 | 触发条件 | 说明 |
|---|---|---|
| `release\AyakaLauncher_<配置>_<平台>.exe` | 默认 | 主程序（x64 / ARM64 分别构建），命名与 Release 附件一致（不含版本号） |
| `release\runtime.zip` | 默认 | 内嵌运行时镜像包，约 4.2GB，仅供本机整合包使用，不再随 Release 分发 |
| `release\AyakaLauncher_<版本>_<配置>_<平台>.zip` | 加 `-WithBundle` | exe + runtime 整合包，约 4.2GB，适合离线分发 |
| `dist\AyakaLauncher.exe` | `dist\` 存在时自动同步 | 本机日常运行目录（runtime 以 junction 挂载）随发布刷新；启动器正在运行时仅告警跳过 |

打包仅用各语言标准库（PowerShell 的 `System.IO.Compression` / Python 的 `zipfile`），无第三方依赖。本机日常运行无需打包——`dist\` 与构建输出目录的 runtime 均已通过 junction 挂好。

## 🔒 许可证

- `Plain Craft Launcher 2/` 使用 [上游自定义许可证](Plain%20Craft%20Launcher%202/LICENCE)
- 其余目录使用 [Apache License 2.0](LICENSE)

## 🙏 致谢

- [PCL Community Edition](https://github.com/PCL-Community/PCL-CE) —— UI 框架与工程基础
- [Meloong-Git/PCL](https://github.com/Meloong-Git/PCL) —— 上游原项目
- [Overleaf](https://github.com/overleaf/overleaf) —— 内嵌的协作 LaTeX 服务
- [Ayakaleaf](https://github.com/ayaka-notes/ayakaleaf-pro) —— 上游 Overleaf 定制服务端，本项目内嵌运行时与工作台的服务端来源

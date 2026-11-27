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
- **文档导入（Pandoc）** —— 镜像内置 Pandoc 3.x，工作台直接导入 Word / Markdown 文档；导入失败给出中文原因提示，超大文档自动拦截
- **公式联动** —— 左侧选中 LaTeX 公式代码，右侧 PDF 自动高亮对应位置并滚动定位（复用 Overleaf 自带 SyncTeX）
- **PDF 反向同步** —— 在 PDF 上单击任意位置，编辑器自动跳到对应源码行并闪烁高亮（反向 SyncTeX 定位）；跨文件定位失败时自动回退官方双击流程
- **错误汉化** —— 编译错误日志附中文解释（点错误跳源码行的官方交互保留）；Word / Markdown 导入失败同样给出中文原因
- **首启环境引导** —— 环境自检页列出 WSL / wslc / .NET 三项运行环境状态，缺失项提供一键修复指引
- **镜像变体** —— Overleaf 主镜像双变体按需部署：完整版（约 3.5GB，精选宏包集）与「中文常用版」精简镜像（约 1.7GB，中文排版 + 常用宏包保留集）
- **宏包面板** —— 页面内搜索 / 安装 / 卸载 TeX Live 宏包（tlmgr 在线补装）；精简镜像缺宏包时在线补装即可，形成闭环
- **服务状态页** —— 容器运行状态、资源占用、最近日志一页可见，支持一键重启服务
- **服务端账号管理** —— 页面内列出服务端全部账号：搜索 / 停用 / 恢复 / 重置密码 / 授予或撤销管理员
- **中文模板库** —— 内置中学试卷、本科毕业论文、竞赛报告、国内期刊四类中文模板，一键新建项目即可用 XeLaTeX 直接编译（随镜像内置中文字体）
- **本地文件夹同步** —— 项目与本地文件夹双向自动同步：本地编辑器保存自动推送、工作台修改自动拉回；同一文件双端都改过时双方版本均保留（冲突副本 + 显式提示），绝不静默覆盖
- **轻量协作客户端** —— AyakaCollabLite（≤ 50MB、免安装、零运行时依赖），异地队友无需完整启动器即可凭邀请码加入协作
- **符号面板拓展** —— 内置符号面板拓展为 LaTeX-Workshop 全量符号集：10 个中文分类、760+ 符号（在 Overleaf 原生符号之上合并增量），支持按名称 / 关键字 / Unicode 搜索，服务启动时自动生效，无需手动操作
- **镜像管理** —— 内置镜像 tar.gz（`runtime/images`，双变体主镜像 + 清单），部署时按所选变体自动加载，重复加载幂等
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
- **在线更新** —— 基于 GitHub Releases 的自动更新通道：启动时按设置检查新版本，下载后 SHA256 校验并自动替换程序文件，设置中可关闭；详见「在线更新」章节

## 📐 编辑器联动

在工作台里用鼠标选中一段公式代码，右侧 PDF 会自动跳到公式渲染位置并高亮，不必再点工具栏的 Sync to PDF：

- 支持 `$…$`、`$$…$$`、`\(…\)`、`\[…\]`，以及 `equation`、`align`、`gather`、`multline`、`split`、`cases`、`matrix` 等 `\begin{}` 环境
- 只对公式选区生效；选中普通文字、复制粘贴不受影响
- 右侧需已成功编译出 PDF 才会触发（未编译时静默跳过，不弹错误提示）
- 高亮框来自 SyncTeX 记录的位置，并经过本地几何消歧与墨迹扫描校正，多行环境、多栏排版下也能框住实际内容

## 🔄 在线更新

从 2.1.0 起内置基于 GitHub Releases 的在线更新通道：

- **通道行为**：启动时按「设置 → 启动器更新」中选择的更新方式检查新版本（自动下载并安装 / 自动下载并提示更新 / 仅提示有新版本 / 不自动检查），下载完成后进行 SHA256 校验，再自动替换程序文件；也可在该页点击「检查更新」手动触发
- **总开关**：取消勾选「启用更新通道」后，启动器完全不检查、不下载更新
- **下载加速**：优先经 ghfast.top / gh-proxy.com 加速下载，加速站点不可用时自动回退直连
- **独立版本线**：版本号为本项目独立维护，与上游 PCL2-CE 的版本号无关
- **存量版本**：2.0.0 及之前的版本未启用更新通道，需手动下载一次新版 exe 替换，此后即可在线更新；已有账号数据与配置不受升级影响

## 🤝 团队协作与远程协作

按队友与你的网络关系选择协作方式：

| 场景 | 功能 | 要求 |
|---|---|---|
| 同一局域网（宿舍 / 实验室 / 办公室内网） | 团队协作（局域网共享） | 队友仅需浏览器，无需安装任何软件 |
| 异地 / 不同网络 | 远程协作（EasyTier 虚拟组网） | 双方都能访问互联网；队友运行完整启动器或轻量客户端 AyakaCollabLite |

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
4. 队友加入后会自动创建协作账号（在「账号中心」带「协作」标记、显示队友的显示名），可像普通账号一样停用、重置密码或删除

**队友**

1. 打开本启动器进入「协作」页，粘贴邀请码（直接粘贴包含邀请码的整段文本也可以，会自动识别其中生效的邀请码），可选填写显示名（默认本机用户名，用于生成协作账号），点击「加入协作」
2. 加入过程全自动：连接公共节点 → 寻找房主 → 按房主启用的界面语言逐个建立本地端口转发（本地沿用房主同端口号，被占用时自动改用空闲端口）
3. 加入成功后页面列出各语言的访问地址（如 `简体中文：http://localhost:8081`）；点击「打开工作台」进入房主的服务（可在房主已启用的界面语言之间切换），也可以直接用浏览器打开列出的地址
4. 协作账号自动配对：加入后启动器自动创建协作账号并完成登录（免注册），「打开工作台」即以登录态进入，可与房主实时协同编辑；同一邀请码重复加入会复用同一账号，不会重复开户。配对失败时转发仍可用，页面会给出中文原因并可一键重试

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

### 🪶 轻量协作客户端（AyakaCollabLite）

只需加入他人协作、不需要本地服务栈的队友，可改用轻量客户端 AyakaCollabLite：

- **体积 ≤ 50MB、免安装、零运行时依赖**：解压即用，双击 `CollabLite.bat` 启动（PowerShell 5.1 WinForms 图形界面，Windows 10/11 自带环境即可运行）
- 功能聚焦异地协作：粘贴房主邀请码（可选填显示名）→ 自动组网（内置 EasyTier v2.6.4，与主程序版本一致保证协议兼容）→ 按房主启用的界面语言建立本地端口转发 → 自动创建协作账号并把邮箱与密码显示在窗口中（各带「复制」按钮，在浏览器登录页分别粘贴一次即可，此后浏览器保持登录）→ 浏览器打开转发地址协同编辑
- 与完整启动器的协作功能互相兼容：房主用完整启动器「开启协作」，队友任选完整启动器「加入协作」或轻量客户端加入，组网参数与协作账号规则一致
- 安装包 `AyakaCollabLite_<版本>_x64.zip` 随主程序 Release 一并分发，附 `.sha256` 校验文件

## 📁 本地文件夹同步（双向）

把 Ayakaleaf 项目与本地文件夹建立双向同步，实现「本地编辑 + Overleaf 编译」混合工作流：

1. 在工作台打开要绑定的项目，点击窗口顶部的「⟳ 本地同步」按钮
2. 在弹出的管理窗口中点击「绑定当前项目到文件夹…」，选择本地文件夹（建议空文件夹）并确认
3. 绑定完成后首轮同步自动把项目内容拉到本地；此后双向自动同步：
   - **本地 → 工作台**：本地编辑器（VS Code / TeXstudio / 记事本等）保存文件后自动推送到服务端，已连接的协作者会实时看到更新
   - **工作台 → 本地**：工作台中的修改（含协作者编辑）自动拉回本地文件夹
   - 本地监听为准实时（约 1.5 秒防抖），远端轮询周期 30 秒；服务未启动时自动跳过，服务恢复后自动续传

**冲突规则（绝不静默覆盖）**：同一文件双端都修改且内容不同时，以远端为当前版本落盘，本地修改保存为冲突副本 `名称.conflict-YYYYMMDD-HHMMSS.扩展名`，并弹出明确提示——双方版本都保留，由你决定保留哪份。

**绑定管理**：管理窗口内可对每个绑定执行「立即同步」「暂停 / 恢复」「打开文件夹」「解除绑定」；解除绑定只停止自动同步，本地文件与清单均保留。绑定配置随启动器持久化，重启后自动恢复监听。

**v1 范围限制**（后续版本演进）：

- 删除 / 重命名不自动传播：本地删除的文件远端保留，远端删除的文件本地保留
- 已存在二进制文件（如图片）的修改不支持推送（请在工作台内替换），新增二进制文件可正常上传
- 编译产物与临时文件（`.aux` / `.log` / `.synctex.gz` 等）自动排除，不会同步到工作台

## 💻 支持平台

| 操作系统 | 支持情况 | 环境要求 |
|---|---|---|
| Windows 10 21H2+ / Windows 11 (x64 / ARM64) | ✅ 完整支持 | [.NET 10 Desktop Runtime](https://get.dot.net/10)（架构与主程序一致）+ WSL 3.0+ |
| Windows 10 1809 - 21H2 | ⚠️ 需手动升级 WSL 至 3.0+ | 同上 |
| Windows 7 / 8 / 8.1 | ❌ 不支持 | / |
| macOS / Linux | ❌ 不支持 | WPF、WebView2 与 WSLC 容器均为 Windows 专属 |

## 🚀 快速开始

1. 从 [Releases](https://github.com/BluesilveEmperor/Ayaka-Leaf-PCL/releases) 下载主程序 `AyakaLauncher_Release_x64.exe`（ARM64 设备选 `AyakaLauncher_Release_ARM64.exe`）；运行时包 `runtime.7z` 从下文「主下载源（网盘）」获取；主程序另有备用下载，见下文「备用下载：主程序」
2. 把 exe 与运行时包放到同一目录，解压得到 `runtime\images\`（约 6.3GB，含双变体主镜像，详见下节）
3. 安装 [.NET 10 Desktop Runtime](https://get.dot.net/10)（架构与主程序一致）
4. 启用 WSL：管理员 PowerShell 执行 `wsl --install --no-distribution`，完成后执行 `wsl --update`
5. 启动 `AyakaLauncher_Release_<平台>.exe` → 注册本地用户并登录
6. 服务台页面依次点击：**开始部署** → **启动服务** → **打开工作台**（服务地址 `http://localhost:8080`，端口可配置）

> ⚠️ `runtime\` 是容器镜像运行时，必须与 exe 保持**同级、不可拆分或改名**。exe 本身不含镜像，缺少 `runtime\images\` 时无法部署服务。

## ⚠️ 功能限制

- **邮件不可用**：本地实例未配置 SMTP，确认邮件 / 密码找回邮件无法发送（注册已自动跳过邮箱确认）；注册策略选「域名白名单」时激活邮件同样无法送达，请知悉后再选用
- **沙箱编译**：WSL 容器环境无法运行同级（sibling）容器，TeX Live 全量沙箱编译不可用，设置页中置灰展示；编译仍在 Ayakaleaf 容器内进行，不受影响。**Pandoc 文档导入**已随新版运行时镜像恢复（镜像内置 Pandoc 3.x，Word / Markdown 导入可用），设置页显示实际检测结果
- **OAuth 回调可达性**：GitHub / GitLab / Gitea / Zotero / Mendeley 授权回调要求访问者能访问站点地址；局域网模式使用本机 IP，公网不可达时授权可能失败
- **凭据安全**：AI 密钥、OAuth Client Secret 等敏感配置保存在本机加密配置源中，容器重建指纹仅取 SHA256 哈希，不写日志

## 📦 内嵌运行时（runtime）

`runtime\images\` 存放 WSLC 容器镜像包，部署时由应用自动加载（幂等，重复加载无副作用），结构如下：

```
AyakaLauncher.exe
runtime\
└─ images\
   ├─ sharelatex-server-pro.tar.gz       # Overleaf 完整版镜像（约 3.5GB，内置 Pandoc 3.x + 精选完整宏包集）
   ├─ sharelatex-server-pro-cn.tar.gz    # Overleaf「中文常用版」精简镜像（约 1.7GB，中文排版 + 常用宏包保留集）
   ├─ git-bridge.tar.gz                  # Git-Bridge / MCP 服务镜像（约 387MB，必选，Git 操作与三方同步依赖）
   ├─ mongo-8.0.tar.gz                   # MongoDB 8.0（约 300MB）
   ├─ redis-7.tar.gz                     # Redis 7（约 42MB）
   ├─ images-manifest.json               # 镜像清单（变体、标签、SHA256、构建时间）
   └─ *.sha256                           # 各镜像包的 SHA256 校验文件
```

**双变体主镜像**：Overleaf 主镜像提供「完整版 / 中文常用版」两种变体，部署时按所选变体加载对应镜像包（未选中的变体包自动跳过；磁盘紧张时可删除不用的变体包）：

| 变体 | 镜像包 | 体积 | 适用 |
|---|---|---|---|
| 完整版（默认） | `sharelatex-server-pro.tar.gz` | 约 3.5GB | 精选完整宏包集开箱即用，覆盖中文支持与常见期刊 / 模板宏包 |
| 中文常用版（精简） | `sharelatex-server-pro-cn.tar.gz` | 约 1.7GB | 面向中文常用排版场景裁剪；后续缺宏包时经「宏包面板」tlmgr 在线补装，形成闭环 |

全部包合计约 6.3GB，**不入 git 仓库**（`.gitignore` 已排除），正式分发走下文「主下载源（网盘）」中的 `runtime.7z`（Release 不再附带运行时包）。需要自行重建镜像时，仓库提供三种 TeX Live 层 Dockerfile：`tools/runtime/texlive-packages/Dockerfile`（精选集合，完整版默认）、`tools/runtime/texlive-full/Dockerfile`（scheme-full 全量）、`tools/runtime/texlive-cn/Dockerfile`（中文常用版精简），经 `tools/runtime/build-images.ps1` / `build-images.py` 一键构建（串联 Pandoc 层 → TeX Live 层 → `wslc save` → SHA256 清单）。获取方式任选其一：

| 方式 | 适用镜像 | 说明 |
|---|---|---|
| 网盘主下载源 `runtime.7z` | 全部 | 从下文「主下载源（网盘）」任选一个分享链接下载，解压到 exe 同级即为 `runtime\images\` |
| `wslc` 自助导出 | mongo / redis | 标准镜像：`wslc pull mongo:8.0` 后 `wslc save -o runtime\images\mongo-8.0.tar.gz mongo:8.0`（redis 同理） |
| 本仓库构建脚本 | sharelatex 双变体 / git-bridge 定制镜像 | `pwsh tools\runtime\build-images.ps1`（默认精选集合；`-Variant cn` 精简中文；`-Variant full` scheme-full） |

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

**runtime junction 机制**：仓库根 `runtime\` 是镜像包的真实存放处；每次构建后，csproj 中的 `LinkAyakaleafRuntime` Target 会把输出目录 `bin\<配置>\net10.0-windows\runtime` 以目录联接（junction）指向它，`dotnet publish` 输出目录由 `LinkAyakaleafRuntimeOnPublish` 同样处理——避免每次构建复制约 6.3GB，应用运行时从 exe 旁的 `runtime\images` 读取。对仓库根 `runtime\` 的改动在下次运行时即时生效。

**首次运行流程**：注册本地用户并登录 → 服务台点击 **开始部署**（自动加载镜像、初始化 MongoDB 副本集）→ **启动服务** → **打开工作台**。可用 `wslc images` 确认 4 个镜像、`wslc list` 确认 3 个容器（mongo / redis / overleaf；启用 Git-Bridge 时另见 git-bridge 容器）均已就绪。

## 📦 发布与分发（Release）

主程序统一从 [GitHub Releases](https://github.com/BluesilveEmperor/Ayaka-Leaf-PCL/releases) 下载，运行时包 `runtime.7z` 从下方「主下载源（网盘）」获取；**Release 仅附带主程序与轻量客户端，不再附带运行时包**。附件如下：

| 附件 | 说明 |
|---|---|
| `AyakaLauncher_Release_x64.exe` | x64 主程序，Framework-dependent 单文件，需 [.NET 10 Desktop Runtime](https://get.dot.net/10) |
| `AyakaLauncher_Release_ARM64.exe` | ARM64 主程序，Framework-dependent 单文件，需求同上 |
| `AyakaCollabLite_<版本>_x64.zip`（+ `.sha256`） | 轻量协作客户端（≤ 50MB），异地队友免完整启动器加入协作，解压即用 |

> 📌 **运行时说明**：`runtime.7z` 内含双变体主镜像（完整版约 3.5GB / 中文常用版约 1.7GB），均内置 Pandoc 3.x 与中文支持；完整版开箱即用，中文常用版缺宏包时经「宏包面板」在线补装。

### 主下载源（网盘）

运行时包 `runtime.7z` 通过网盘分享分发，以下**全部为主下载源**，任选其一即可；后续会陆续补充其他网盘的分享链接。

| 网盘 | 分享链接 |
|---|---|
| 阿里云盘 | [阿里云盘分享链接](https://www.alipan.com/s/NYaZyrvbRmX) |
| 其他网盘 | 待补充 |

> 📁 **按版本分文件夹**：网盘分享页按启动器版本建立文件夹，请先进入对应版本目录（当前 `v2.1.2`）再下载 `runtime.7z`。主程序的备用网盘分享同样按版本分文件夹，两者必须取自**同一版本目录**，不要跨版本混用。

若暂时没在网盘里找到，那多半是网络有点慢还没传完，或者作者有点懒还没上传——稍后再来看看。用支持 7z 的解压工具把 `runtime.7z` 解压到 exe 同级目录，得到 `runtime\images\`；随后按下节「部署步骤」继续。

### 备用下载：主程序（阿里云盘）

GitHub Releases 是主程序的**主渠道**；下载不便时，可从以下备用分享获取 `AyakaLauncher_Release_x64.exe` / `AyakaLauncher_Release_ARM64.exe`（以分享页实际文件为准）：

- [阿里云盘分享链接](https://www.alipan.com/s/XBcaUK75hrU)

> 📁 分享页**按启动器版本分文件夹**存放，请先进入对应版本目录（当前 `v2.1.2`）再取 exe，并与上文「主下载源（网盘）」的 `runtime.7z` 保持同一版本。

> ⚠️ 这里只提供**主程序 exe**。运行时包 `runtime.7z` 是另一份资源，仍须从上文「主下载源（网盘）」获取；两个链接用途不同，不能互相替代。

### 部署步骤

1. 把 `AyakaLauncher_Release_<平台>.exe` 与运行时包 `runtime.7z` 放到同一目录
2. 在该目录解压运行时包，得到 `runtime\images\`（含 `sharelatex-server-pro`、`git-bridge`、`mongo-8.0`、`redis-7` 四个镜像包）
3. 运行 exe，按「快速开始」完成首次部署

### 运行时包的分发方式

运行时镜像不入库，干净的 CI 构建机无法凭空生成运行时包。因此**运行时包统一由网盘主下载源 `runtime.7z` 分发，Release 不再附带**。本机执行 `pwsh release.ps1` / `python release.py` 仍会在 `release\` 下生成 `runtime.zip`，供本机整合包与离线分发使用，但不会上传到 Release。

### 为什么 runtime 不进入代码仓库

单个镜像包最大约 3.5GB（sharelatex-server-pro 完整版），远超 GitHub 普通仓库 100MB 的单文件限制，因此运行时镜像不打入 git，改由网盘主下载源 `runtime.7z` 分发（Release 不再附带运行时包）。仓库内的 `runtime\` 目录仅供本机开发使用，已在 `.gitignore` 中忽略。

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

```powershell
# 可选：随发布一并构建轻量协作客户端（Python 版用 --with-collab-lite）
pwsh -ExecutionPolicy Bypass -File release.ps1 -Configuration Release -Platform x64 -WithCollabLite
```

> `runtime.zip` 与平台无关，ARM64 重跑仅重新打包覆盖，内容不变；Release 不再附带运行时包，对外统一由网盘 `runtime.7z` 分发。

打包前脚本会先检查项目根是否存在 `runtime\` 目录：若不存在，直接提示先从网盘下载 `runtime.7z`、解压到项目根目录后再运行脚本；若存在，则继续校验 `runtime\images\` 下的 `sharelatex-server-pro.tar.gz`、`git-bridge.tar.gz`、`mongo-8.0.tar.gz`、`redis-7.tar.gz` 四个必备镜像是否齐备，缺失则报错并提示补齐（可从网盘 `runtime.7z` 解压恢复）。随后校验镜像清单 `images-manifest.json`（存在时）：清单声明的每个镜像包（含中文常用版变体）必须存在，且各自的 `.sha256` 伴生文件与清单一致，防止产出残缺或被篡改的运行时包。

| 产物 | 触发条件 | 说明 |
|---|---|---|
| `release\AyakaLauncher_<配置>_<平台>.exe` | 默认 | 主程序（x64 / ARM64 分别构建），命名与 Release 附件一致（不含版本号） |
| `release\runtime.zip` | 默认 | 内嵌运行时镜像包（含双变体主镜像），约 6.3GB，仅供本机整合包使用，不再随 Release 分发 |
| `release\AyakaLauncher_<版本>_<配置>_<平台>.zip` | 加 `-WithBundle` | exe + runtime 整合包（含双变体主镜像），约 6.3GB，适合离线分发 |
| `release\AyakaCollabLite_<版本>_x64.zip`（+ `.sha256`） | 默认校验 / `-WithCollabLite` 时先构建 | 轻量协作客户端安装包（≤ 50MB）；已存在则校验哈希纳入产物清单，缺失时告警提示构建 |
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
- [Campfire](https://github.com/Xocio/Campfire) —— 异地 / 异网协作方案与「營火橘」主题色的设计参考
- [LaTeX-Workshop](https://github.com/James-Yu/LaTeX-Workshop) —— 符号面板拓展所采纳的全量符号集来源

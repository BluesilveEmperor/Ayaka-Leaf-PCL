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
- **服务台** —— 一键部署 / 启动 / 停止内置 Overleaf 服务（MongoDB 8.0 + Redis 7 + Overleaf），自动初始化 MongoDB 副本集，服务就绪自动探测
- **容器运行时** —— 基于 Windows WSL 3.0+ 内置容器能力（WSLC），**无需 Docker Desktop**，数据卷为独立 vhd 文件
- **工作台** —— 内嵌 WebView2 打开 Overleaf，自动开户 + 自动登录，免手动输入
- **公式联动** —— 左侧选中 LaTeX 公式代码，右侧 PDF 自动高亮对应位置并滚动定位（复用 Overleaf 自带 SyncTeX）
- **镜像管理** —— 内置镜像 tar.gz（`runtime/images`），部署时自动加载，重复加载幂等
- **备份恢复** —— 数据卷即 vhd 文件，页面内一键快照 / 恢复

## 📐 编辑器联动

在工作台里用鼠标选中一段公式代码，右侧 PDF 会自动跳到公式渲染位置并高亮，不必再点工具栏的 Sync to PDF：

- 支持 `$…$`、`$$…$$`、`\(…\)`、`\[…\]`，以及 `equation`、`align`、`gather`、`multline`、`split`、`cases`、`matrix` 等 `\begin{}` 环境
- 只对公式选区生效；选中普通文字、复制粘贴不受影响
- 右侧需已成功编译出 PDF 才会触发（未编译时静默跳过，不弹错误提示）
- 高亮框来自 SyncTeX 记录的位置，多行环境可能只覆盖该处首行

## 💻 支持平台

| 操作系统 | 支持情况 | 环境要求 |
|---|---|---|
| Windows 10 21H2+ / Windows 11 (x64) | ✅ 完整支持 | [.NET 10 Desktop Runtime](https://get.dot.net/10) + WSL 3.0+ |
| Windows 10 1809 - 21H2 | ⚠️ 需手动升级 WSL 至 3.0+ | 同上 |
| Windows 7 / 8 / 8.1 | ❌ 不支持 | / |
| macOS / Linux | ❌ 不支持 | WPF、WebView2 与 WSLC 容器均为 Windows 专属 |

## 🚀 快速开始

1. 从 [Releases](https://github.com/BluesilveEmperor/Ayaka-Leaf-PCL/releases) 下载**完整分发包** `AyakaLauncher_<版本>_Release_x64.zip`（约 1.6GB，已内嵌运行时）并解压到任意目录
2. 安装 [.NET 10 Desktop Runtime](https://get.dot.net/10)（x64）
3. 启用 WSL：管理员 PowerShell 执行 `wsl --install --no-distribution`，完成后执行 `wsl --update`
4. 启动 `AyakaLauncher.exe` → 注册本地用户并登录
5. 服务台页面依次点击：**开始部署** → **启动服务** → **打开工作台**（服务地址 `http://localhost:8080`，端口可配置）

> ⚠️ `runtime\` 是容器镜像运行时，必须与 `AyakaLauncher.exe` 保持**同级、不可拆分或改名**。若只下载 13MB 的独立 exe，需自备 `runtime\`（见下节）。

## 📦 内嵌运行时（runtime）

`runtime\images\` 存放 WSLC 容器镜像包，部署时由应用自动加载（幂等，重复加载无副作用），结构如下：

```
AyakaLauncher.exe
runtime\
└─ images\
   ├─ sharelatex-server-pro.tar.gz   # Overleaf 服务端定制镜像（约 1.24GB）
   ├─ mongo-8.0.tar.gz               # MongoDB 8.0（约 300MB）
   └─ redis-7.tar.gz                 # Redis 7（约 42MB）
```

三个包合计约 1.6GB，**不入 git 仓库**（`.gitignore` 已排除）。获取方式任选其一：

| 方式 | 适用镜像 | 说明 |
|---|---|---|
| Release 完整包解压 | 全部 | 最省事，`AyakaLauncher_*_Release_x64.zip` 内已含 `runtime\` |
| `wslc` 自助导出 | mongo / redis | 标准镜像：`wslc pull mongo:8.0` 后 `wslc save -o runtime\images\mongo-8.0.tar mongo:8.0`（redis 同理） |
| ayakaleaf-pro 构建 | sharelatex 定制镜像 | 从 [ayakaleaf-pro](https://github.com/ayaka-notes/ayakaleaf-pro) 构建镜像后 `wslc save` 导出 |

**放置位置**：分发包内放在 exe 同级；源码开发放在**仓库根** `runtime\images\`（构建后自动挂到输出目录，见下节）。

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

# 2. 摆放 runtime：把上节获取的 3 个镜像包放到仓库根
#    runtime\images\sharelatex-server-pro.tar.gz
#    runtime\images\mongo-8.0.tar.gz
#    runtime\images\redis-7.tar.gz

# 3. 构建并运行
dotnet run --project "Plain Craft Launcher 2"
```

也可以用 Visual Studio / Rider / VS Code 打开解决方案直接 F5。

**runtime junction 机制**：仓库根 `runtime\` 是镜像包的真实存放处；每次构建后，csproj 中的 `LinkAyakaleafRuntime` Target 会把输出目录 `bin\<配置>\net10.0-windows\runtime` 以目录联接（junction）指向它——避免每次构建复制 1.6GB，应用运行时从 exe 旁的 `runtime\images` 读取。对仓库根 `runtime\` 的改动在下次运行时即时生效。

**首次运行流程**：注册本地用户并登录 → 服务台点击 **开始部署**（自动加载镜像、初始化 MongoDB 副本集）→ **启动服务** → **打开工作台**。可用 `wslc images` 确认 3 个镜像、`wslc list` 确认 3 个容器（mongo / redis / overleaf）均已就绪。

### 发布打包

```powershell
pwsh -ExecutionPolicy Bypass -File release.ps1 -Configuration Release -Platform x64
```

产出 `release\AyakaLauncher_<版本>_Release_x64.zip`（exe + 真实 runtime 文件，可直接分发）及 `artifact\AyakaLauncher.exe` 单文件。本机日常运行无需打包——构建输出目录已通过 junction 挂好 runtime。

## 🔒 许可证

- `Plain Craft Launcher 2/` 使用 [上游自定义许可证](Plain%20Craft%20Launcher%202/LICENCE)
- 其余目录使用 [Apache License 2.0](LICENSE)

## 🙏 致谢

- [PCL Community Edition](https://github.com/PCL-Community/PCL-CE) —— UI 框架与工程基础
- [Meloong-Git/PCL](https://github.com/Meloong-Git/PCL) —— 上游原项目
- [Overleaf](https://github.com/overleaf/overleaf) —— 内嵌的协作 LaTeX 服务

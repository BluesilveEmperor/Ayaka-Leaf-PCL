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
- **镜像管理** —— 内置镜像 tar.gz（`runtime/images`），部署时自动加载，重复加载幂等
- **备份恢复** —— 数据卷即 vhd 文件，页面内一键快照 / 恢复

## 💻 支持平台

| 操作系统 | 支持情况 | 环境要求 |
|---|---|---|
| Windows 10 21H2+ / Windows 11 (x64) | ✅ 完整支持 | [.NET 10 Desktop Runtime](https://get.dot.net/10) + WSL 3.0+ |
| Windows 10 1809 - 21H2 | ⚠️ 需手动升级 WSL 至 3.0+ | 同上 |
| Windows 7 / 8 / 8.1 | ❌ 不支持 | / |
| macOS / Linux | ⚠️ 仅源码编译支持 | [.NET 10 SDK](https://get.dot.net/10) |

## 🚀 快速开始

1. 安装 [.NET 10 Desktop Runtime](https://get.dot.net/10)（x64）
2. 启用 WSL：管理员 PowerShell 执行 `wsl --install --no-distribution`，完成后执行 `wsl --update`
3. 启动 `AyakaLauncher.exe` → 注册本地用户并登录
4. 服务台页面依次点击：**开始部署** → **启动服务** → **打开工作台**（浏览器访问 `http://localhost:8080`）

## 🔨 构建

需要 [.NET 10 SDK](https://get.dot.net/10)：

```powershell
dotnet publish "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" -c Release -p:Platform=x64 --no-self-contained -o artifact
```

产物为单文件 `AyakaLauncher.exe`（Framework-dependent）。

## 🔒 许可证

- `Plain Craft Launcher 2/` 使用 [上游自定义许可证](Plain%20Craft%20Launcher%202/LICENCE)
- 其余目录使用 [Apache License 2.0](LICENSE)

## 🙏 致谢

- [PCL Community Edition](https://github.com/PCL-Community/PCL-CE) —— UI 框架与工程基础
- [Meloong-Git/PCL](https://github.com/Meloong-Git/PCL) —— 上游原项目
- [Overleaf](https://github.com/overleaf/overleaf) —— 内嵌的协作 LaTeX 服务

[简体中文](README.md) | **English** | [繁體中文](README-ZH_TW.md)

<div align="center">

<img src="Plain Craft Launcher 2/Images/icon.ico" alt="Logo" width="80" height="80">

# AyakaLauncher

**The all-in-one desktop client for Ayakaleaf** · Based on [PCL Community Edition](https://github.com/PCL-Community/PCL-CE)

</div>

AyakaLauncher is the all-in-one desktop client for [Ayakaleaf](https://github.com/ayaka-notes/ayakaleaf-pro) (a customized Overleaf server distribution), built on top of the PCL Community Edition framework.

This project is **fully decoupled from Minecraft**: it no longer contains any MC-related code, pages, or resources.

## ✨ Features

- **Local user system** — Sign up / sign in / multi-user switching (SQLite + PBKDF2), per-user isolated data directories, and a login gate on startup
- **Service console** — Deploy / start / stop the bundled Overleaf service (MongoDB 8.0 + Redis 7 + Overleaf) with one click; the MongoDB replica set is initialized automatically and service readiness is probed automatically
- **Container runtime** — Powered by the container capabilities built into Windows WSL 3.0+ (WSLC), **no Docker Desktop required**; data volumes are standalone vhd files
- **Workbench** — Opens Overleaf in an embedded WebView2 with automatic account provisioning and sign-in, zero manual input
- **Image management** — Bundled image tarballs (`runtime/images`) are loaded automatically during deployment; repeated loads are idempotent
- **Backup & restore** — Each data volume is just a vhd file; one-click snapshots and restores from the backup page

## 💻 Supported Platforms

| Operating system | Support | Requirements |
|---|---|---|
| Windows 10 21H2+ / Windows 11 (x64) | ✅ Fully supported | [.NET 10 Desktop Runtime](https://get.dot.net/10) + WSL 3.0+ |
| Windows 10 1809 - 21H2 | ⚠️ Requires manually upgrading WSL to 3.0+ | Same as above |
| Windows 7 / 8 / 8.1 | ❌ Not supported | / |
| macOS / Linux | ⚠️ Source-build only | [.NET 10 SDK](https://get.dot.net/10) |

## 🚀 Quick Start

1. Install the [.NET 10 Desktop Runtime](https://get.dot.net/10) (x64)
2. Enable WSL: in an elevated PowerShell, run `wsl --install --no-distribution`, then run `wsl --update`
3. Launch `AyakaLauncher.exe` → register a local user and sign in
4. In the service console, click in order: **Deploy** → **Start Services** → **Open Workbench** (browser opens at `http://localhost:8080`)

## 🔨 Building

Requires the [.NET 10 SDK](https://get.dot.net/10):

```powershell
dotnet publish "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" -c Release -p:Platform=x64 --no-self-contained -o artifact
```

The output is a single-file `AyakaLauncher.exe` (framework-dependent).

## 🔒 License

- `Plain Craft Launcher 2/` is licensed under the [upstream custom license](Plain%20Craft%20Launcher%202/LICENCE)
- All other directories are licensed under the [Apache License 2.0](LICENSE)

## 🙏 Acknowledgements

- [PCL Community Edition](https://github.com/PCL-Community/PCL-CE) — UI framework and engineering foundation
- [Meloong-Git/PCL](https://github.com/Meloong-Git/PCL) — the upstream original project
- [Overleaf](https://github.com/overleaf/overleaf) — the bundled collaborative LaTeX service

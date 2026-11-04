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
- **Formula highlight sync** — Select formula code on the left and the PDF on the right highlights the rendered formula and scrolls to it (reuses Overleaf's built-in SyncTeX)
- **Image management** — Bundled image tarballs (`runtime/images`) are loaded automatically during deployment; repeated loads are idempotent
- **Backup & restore** — Each data volume is just a vhd file; one-click snapshots and restores from the backup page

## 📐 Editor Sync

Select a piece of formula code in the workbench and the PDF pane jumps to the rendered formula and highlights it — no need to click Sync to PDF in the toolbar:

- Supports `$…$`, `$$…$$`, `\(…\)`, `\[…\]` and `\begin{}` environments such as `equation`, `align`, `gather`, `multline`, `split`, `cases`, `matrix`
- Only formula selections trigger it; selecting plain text, copying and pasting are unaffected
- Requires a successfully compiled PDF; it stays silent when no PDF exists (no error toasts)
- Highlight boxes come from SyncTeX positions, so multi-line environments may only cover the first line of that spot

## 💻 Supported Platforms

| Operating system | Support | Requirements |
|---|---|---|
| Windows 10 21H2+ / Windows 11 (x64) | ✅ Fully supported | [.NET 10 Desktop Runtime](https://get.dot.net/10) + WSL 3.0+ |
| Windows 10 1809 - 21H2 | ⚠️ Requires manually upgrading WSL to 3.0+ | Same as above |
| Windows 7 / 8 / 8.1 | ❌ Not supported | / |
| macOS / Linux | ❌ Not supported | WPF, WebView2 and WSLC containers are Windows-only |

## 🚀 Quick Start

1. Download `AyakaLauncher_Release_x64.exe` (or `AyakaLauncher_Release_ARM64.exe` on ARM64 devices) and `runtime.zip` from [Releases](https://github.com/BluesilveEmperor/Ayaka-Leaf-PCL/releases)
2. Put the exe and `runtime.zip` in the same folder and extract `runtime.zip` there to get `runtime\images\` (~1.6GB, see Release & Distribution below)
3. Install the [.NET 10 Desktop Runtime](https://get.dot.net/10) matching the executable's architecture
4. Enable WSL: in an elevated PowerShell, run `wsl --install --no-distribution`, then run `wsl --update`
5. Launch `AyakaLauncher_Release_<platform>.exe` → register a local user and sign in
6. In the service console, click in order: **Deploy** → **Start Services** → **Open Workbench** (service address `http://localhost:8080`, port configurable)

> ⚠️ `runtime\` holds the container images and must stay **next to the exe, with the same name**. The exe does not bundle any image; deployment fails without `runtime\images\`.

## 🔨 Building

Requires the [.NET 10 SDK](https://get.dot.net/10):

```powershell
dotnet publish "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" -c Release -p:Platform=x64 --no-self-contained -o artifact
```

The output is a single-file `AyakaLauncher.exe` (framework-dependent).

## 📦 Release & Distribution

Official builds are published on [GitHub Releases](https://github.com/BluesilveEmperor/Ayaka-Leaf-PCL/releases). Each release provides these attachments:

| Attachment | Description |
|---|---|
| `AyakaLauncher_Release_x64.exe` | x64 single-file executable (framework-dependent); requires the [.NET 10 Desktop Runtime](https://get.dot.net/10) |
| `AyakaLauncher_Release_ARM64.exe` | ARM64 single-file executable (framework-dependent); same runtime requirement |
| `runtime.zip` | Bundled container runtime images, about 1.6GB |

### Deployment

1. Put `AyakaLauncher_Release_<platform>.exe` and `runtime.zip` in the same folder
2. Extract `runtime.zip` in that folder to get `runtime\images\` (containing the `sharelatex-server-pro`, `mongo-8.0` and `redis-7` image tarballs)
3. Run the exe

`runtime\` must stay **next to the exe, with the same name**; do not split it up or rename it.

### First release (where `runtime.zip` comes from)

CI cannot produce `runtime.zip` out of thin air: the runtime images are not in the repository, so a clean build machine has nothing to pack. Therefore:

- **First release** (when no earlier release contains `runtime.zip`): run `pwsh release.ps1` locally to get `release\runtime.zip`, then upload it to that release manually — or, when triggering the workflow by hand, put the tag of a release that already contains `runtime.zip` into the `runtime_source_tag` input.
- **Subsequent releases**: the `package_runtime` job automatically finds the most recent non-draft release that contains `runtime.zip`, downloads it and reuses it for the new release, so no manual upload is needed again.

### Why the runtime is not in the repository

The largest single image tarball is 1262.9MB, far beyond GitHub's 100MB per-file limit for regular repositories, so the runtime images are distributed as the `runtime.zip` release attachment instead. The `runtime\` folder inside the repository is for local development only and is already ignored by `.gitignore`.

### Local packaging

```powershell
# Default: produces the exe and runtime.zip
pwsh -ExecutionPolicy Bypass -File release.ps1 -Configuration Release -Platform x64

# Optional: also produce the all-in-one bundle
pwsh -ExecutionPolicy Bypass -File release.ps1 -Configuration Release -Platform x64 -WithBundle
```

Before packing, the script verifies that `sharelatex-server-pro.tar.gz`, `mongo-8.0.tar.gz` and `redis-7.tar.gz` all exist under `runtime\images\`; if any is missing it aborts with an error telling you to restore them by extracting the `runtime.zip` release attachment.

| Artifact | Produced when | Description |
|---|---|---|
| `release\AyakaLauncher_<configuration>_<platform>.exe` | by default | The executable, named exactly like the release attachment (no version number) |
| `release\runtime.zip` | by default | Bundled runtime image archive, about 1.6GB — the same file published as the `runtime.zip` release attachment |
| `release\AyakaLauncher_<version>_<configuration>_<platform>.zip` | with `-WithBundle` | exe plus runtime bundle, about 1.7GB, for offline distribution |

The script archives with the built-in `C:\Windows\System32\tar.exe`, so it does not depend on whichever `tar` is on `PATH`. Day-to-day local runs need no packaging: the build output directory already links `runtime` through a junction.

## 🔒 License

- `Plain Craft Launcher 2/` is licensed under the [upstream custom license](Plain%20Craft%20Launcher%202/LICENCE)
- All other directories are licensed under the [Apache License 2.0](LICENSE)

## 🙏 Acknowledgements

- [PCL Community Edition](https://github.com/PCL-Community/PCL-CE) — UI framework and engineering foundation
- [Meloong-Git/PCL](https://github.com/Meloong-Git/PCL) — the upstream original project
- [Overleaf](https://github.com/overleaf/overleaf) — the bundled collaborative LaTeX service
- [Ayakaleaf](https://github.com/ayaka-notes/ayakaleaf-pro) — the upstream customized Overleaf server distribution that provides this project's bundled runtime and workbench backend

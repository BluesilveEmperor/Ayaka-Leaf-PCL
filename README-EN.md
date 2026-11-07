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
- **Service console** — Deploy / start / stop the bundled Overleaf service (MongoDB 8.0 + Redis 7 + Overleaf; plus the git-bridge container when Git-Bridge is enabled) with one click; the MongoDB replica set is initialized automatically and service readiness is probed automatically
- **Container runtime** — Powered by the container capabilities built into Windows WSL 3.0+ (WSLC), **no Docker Desktop required**; data volumes are standalone vhd files
- **Workbench** — Opens Overleaf in an embedded WebView2 with automatic account provisioning and sign-in, zero manual input
- **Formula highlight sync** — Select formula code on the left and the PDF on the right highlights the rendered formula and scrolls to it (reuses Overleaf's built-in SyncTeX)
- **Symbol palette extension** — The built-in symbol palette is expanded to the full LaTeX-Workshop symbol set: 10 Chinese categories, 760+ symbols (merged on top of Overleaf's native ones), searchable by name / keyword / Unicode. Applied automatically when the service starts, no manual steps needed
- **Image management** — Bundled image tarballs (`runtime/images`) are loaded automatically during deployment; repeated loads are idempotent
- **Backup & restore** — Each data volume is just a vhd file; one-click snapshots and restores from the backup page
- **LAN sharing (team collaboration)** — The service console shows a live "teammate access address" so peers on the same LAN can reach your Overleaf with just a browser, register accounts and co-edit in real time; the address refreshes automatically on network changes, and VPN / TUN virtual adapters (Mihomo / Clash, etc.) are excluded automatically. See the "Team Collaboration" chapter
- **Remote collaboration** — EasyTier-based virtual networking: the host opens the local Overleaf service with a one-click invite code; remote peers join the virtual network and get port forwards automatically — no public IP or port mapping needed. See the "Remote Collaboration" chapter
- **Portable account isolation** — Account database, credentials and user data all live next to the launcher exe; copy the folder for a fully portable install, and multiple copies never share or collide accounts
- **Theme colors** — Four built-in theme colors to switch at will — Sky Blue, Totoro Blue, Crash Blue and Campfire Orange (Campfire's signature #E2680C) — adapted to both light and dark modes

## 📐 Editor Sync

Select a piece of formula code in the workbench and the PDF pane jumps to the rendered formula and highlights it — no need to click Sync to PDF in the toolbar:

- Supports `$…$`, `$$…$$`, `\(…\)`, `\[…\]` and `\begin{}` environments such as `equation`, `align`, `gather`, `multline`, `split`, `cases`, `matrix`
- Only formula selections trigger it; selecting plain text, copying and pasting are unaffected
- Requires a successfully compiled PDF; it stays silent when no PDF exists (no error toasts)
- Highlight boxes come from SyncTeX positions, so multi-line environments may only cover the first line of that spot

## 🤝 Team Collaboration & Remote Collaboration

Pick the collaboration mode based on how your peers connect to you:

| Scenario | Feature | Requirements |
|---|---|---|
| Same LAN (dorm / lab / office network) | Team collaboration (LAN sharing) | Peers only need a browser — no software to install |
| Remote / different networks | Remote collaboration (EasyTier virtual networking) | Both sides need internet access; peers must run this launcher |

### 🏠 Team Collaboration (LAN Sharing)

Peers on the same LAN can reach your Overleaf service with just a browser — register accounts and co-edit documents in real time.

**Host (you)**

1. Start the service on the "Service" page
2. Check "Networking & Collaboration → Allow LAN access (team collaboration)"; the page then shows the "teammate access address" (e.g. `http://192.168.1.100:8080`; when the Simplified Chinese UI is enabled, the Chinese-port address is shown as well)
3. Restart the service for the change to take effect (this toggle requires a service restart)
4. Send the "teammate access address" to peers on the same LAN

**Peers**

1. Open the address shared by the host in a browser, on any device on the same LAN (PC / tablet / phone all work)
2. On first visit, register an account (or sign in to an existing one)
3. Then create / join projects and co-edit with the host and other peers in real time

**Notes**

- The address uses the host machine's LAN IP and refreshes automatically on network changes (Wi-Fi ↔ hotspot, etc.); VPN / TUN virtual adapters (Mihomo / Clash, etc.) are excluded automatically, so a proxy adapter IP never shows up by mistake
- After turning the toggle off and restarting the service, the service goes back to localhost-only access
- Peers share the host's service and project space; the practical peer limit is the host machine's performance

### 🌐 Remote Collaboration

Remote peers can join your Overleaf service with an invite code — no public IP or port mapping required. The virtual network is powered by [EasyTier](https://github.com/EasyTier/EasyTier) (Apache-2.0); on first use the collaboration component is downloaded automatically from the official release channels — nothing to install manually.

**Host**

1. Start the service, open the "Collab" page and click "Start Collaboration"
2. Click "Copy Invite Code" and send the generated code (format `A/XXXX-XXXX-XXXX-XXXX`) to your teammates
3. Wait for peers to join; the page shows the number of connected public relay nodes, and the member list refreshes every 2 seconds, showing each peer's connection type (direct / relayed) and latency

**Guest**

1. Open this launcher, go to the "Collab" page, paste the invite code (pasting a whole block of text containing the code is fine — the code inside is recognized automatically) and click "Join Collaboration"
2. Joining is fully automatic: connect to public nodes → find the host → establish one local port forward per interface language enabled by the host (the host's port number is reused locally; if occupied, a free port is picked automatically)
3. Once joined, the page lists the per-language access addresses (e.g. `简体中文: http://localhost:8081`); click "Open Workbench" to enter the host's service (switchable between the languages the host has enabled), or open the listed addresses directly in a browser
4. On first visit, register or sign in on the host's service; then co-edit with the host in real time

**Both sides**

- The collab session survives page switches; "Stop Collaboration" disconnects at any time, and the network component is cleaned up automatically when the launcher exits
- Once the host stops the collaboration, all peers lose access immediately
- If it sits on "Connecting to public nodes…" for a long time, the built-in nodes are usually unreachable due to a restricted network — configure custom relay nodes (see below)

**Privacy & security**

- Traffic is end-to-end encrypted; public relay nodes only forward ciphertext and cannot decrypt contents; P2P direct connection is preferred whenever possible, with relay as fallback only
- The host-side whitelist only exposes the Overleaf service ports — no other local port is reachable
- The invite code is the network key (16 characters, with a checksum digit against typos); do not share it publicly. If it leaks, the host can stop the collaboration and click "Start Collaboration" again — a new code is generated and the old one becomes invalid
- Advanced options via the `AyakaleafCollab*` config entries: "hide IP" (peers cannot see each other's virtual IPs), "direct only" (no relay fallback)

**Network nodes**

Five community relay nodes are connected automatically by default; custom nodes (`tcp://` / `udp://` / `wss://` / `https://`, comma-separated) can be added and are appended to the built-in ones.

## 💻 Supported Platforms

| Operating system | Support | Requirements |
|---|---|---|
| Windows 10 21H2+ / Windows 11 (x64 / ARM64) | ✅ Fully supported | [.NET 10 Desktop Runtime](https://get.dot.net/10) (matching the executable's architecture) + WSL 3.0+ |
| Windows 10 1809 - 21H2 | ⚠️ Requires manually upgrading WSL to 3.0+ | Same as above |
| Windows 7 / 8 / 8.1 | ❌ Not supported | / |
| macOS / Linux | ❌ Not supported | WPF, WebView2 and WSLC containers are Windows-only |

## 🚀 Quick Start

1. Download `AyakaLauncher_Release_x64.exe` (or `AyakaLauncher_Release_ARM64.exe` on ARM64 devices) and `runtime.zip` from [Releases](https://github.com/BluesilveEmperor/Ayaka-Leaf-PCL/releases)
2. Put the exe and `runtime.zip` in the same folder and extract `runtime.zip` there to get `runtime\images\` (~4.2GB, see Release & Distribution below)
3. Install the [.NET 10 Desktop Runtime](https://get.dot.net/10) matching the executable's architecture
4. Enable WSL: in an elevated PowerShell, run `wsl --install --no-distribution`, then run `wsl --update`
5. Launch `AyakaLauncher_Release_<platform>.exe` → register a local user and sign in
6. In the service console, click in order: **Deploy** → **Start Services** → **Open Workbench** (service address `http://localhost:8080`, port configurable)

> ⚠️ `runtime\` holds the container images and must stay **next to the exe, with the same name**. The exe does not bundle any image; deployment fails without `runtime\images\`.

## 🛠️ Local Development

### Prerequisites

| Dependency | Version | Install |
|---|---|---|
| .NET SDK | 10 | [get.dot.net/10](https://get.dot.net/10) |
| WSL | 3.0+ | Elevated PowerShell: `wsl --install --no-distribution`, then `wsl --update` |
| Container runtime | WSLC | Built into WSL 3.0+, **no Docker Desktop required** |

### Setup

```powershell
# 1. Clone
git clone https://github.com/BluesilveEmperor/Ayaka-Leaf-PCL.git
cd Ayaka-Leaf-PCL

# 2. Place the runtime: put the 4 image tarballs acquired above at the repository root
#    runtime\images\sharelatex-server-pro.tar.gz
#    runtime\images\git-bridge.tar.gz
#    runtime\images\mongo-8.0.tar.gz
#    runtime\images\redis-7.tar.gz

# 3. Build and run
dotnet run --project "Plain Craft Launcher 2"
```

You can also open the solution in Visual Studio / Rider / VS Code and press F5.

**runtime junction mechanism**: the repository-root `runtime\` folder is where the image tarballs actually live. After each build, the `LinkAyakaleafRuntime` target in the csproj links the output directory `bin\<Configuration>\net10.0-windows\runtime` to it via a directory junction (`dotnet publish` output directories get the same treatment from `LinkAyakaleafRuntimeOnPublish`) — avoiding copying 4.2GB on every build. The app reads `runtime\images` next to the exe at runtime, so changes to the repository-root `runtime\` take effect on the next launch.

**First run**: register a local user and sign in → in the service console click **Deploy** (loads the images and initializes the MongoDB replica set automatically) → **Start Services** → **Open Workbench**. Use `wslc images` to confirm the 4 images and `wslc list` to confirm the 3 containers (mongo / redis / overleaf) are all up.

## 📦 Release & Distribution

Official builds are published on [GitHub Releases](https://github.com/BluesilveEmperor/Ayaka-Leaf-PCL/releases). Each release provides these attachments:

| Attachment | Description |
|---|---|
| `AyakaLauncher_Release_x64.exe` | x64 single-file executable (framework-dependent); requires the [.NET 10 Desktop Runtime](https://get.dot.net/10) |
| `AyakaLauncher_Release_ARM64.exe` | ARM64 single-file executable (framework-dependent); same runtime requirement |
| `runtime.zip` | Bundled container runtime images, about 4.2GB |

### Alternative download (Aliyun Drive)

If GitHub Releases is slow, `runtime.7z` is also shared via [Aliyun Drive](https://www.alipan.com/s/NYaZyrvbRmX) (to be uploaded by the maintainer later — check the share page for the actual file). Extract it with a 7z-capable tool into the folder containing the exe to get `runtime\images\`; the remaining deployment steps are identical to `runtime.zip`.

### Deployment

1. Put `AyakaLauncher_Release_<platform>.exe` and `runtime.zip` in the same folder
2. Extract `runtime.zip` in that folder to get `runtime\images\` (containing the `sharelatex-server-pro`, `git-bridge`, `mongo-8.0` and `redis-7` image tarballs)
3. Run the exe

`runtime\` must stay **next to the exe, with the same name**; do not split it up or rename it.

### First release (where `runtime.zip` comes from)

CI cannot produce `runtime.zip` out of thin air: the runtime images are not in the repository, so a clean build machine has nothing to pack. Therefore:

- **First release** (when no earlier release contains `runtime.zip`): run `pwsh release.ps1` locally to get `release\runtime.zip`, then upload it to that release manually — or, when triggering the workflow by hand, put the tag of a release that already contains `runtime.zip` into the `runtime_source_tag` input.
- **Subsequent releases**: the `package_runtime` job automatically finds the most recent non-draft release that contains `runtime.zip`, downloads it and reuses it for the new release, so no manual upload is needed again.

### Why the runtime is not in the repository

The largest single image tarball is ~3.46GB (`sharelatex-server-pro`, which bundles the **full scheme-full TeX Live** — 5,267 packages incl. ctex/xeCJK and journal templates such as IEEEtran / revtex / elsarticle), far beyond GitHub's 100MB per-file limit for regular repositories, so the runtime images are distributed as the `runtime.zip` release attachment instead. The `runtime\` folder inside the repository is for local development only and is already ignored by `.gitignore`. To rebuild the image yourself, the repo ships two Dockerfiles: `tools/runtime/texlive-full/Dockerfile` (full scheme-full) and `tools/runtime/texlive-packages/Dockerfile` (curated collections, smaller) — build, then `wslc tag` + `wslc save` to export.

### Local packaging

The packaging script has two equivalent implementations (`release.ps1` / `release.py`); pick either:

```powershell
# PowerShell version
pwsh -ExecutionPolicy Bypass -File release.ps1 -Configuration Release -Platform x64
```

```bash
# Python version (requires Python 3.9+, standard library only)
python release.py --configuration Release --platform x64
```

```powershell
# ARM64: run the script again (runtime.zip is platform-independent; re-packing just overwrites it with identical content)
pwsh -ExecutionPolicy Bypass -File release.ps1 -Configuration Release -Platform ARM64

# Optional: also produce the all-in-one bundle (use --with-bundle in the Python version)
pwsh -ExecutionPolicy Bypass -File release.ps1 -Configuration Release -Platform x64 -WithBundle
```

Before packing, the script verifies that `sharelatex-server-pro.tar.gz`, `git-bridge.tar.gz`, `mongo-8.0.tar.gz` and `redis-7.tar.gz` all exist under `runtime\images\`; if any is missing it aborts with an error telling you to restore them by extracting the `runtime.zip` release attachment.

| Artifact | Produced when | Description |
|---|---|---|
| `release\AyakaLauncher_<configuration>_<platform>.exe` | by default | The executable (x64 / ARM64 built separately), named exactly like the release attachment (no version number) |
| `release\runtime.zip` | by default | Bundled runtime image archive, about 4.2GB — the same file published as the `runtime.zip` release attachment |
| `release\AyakaLauncher_<version>_<configuration>_<platform>.zip` | with `-WithBundle` | exe plus runtime bundle, about 4.2GB, for offline distribution |
| `dist\AyakaLauncher.exe` | auto-synced when `dist\` exists | The day-to-day local run directory (runtime junction-mounted) gets refreshed on every release; skipped with a warning if the launcher is currently running |

Packaging uses only standard libraries (PowerShell's `System.IO.Compression` / Python's `zipfile`), no third-party dependencies. Day-to-day local runs need no packaging: `dist\` and the build output directory already link `runtime` through junctions.

## 🔒 License

- `Plain Craft Launcher 2/` is licensed under the [upstream custom license](Plain%20Craft%20Launcher%202/LICENCE)
- All other directories are licensed under the [Apache License 2.0](LICENSE)

## 🙏 Acknowledgements

- [PCL Community Edition](https://github.com/PCL-Community/PCL-CE) — UI framework and engineering foundation
- [Meloong-Git/PCL](https://github.com/Meloong-Git/PCL) — the upstream original project
- [Overleaf](https://github.com/overleaf/overleaf) — the bundled collaborative LaTeX service
- [Ayakaleaf](https://github.com/ayaka-notes/ayakaleaf-pro) — the upstream customized Overleaf server distribution that provides this project's bundled runtime and workbench backend

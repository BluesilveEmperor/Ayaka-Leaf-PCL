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
- **Document import (Pandoc)** — Pandoc 3.x is built into the image, so Word / Markdown documents import right from the workbench; failed imports show the reason in Chinese, and oversized documents are blocked automatically
- **Formula highlight sync** — Select LaTeX formula code on the left and the PDF on the right highlights the rendered formula and scrolls to it (reuses Overleaf's built-in SyncTeX)
- **PDF reverse sync** — Click anywhere on the PDF and the editor jumps to the corresponding source line with a flashing highlight (reverse SyncTeX lookup); automatically falls back to the official double-click flow when cross-file lookup fails
- **Error localization** — Compile error logs come with Chinese explanations (the official click-error-to-jump-to-source-line interaction is preserved); Word / Markdown import failures likewise show the reason in Chinese
- **First-run environment guide** — An environment self-check page lists the status of the three runtime prerequisites — WSL / wslc / .NET — with one-click repair instructions for anything missing
- **Image variants** — The Overleaf main image ships in two variants deployed on demand: the full variant (~3.5GB, curated macro package set) and the trimmed "Chinese-common" variant (~1.7GB, Chinese typesetting plus a retained set of common macro packages)
- **Macro package panel** — Search / install / uninstall TeX Live macro packages right on the page (online top-ups via tlmgr); when the trimmed image is missing a package, installing it online closes the loop
- **Service status page** — Container status, resource usage and recent logs all visible on one page, with one-click service restart
- **Server account management** — Lists every server account on the page: search / disable / re-enable / reset password / grant or revoke admin
- **Chinese template library** — Four categories of built-in Chinese templates — secondary-school exam papers, undergraduate theses, competition reports and domestic journals; create a project with one click and compile it directly with XeLaTeX (Chinese fonts are bundled with the image)
- **Local folder sync** — Two-way automatic sync between projects and local folders: saves from a local editor are pushed up automatically, workbench changes are pulled back automatically; when both sides change the same file, both versions are kept (conflict copies plus an explicit prompt) — never a silent overwrite
- **Lightweight collab client** — AyakaCollabLite (≤ 50MB, install-free, zero runtime dependencies) lets remote peers join a collaboration with just an invite code, without the full launcher
- **Symbol palette extension** — The built-in symbol palette is expanded to the full LaTeX-Workshop symbol set: 10 Chinese categories, 760+ symbols (merged on top of Overleaf's native ones), searchable by name / keyword / Unicode. Applied automatically when the service starts, no manual steps needed
- **Image management** — Bundled image tarballs (`runtime/images`, dual-variant main images plus a manifest) are loaded automatically during deployment according to the selected variant; repeated loads are idempotent
- **Backup & restore** — Each data volume is just a vhd file; one-click snapshots and restores from the backup page
- **LAN sharing (team collaboration)** — The service console shows a live "teammate access address" so peers on the same LAN can reach your Overleaf with just a browser, register accounts and co-edit in real time; the address refreshes automatically on network changes, and VPN / TUN virtual adapters (Mihomo / Clash, etc.) are excluded automatically. See the "Team Collaboration" chapter
- **Remote collaboration** — EasyTier-based virtual networking: the host opens the local Overleaf service with a one-click invite code; remote peers enter the code to join the virtual network and get port forwards automatically — no public IP or port mapping needed. See the "Remote Collaboration" chapter
- **Portable account isolation** — The account database, credentials and user data all live in the launcher's own folder; copy the whole folder for a fully portable install, and multiple copies never share or collide accounts
- **Pro feature parity** — Settings → Ayakaleaf: Git-Bridge (git operations + MCP Server), AI assistant, template gallery, two-way GitHub / GitLab / Gitea sync, Zotero / Mendeley references, registration policy (open / domain allowlist / closed), reCAPTCHA, leaked-password checks, branding, advanced environment variables (arbitrary pass-through such as SSO); all switches default to off, aligned with ayakaleaf-pro behavior
- **Multi-language UI** — 18 interface languages bundled with the image, enabled on demand (English fixed to the main port, each other language on consecutive ports, +1 each); switchable both inside the workbench and from the footer language selector
- **Theme colors** — Four built-in theme colors to switch at will — Sky Blue, Totoro Blue, Crash Blue and Campfire Orange (Campfire's signature #E2680C) — adapted to both light and dark modes
- **Admin & account linkage** — The first local user automatically becomes a server admin; the Account Center links straight to the admin panel with one-click Ayakaleaf password resets; deleting a local user also cleans up the server account
- **Full backup** — All data volumes + account credentials + the local user database + launcher settings packed into a single zip with one click, and restored with one click
- **Image version management** — The image page shows the image and version currently in use; after importing a newer image package and restarting the service, containers are rebuilt automatically with the new image (data volumes survive across versions)
- **Online updates** — An auto-update channel built on GitHub Releases: new versions are checked at startup per your settings, downloaded with SHA256 verification and the program files are replaced automatically; can be turned off in Settings. See the "Online Updates" chapter

## 📐 Editor Sync

Select a piece of formula code in the workbench and the PDF pane jumps to the rendered formula and highlights it — no need to click Sync to PDF in the toolbar:

- Supports `$…$`, `$$…$$`, `\(…\)`, `\[…\]` and `\begin{}` environments such as `equation`, `align`, `gather`, `multline`, `split`, `cases`, `matrix`
- Only formula selections trigger it; selecting plain text, copying and pasting are unaffected
- Requires a successfully compiled PDF; it stays silent when no PDF exists (no error toasts)
- Highlight boxes come from SyncTeX positions, refined by local geometry disambiguation and ink scanning, so they wrap the actual content even in multi-line environments and multi-column layouts

## 🔄 Online Updates

Starting with 2.1.0 the launcher ships an online update channel built on GitHub Releases:

- **Behavior**: at startup new versions are checked according to the update mode chosen in "Settings → Launcher Update" (auto download & install / auto download & announce / announce only / never check); downloads are verified with SHA256 and the program files are replaced automatically. A manual "Check for updates" button is also available on that page
- **Master switch**: unchecking "Enable update channel" stops all update checks and downloads
- **Download acceleration**: downloads prefer the ghfast.top / gh-proxy.com mirrors and fall back to a direct connection when they are unavailable
- **Independent version line**: version numbers are maintained independently for this project and are unrelated to upstream PCL2-CE versions
- **Legacy builds**: 2.0.0 and earlier did not ship the update channel — download the new exe manually once and replace the old file; online updates work from then on. Existing account data and settings survive upgrades

## 🤝 Team Collaboration & Remote Collaboration

Pick the collaboration mode based on how your peers connect to you:

| Scenario | Feature | Requirements |
|---|---|---|
| Same LAN (dorm / lab / office network) | Team collaboration (LAN sharing) | Peers only need a browser — no software to install |
| Remote / different networks | Remote collaboration (EasyTier virtual networking) | Both sides need internet access; peers run the full launcher or the lightweight client AyakaCollabLite |

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

1. Start the service on the "Service" page, then open the "Collab" page and click "Start Collaboration"
2. Click "Copy Invite Code" and send the generated code (format `A/XXXX-XXXX-XXXX-XXXX`) to your teammates
3. Wait for peers to join; the page shows the number of connected public relay nodes in real time, and the member list refreshes every 2 seconds, showing each member's connection type (direct / relayed) and latency
4. After a peer joins, a collab account is created automatically for them (tagged with a "Collab" badge in the "Account Center" and showing the peer's display name); it can be disabled, password-reset or deleted like any normal account

**Guest**

1. Open this launcher, go to the "Collab" page and paste the invite code (pasting a whole block of text containing the code is fine — the active code inside is recognized automatically); optionally enter a display name (defaults to the local user name, used to generate the collab account) and click "Join Collaboration"
2. Joining is fully automatic: connect to public nodes → find the host → establish one local port forward per interface language enabled by the host (the host's port number is reused locally; if occupied, a free port is picked automatically)
3. Once joined, the page lists the per-language access addresses (e.g. `Simplified Chinese: http://localhost:8081`); click "Open Workbench" to enter the host's service (switchable between the interface languages the host has enabled), or open the listed addresses directly in a browser
4. Automatic collab-account pairing: after joining, the launcher creates the collab account and signs you in automatically (no registration needed) — "Open Workbench" enters already signed in, ready for real-time co-editing with the host; joining the same invite code again reuses the same account instead of creating a duplicate. If pairing fails, the port forwards still work; the page shows the reason in Chinese with a one-click retry

**Both sides**

- The collab session survives page switches; "Stop Collaboration" disconnects at any time, and the network component is cleaned up automatically when the launcher exits
- Once the host stops the collaboration, all peers lose access immediately
- If it sits on "Connecting to public nodes…" for a long time, the built-in nodes are usually unreachable due to a restricted network — configure custom relay nodes (see below)

**Privacy & security**

- Traffic is end-to-end encrypted; public relay nodes only forward ciphertext and cannot decrypt contents; P2P direct connection is preferred whenever possible, with relay as fallback only
- The host-side whitelist only exposes the Overleaf service ports — no other local port is reachable
- The invite code is the network key (16 characters, with a checksum digit against typos); do not share it publicly. If it leaks, the host can stop the collaboration and click "Start Collaboration" again — a new code is generated and the old one becomes invalid
- Advanced options via the `AyakaleafCollab*` config entries: "hide IP" (neither side can see the other's virtual IP), "direct only" (disables the relay fallback)

**Network nodes**

Five community relay nodes are connected automatically by default; custom nodes (`tcp://` / `udp://` / `wss://` / `https://`, comma-separated) can be configured when the network is restricted and are appended after the built-in ones.

### 🪶 Lightweight Collab Client (AyakaCollabLite)

Peers who only need to join someone else's collaboration — not the local service stack — can use the lightweight client AyakaCollabLite instead:

- **≤ 50MB, install-free, zero runtime dependencies**: unzip and go, double-click `CollabLite.bat` to start (a PowerShell 5.1 WinForms GUI; runs on the environment bundled with Windows 10/11)
- Focused on remote collaboration: paste the host's invite code (display name optional) → automatic virtual networking (EasyTier v2.6.4 bundled, the same version as the main program to guarantee protocol compatibility) → local port forwards per interface language enabled by the host → the collab account is created automatically and its email and password are shown in the window (each with a "Copy" button — paste them once each on the browser sign-in page and the browser stays signed in) → open the forwarded address in a browser and co-edit
- Fully interoperable with the full launcher's collaboration feature: the host runs "Start Collaboration" in the full launcher, and peers join with either the full launcher's "Join Collaboration" or the lightweight client — networking parameters and collab-account rules are identical
- The package `AyakaCollabLite_<version>_x64.zip` is distributed alongside the main program's Release, with a `.sha256` checksum file

## 📁 Local Folder Sync (Two-way)

Bind an Ayakaleaf project to a local folder with two-way sync and get a hybrid "edit locally, compile on Overleaf" workflow:

1. Open the project to bind in the workbench and click the "⟳ Local Sync" button at the top of the window
2. In the management window that opens, click "Bind current project to a folder…", pick a local folder (an empty one is recommended) and confirm
3. Once bound, the first sync round pulls the project content down automatically; from then on, both directions sync automatically:
   - **Local → workbench**: when a local editor (VS Code / TeXstudio / Notepad, etc.) saves a file, it is pushed to the server automatically and connected collaborators see the update in real time
   - **Workbench → local**: changes made in the workbench (including edits by collaborators) are pulled back to the local folder automatically
   - Local watching is near-real-time (~1.5s debounce), the remote polling cycle is 30 seconds; syncing is skipped automatically while the service is down and resumes when it is back

**Conflict rules (never a silent overwrite)**: when the same file is changed on both ends with different content, the remote version is written to disk as the current one and the local modification is saved as a conflict copy `name.conflict-YYYYMMDD-HHMMSS.ext`, with an explicit prompt — both versions are kept and you decide which one to keep.

**Binding management**: for each binding, the management window offers "Sync now", "Pause / resume", "Open folder" and "Unbind"; unbinding only stops automatic syncing — local files and the manifest are kept. Binding configuration persists with the launcher and watching resumes automatically after a restart.

**v1 scope limits** (to evolve in later versions):

- Deletes / renames are not propagated automatically: files deleted locally stay on the remote, and files deleted remotely stay local
- Modifying an existing binary file (e.g. an image) cannot be pushed (replace it in the workbench instead); newly added binary files upload fine
- Compile artifacts and temporary files (`.aux` / `.log` / `.synctex.gz`, etc.) are excluded automatically and never synced to the workbench

## 💻 Supported Platforms

| Operating system | Support | Requirements |
|---|---|---|
| Windows 10 21H2+ / Windows 11 (x64 / ARM64) | ✅ Fully supported | [.NET 10 Desktop Runtime](https://get.dot.net/10) (matching the executable's architecture) + WSL 3.0+ |
| Windows 10 1809 - 21H2 | ⚠️ Requires manually upgrading WSL to 3.0+ | Same as above |
| Windows 7 / 8 / 8.1 | ❌ Not supported | / |
| macOS / Linux | ❌ Not supported | WPF, WebView2 and WSLC containers are Windows-only |

## 🚀 Quick Start

1. Download the executable `AyakaLauncher_Release_x64.exe` (or `AyakaLauncher_Release_ARM64.exe` on ARM64 devices) from [Releases](https://github.com/BluesilveEmperor/Ayaka-Leaf-PCL/releases); get the runtime archive `runtime.7z` from the "Primary download sources (cloud drives)" section below; a fallback download for the executable is listed under "Alternative download: launcher executable" below
2. Put the exe and the runtime archive in the same folder and extract it there to get `runtime\images\` (~6.3GB, dual-variant main images included — see the "Embedded runtime" section below)
3. Install the [.NET 10 Desktop Runtime](https://get.dot.net/10) matching the executable's architecture
4. Enable WSL: in an elevated PowerShell, run `wsl --install --no-distribution`, then run `wsl --update`
5. Launch `AyakaLauncher_Release_<platform>.exe` → register a local user and sign in
6. In the service console, click in order: **Deploy** → **Start Services** → **Open Workbench** (service address `http://localhost:8080`, port configurable)

> ⚠️ `runtime\` holds the container image runtime and must stay **next to the exe — do not split it up or rename it**. The exe itself contains no images; deployment fails without `runtime\images\`.

## ⚠️ Functionality Limitations

- **Email unavailable**: the local instance has no SMTP configured, so confirmation / password-recovery emails cannot be sent (registration skips email confirmation automatically); if you pick the "domain allowlist" registration policy, activation emails equally cannot be delivered — take this into account before choosing it
- **Sandboxed compilation**: the WSL container environment cannot run sibling containers, so TeX Live's full sandboxed compilation is unavailable and shown greyed-out in Settings; compilation still happens inside the Ayakaleaf container and is unaffected. **Pandoc document import** has been restored with the new runtime image (Pandoc 3.x built in; Word / Markdown import works), and the Settings page shows the actual detection result
- **OAuth callback reachability**: GitHub / GitLab / Gitea / Zotero / Mendeley authorization callbacks require visitors to reach the site address; LAN mode uses the machine's local IP, and authorization may fail when it is not reachable from the public internet
- **Credential safety**: sensitive configuration such as AI keys and OAuth client secrets is stored in the local encrypted configuration store; container rebuild fingerprints only take a SHA256 hash and never log secrets

## 📦 Embedded Runtime (`runtime`)

`runtime\images\` holds the WSLC container image tarballs, loaded automatically by the app during deployment (idempotent — repeated loads have no side effects). Layout:

```
AyakaLauncher.exe
runtime\
└─ images\
   ├─ sharelatex-server-pro.tar.gz       # Full Overleaf image (~3.5GB, Pandoc 3.x + curated full macro package set built in)
   ├─ sharelatex-server-pro-cn.tar.gz    # Trimmed "Chinese-common" Overleaf image (~1.7GB, Chinese typesetting + retained common macro packages)
   ├─ git-bridge.tar.gz                  # Git-Bridge / MCP service image (~387MB, required — Git operations and third-party sync depend on it)
   ├─ mongo-8.0.tar.gz                   # MongoDB 8.0 (~300MB)
   ├─ redis-7.tar.gz                     # Redis 7 (~42MB)
   ├─ images-manifest.json               # Image manifest (variants, tags, SHA256, build time)
   └─ *.sha256                           # SHA256 checksum files for each image tarball
```

**Dual-variant main images**: the Overleaf main image comes in two variants — full / Chinese-common. Deployment loads the tarball for the selected variant (the unselected variant's tarball is skipped automatically; delete the one you don't use if disk space is tight):

| Variant | Image tarball | Size | Best for |
|---|---|---|---|
| Full (default) | `sharelatex-server-pro.tar.gz` | ~3.5GB | Curated full macro package set out of the box, covering Chinese support and common journal / template packages |
| Chinese-common (trimmed) | `sharelatex-server-pro-cn.tar.gz` | ~1.7GB | Trimmed for common Chinese typesetting scenarios; missing packages can be topped up online later via the macro package panel (tlmgr) — a closed loop |

All tarballs together weigh about 6.3GB and are **not in the git repository** (already excluded by `.gitignore`); official distribution goes through the `runtime.7z` in the "Primary download sources (cloud drives)" section below (Releases no longer attach the runtime). To rebuild the images yourself, the repository ships three TeX Live layer Dockerfiles: `tools/runtime/texlive-packages/Dockerfile` (curated set, the default for the full variant), `tools/runtime/texlive-full/Dockerfile` (scheme-full, everything) and `tools/runtime/texlive-cn/Dockerfile` (Chinese-common trimmed), built in one go via `tools/runtime/build-images.ps1` / `build-images.py` (chaining the Pandoc layer → TeX Live layer → `wslc save` → SHA256 manifest). Pick any of these ways to get them:

| Method | Applies to | Notes |
|---|---|---|
| Cloud-drive primary source `runtime.7z` | All | Download from any share link in the "Primary download sources (cloud drives)" section below and extract next to the exe to get `runtime\images\` |
| Self-service export via `wslc` | mongo / redis | Standard images: `wslc pull mongo:8.0`, then `wslc save -o runtime\images\mongo-8.0.tar.gz mongo:8.0` (same for redis) |
| This repository's build scripts | Customized sharelatex dual-variant / git-bridge images | `pwsh tools\runtime\build-images.ps1` (curated set by default; `-Variant cn` for the trimmed Chinese variant; `-Variant full` for scheme-full) |

**Where to put them**: whether from the extracted `runtime.7z` or an all-in-one bundle, everything goes next to the exe; for source development put them in the **repository root** `runtime\images\` (junction-mounted into the output directory after building — see the next section).

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

**runtime junction mechanism**: the repository-root `runtime\` folder is where the image tarballs actually live. After each build, the `LinkAyakaleafRuntime` target in the csproj links the output directory `bin\<Configuration>\net10.0-windows\runtime` to it via a directory junction (`dotnet publish` output directories get the same treatment from `LinkAyakaleafRuntimeOnPublish`) — avoiding copying ~6.3GB on every build. The app reads `runtime\images` next to the exe at runtime, so changes to the repository-root `runtime\` take effect on the next launch.

**First run**: register a local user and sign in → in the service console click **Deploy** (loads the images and initializes the MongoDB replica set automatically) → **Start Services** → **Open Workbench**. Use `wslc images` to confirm the 4 images and `wslc list` to confirm the 3 containers (mongo / redis / overleaf; plus the git-bridge container when Git-Bridge is enabled) are all up.

## 📦 Release & Distribution

The executable is published on [GitHub Releases](https://github.com/BluesilveEmperor/Ayaka-Leaf-PCL/releases); the runtime archive `runtime.7z` comes from the "Primary download sources (cloud drives)" section below. **Releases ship the executable and the lightweight client only — the runtime is no longer attached.** Attachments:

| Attachment | Description |
|---|---|
| `AyakaLauncher_Release_x64.exe` | x64 main executable, framework-dependent single file; requires the [.NET 10 Desktop Runtime](https://get.dot.net/10) |
| `AyakaLauncher_Release_ARM64.exe` | ARM64 main executable, framework-dependent single file; same runtime requirement |
| `AyakaCollabLite_<version>_x64.zip` (+ `.sha256`) | Lightweight collab client (≤ 50MB); lets remote peers join without the full launcher — unzip and run |

> 📌 **Runtime note**: `runtime.7z` contains the dual-variant main images (full ~3.5GB / Chinese-common ~1.7GB), both with Pandoc 3.x and Chinese support built in; the full variant works out of the box, while the Chinese-common variant can top up missing macro packages online via the macro package panel.

### Primary download sources (cloud drives)

The runtime archive `runtime.7z` is distributed via cloud-drive shares. **Every one of the following is a primary download source** — pick whichever works best for you. More shares will be added over time.

| Cloud drive | Share link |
|---|---|
| Aliyun Drive | [Aliyun Drive share](https://www.alipan.com/s/NYaZyrvbRmX) |
| Other cloud drives | To be added |

> 📁 **Organised by version**: the share is split into folders per launcher version — open the folder matching your executable (currently `v2.1.2`) before downloading `runtime.7z`. The launcher's fallback share uses the same per-version folder layout; take both files from the **same version folder** and never mix versions.

If the archive isn't there yet, it's either still crawling up on a slow connection or the maintainer has been a bit lazy — check back later. Extract `runtime.7z` with a 7z-capable tool into the folder containing the exe to get `runtime\images\`; then follow the "Deployment" steps below.

### Alternative download: launcher executable (Aliyun Drive)

GitHub Releases remains the **primary channel** for the executable. If it is inconvenient, the same `AyakaLauncher_Release_x64.exe` / `AyakaLauncher_Release_ARM64.exe` builds can also be fetched from this fallback share (check the share page for the actual files):

- [Aliyun Drive share](https://www.alipan.com/s/XBcaUK75hrU)

> 📁 The share is **organised into folders per launcher version** — open the folder for your release (currently `v2.1.2`) before downloading the exe, and keep it on the same version as `runtime.7z` from the section above.

> ⚠️ This share contains **the launcher executable only**. The runtime archive `runtime.7z` is a separate resource — get it from the "Primary download sources (cloud drives)" section above. The two links are not interchangeable.

### Deployment

1. Put `AyakaLauncher_Release_<platform>.exe` and the runtime archive `runtime.7z` in the same folder
2. Extract the archive there to get `runtime\images\` (containing the `sharelatex-server-pro`, `git-bridge`, `mongo-8.0` and `redis-7` image tarballs)
3. Run the exe and follow "Quick Start" to complete the first deployment

### How the runtime is distributed

The runtime images are not in the repository, so a clean CI machine cannot produce a runtime archive out of thin air. Therefore **the runtime is distributed only as `runtime.7z` through the cloud-drive primary sources; Releases no longer ship it**. Running `pwsh release.ps1` / `python release.py` locally still produces `release\runtime.zip` for local bundles and offline distribution, but it is not uploaded to any Release.

### Why the runtime is not in the repository

The largest single image tarball is ~3.5GB (`sharelatex-server-pro`, full variant), far beyond GitHub's 100MB per-file limit for regular repositories, so the runtime images are kept out of git and distributed as `runtime.7z` through the cloud-drive primary sources (Releases no longer attach the runtime). The `runtime\` folder inside the repository is for local development only and is already ignored by `.gitignore`.

### Local packaging

The packaging script has two equivalent implementations (`release.ps1` / `release.py`); pick either:

```powershell
# PowerShell version: build x64 and ARM64 separately
pwsh -ExecutionPolicy Bypass -File release.ps1 -Configuration Release -Platform x64
pwsh -ExecutionPolicy Bypass -File release.ps1 -Configuration Release -Platform ARM64
```

```bash
# Python version (requires Python 3.9+, standard library only): x64 and ARM64
python release.py --configuration Release --platform x64
python release.py --configuration Release --platform ARM64
```

```powershell
# Optional: also produce the all-in-one bundle (use --with-bundle in the Python version)
pwsh -ExecutionPolicy Bypass -File release.ps1 -Configuration Release -Platform x64 -WithBundle
pwsh -ExecutionPolicy Bypass -File release.ps1 -Configuration Release -Platform ARM64 -WithBundle
```

```powershell
# Optional: also build the lightweight collab client with the release (use --with-collab-lite in the Python version)
pwsh -ExecutionPolicy Bypass -File release.ps1 -Configuration Release -Platform x64 -WithCollabLite
```

> `runtime.zip` is platform-independent, so the ARM64 run only re-packs and overwrites it with identical content. Releases no longer ship the runtime; it is distributed as `runtime.7z` through the cloud-drive primary sources.

Before packing, the script first checks that a `runtime\` folder exists at the repository root. If it is missing, it prompts you to download `runtime.7z` from a cloud drive first, extract it to the repository root and run the script again. If the folder exists, it verifies that the four required images — `sharelatex-server-pro.tar.gz`, `git-bridge.tar.gz`, `mongo-8.0.tar.gz` and `redis-7.tar.gz` — are all present under `runtime\images\`, aborting with an error and a hint to fill the gap if any is missing (restorable by extracting `runtime.7z` from a cloud drive). It then validates the image manifest `images-manifest.json` (when present): every tarball declared in the manifest (including the Chinese-common variant) must exist, and each companion `.sha256` file must match the manifest — preventing a truncated or tampered runtime archive from being produced.

| Artifact | Produced when | Description |
|---|---|---|
| `release\AyakaLauncher_<configuration>_<platform>.exe` | by default | The main executable (x64 / ARM64 built separately), named exactly like the Release attachment (no version number) |
| `release\runtime.zip` | by default | Bundled runtime image archive (dual-variant main images included), about 6.3GB, for local bundles only — no longer published as a Release attachment |
| `release\AyakaLauncher_<version>_<configuration>_<platform>.zip` | with `-WithBundle` | exe plus runtime all-in-one bundle (dual-variant main images included), about 6.3GB, for offline distribution |
| `release\AyakaCollabLite_<version>_x64.zip` (+ `.sha256`) | hash-checked by default / built first with `-WithCollabLite` | Lightweight collab client package (≤ 50MB); if it already exists, its hash is verified and it is included in the artifact list; a warning prompts a build if it is missing |
| `dist\AyakaLauncher.exe` | auto-synced when `dist\` exists | The day-to-day local run directory (runtime junction-mounted) gets refreshed on every release; skipped with a warning if the launcher is currently running |

Packaging uses only standard libraries (PowerShell's `System.IO.Compression` / Python's `zipfile`), no third-party dependencies. Day-to-day local runs need no packaging: `dist\` and the build output directory already link `runtime` through junctions.

## 🔒 License

- `Plain Craft Launcher 2/` is licensed under the [upstream custom license](Plain%20Craft%20Launcher%202/LICENCE)
- All other directories are licensed under the [Apache License 2.0](LICENSE)

## 🙏 Acknowledgements

- [PCL Community Edition](https://github.com/PCL-Community/PCL-CE) — UI framework and engineering foundation
- [Meloong-Git/PCL](https://github.com/Meloong-Git/PCL) — the upstream original project
- [Overleaf](https://github.com/overleaf/overleaf) — the bundled collaborative LaTeX service
- [Ayakaleaf](https://github.com/ayaka-notes/ayakaleaf-pro) — the upstream customized Overleaf server; the server source of this project's bundled runtime and workbench
- [Campfire](https://github.com/Xocio/Campfire) — design reference for the remote / cross-network collaboration scheme and the "Campfire Orange" theme color
- [LaTeX-Workshop](https://github.com/James-Yu/LaTeX-Workshop) — the source of the full symbol set adopted by the symbol palette extension

[简体中文](README.md) | [English](README-EN.md) | **繁體中文**

<div align="center">

<img src="Plain Craft Launcher 2/Images/icon.ico" alt="Logo" width="80" height="80">

# AyakaLauncher

**Ayakaleaf 一體化桌面端** · 基於 [PCL Community Edition](https://github.com/PCL-Community/PCL-CE) 二次開發

</div>

AyakaLauncher 是 [Ayakaleaf](https://github.com/ayaka-notes/ayakaleaf-pro)（Overleaf 伺服器定製版）的一體化桌面用戶端，基於 PCL Community Edition 的框架二次開發而來。

本專案已與 Minecraft 啟動功能**完全解耦**：不再包含任何 MC 相關程式碼、頁面與資源。

## ✨ 功能特性

- **本機使用者系統** —— 註冊 / 登入 / 多使用者切換（SQLite + PBKDF2），每使用者獨立資料目錄，啟動登入門禁
- **服務台** —— 一鍵部署 / 啟動 / 停止內建 Overleaf 服務（MongoDB 8.0 + Redis 7 + Overleaf；啟用 Git-Bridge 時另含 git-bridge 容器），自動初始化 MongoDB 副本集，服務就緒自動探測
- **容器執行時** —— 基於 Windows WSL 3.0+ 內建容器能力（WSLC），**無需 Docker Desktop**，資料卷為獨立 vhd 檔案
- **工作台** —— 內嵌 WebView2 開啟 Overleaf，自動開戶 + 自動登入，免手動輸入
- **公式聯動** —— 左側選取 LaTeX 公式程式碼，右側 PDF 自動高亮對應位置並捲動定位（重用 Overleaf 內建 SyncTeX）
- **符號面板拓展** —— 內建符號面板拓展為 LaTeX-Workshop 全量符號集：10 個中文分類、760+ 符號（在 Overleaf 原生符號之上合併增量），支援按名稱 / 關鍵字 / Unicode 搜尋，服務啟動時自動生效，無需手動操作
- **映像檔管理** —— 內建映像檔 tar.gz（`runtime/images`），部署時自動載入，重複載入冪等
- **備份還原** —— 資料卷即 vhd 檔案，頁面內一鍵快照 / 還原
- **區域網路共享（團隊協作）** —— 服務台即時顯示「隊友存取位址」，同一區域網路的隊友用瀏覽器即可存取、註冊帳號並即時協同編輯；網路切換後位址自動重新整理，並自動排除 VPN / TUN 虛擬網卡（Mihomo / Clash 等），詳見「團隊協作」章節
- **遠端協作** —— 基於 EasyTier 的異地組網：房主一鍵產生邀請碼開放本機 Overleaf 服務，異地隊友輸碼自動加入虛擬網路並轉發連接埠，無需公網 IP 或連接埠映射，詳見「遠端協作」章節
- **帳號資料隔離** —— 帳號庫、憑證與使用者資料全部隨啟動器目錄存放，整個目錄複製即成可攜版，多個副本互不串號
- **主題色** —— 天空藍、龍貓藍、當機藍、營火橘（Campfire 標誌色 #E2680C）四款內建主題色隨選隨切，淺色 / 深色模式均適配

## 📐 編輯器聯動

在工作台裡用滑鼠選取一段公式程式碼，右側 PDF 會自動跳到公式渲染位置並高亮，不必再點工具列的 Sync to PDF：

- 支援 `$…$`、`$$…$$`、`\(…\)`、`\[…\]`，以及 `equation`、`align`、`gather`、`multline`、`split`、`cases`、`matrix` 等 `\begin{}` 環境
- 只對公式選取生效；選取一般文字、複製貼上不受影響
- 右側需已成功編譯出 PDF 才會觸發（未編譯時靜默略過，不彈錯誤提示）
- 高亮框來自 SyncTeX 記錄的位置，多行環境可能只涵蓋該處首行

## 🤝 團隊協作與遠端協作

按隊友與你的網路關係選擇協作方式：

| 場景 | 功能 | 要求 |
|---|---|---|
| 同一區域網路（宿舍 / 實驗室 / 辦公室內網） | 團隊協作（區域網路共享） | 隊友僅需瀏覽器，無需安裝任何軟體 |
| 異地 / 不同網路 | 遠端協作（EasyTier 虛擬組網） | 雙方都能存取網際網路；隊友需執行本啟動器 |

### 🏠 團隊協作（區域網路共享）

同一區域網路的隊友用瀏覽器即可存取你的 Overleaf 服務、註冊帳號並即時協同編輯。

**房主（你）**

1. 在「服務」頁啟動服務
2. 勾選「網路與協作 → 允許區域網路存取（團隊協作）」，頁面隨即顯示「隊友存取位址」（如 `http://192.168.1.100:8080`；啟用簡體中文介面時會一併顯示中文連接埠位址）
3. 重新啟動服務使開關生效（該開關修改後需重新啟動服務）
4. 把「隊友存取位址」發給同一區域網路的隊友

**隊友**

1. 在同一區域網路的任意裝置（電腦 / 平板 / 手機均可）用瀏覽器開啟房主分享的位址
2. 首次存取點擊註冊建立帳號（或登入既有帳號）
3. 之後即可建立 / 加入專案，與房主及其他隊友即時協同編輯

**說明**

- 位址取房主電腦的區域網路 IP，網路切換（Wi-Fi ↔ 熱點等）後自動重新整理；已自動排除 VPN / TUN 虛擬網卡（Mihomo / Clash 等），不會誤顯示代理網卡 IP
- 關閉開關並重新啟動服務後，服務恢復為僅本機可存取
- 隊友共享房主的服務與專案空間，人數上限取決於房主電腦效能

### 🌐 遠端協作（異地組網）

異地隊友無需公網 IP 或連接埠映射，憑邀請碼即可加入你的 Overleaf 服務一起協作。虛擬網路基於 [EasyTier](https://github.com/EasyTier/EasyTier)（Apache-2.0）；首次使用時自動從官方發佈渠道下載協作元件，無需手動安裝。

**房主**

1. 在「服務」頁啟動服務後進入「協作」頁，點擊「開啟協作」
2. 點擊「複製邀請碼」，把產生的邀請碼（格式 `A/XXXX-XXXX-XXXX-XXXX`）發給隊友
3. 等待隊友加入；頁面即時顯示已連線的公共中繼節點數，成員列表每 2 秒自動重新整理，逐個顯示各成員的連線方式（直連 / 中繼）與延遲

**隊友**

1. 開啟本啟動器進入「協作」頁，貼上邀請碼（直接貼上包含邀請碼的整段文字也可以，會自動識別其中生效的邀請碼），點擊「加入協作」
2. 加入過程全自動：連線公共節點 → 尋找房主 → 按房主啟用的介面語言逐個建立本機連接埠轉發（本機沿用房主同連接埠號，被占用時自動改用空閒連接埠）
3. 加入成功後頁面列出各語言的存取位址（如 `简体中文：http://localhost:8081`）；點擊「開啟工作台」進入房主的服務（可在房主已啟用的介面語言之間切換），也可以直接用瀏覽器開啟列出的位址
4. 首次存取需在房主的服務中註冊或登入帳號，之後即可與房主即時協同編輯

**雙方通用**

- 協作會話切頁不中斷；「停止協作」隨時可斷開，啟動器退出時自動清理網路元件
- 房主停止協作後，所有隊友立即失去存取
- 若長時間停留在「正在連線公共節點…」，通常是網路受限打不通內建節點，可設定自訂中繼節點（見下）

**隱私與安全**

- 流量端對端加密，公共中繼節點只轉發密文、無法解密內容；能直連時優先 P2P 直連，中繼僅作備援
- 房主側白名單僅開放 Overleaf 服務連接埠，本機其他連接埠對隊友不可達
- 邀請碼即網路金鑰（16 位、含校驗位防輸入錯誤），請勿公開分享；若有洩露，房主停止協作後重新「開啟協作」會產生新邀請碼，舊邀請碼隨之作廢
- 支援「隱藏 IP」（雙方互不可見虛擬 IP）、「僅直連」（停用中繼備援）等進階選項（`AyakaleafCollab*` 設定項）

**網路節點**

預設自動連線 5 個社群公共中繼節點；網路受限時可設定自訂節點（`tcp://` / `udp://` / `wss://` / `https://` 格式，多個以逗號分隔，附加在內建節點之後）。

## 💻 支援平台

| 作業系統 | 支援情況 | 環境需求 |
|---|---|---|
| Windows 10 21H2+ / Windows 11 (x64 / ARM64) | ✅ 完整支援 | [.NET 10 Desktop Runtime](https://get.dot.net/10)（架構與主程式一致）+ WSL 3.0+ |
| Windows 10 1809 - 21H2 | ⚠️ 需手動升級 WSL 至 3.0+ | 同上 |
| Windows 7 / 8 / 8.1 | ❌ 不支援 | / |
| macOS / Linux | ❌ 不支援 | WPF、WebView2 與 WSLC 容器皆為 Windows 專屬 |

## 🚀 快速開始

1. 從 [Releases](https://github.com/BluesilveEmperor/Ayaka-Leaf-PCL/releases) 下載主程式 `AyakaLauncher_Release_x64.exe`（ARM64 裝置改用 `AyakaLauncher_Release_ARM64.exe`）與執行環境包 `runtime.zip`
2. 將 exe 與 `runtime.zip` 放在同一目錄，解壓縮 `runtime.zip` 得到 `runtime\images\`（約 4.2GB，詳見下文「發佈與分發」）
3. 安裝 [.NET 10 Desktop Runtime](https://get.dot.net/10)（架構與主程式一致）
4. 啟用 WSL：以系統管理員 PowerShell 執行 `wsl --install --no-distribution`，完成後執行 `wsl --update`
5. 啟動 `AyakaLauncher_Release_<平台>.exe` → 註冊本機使用者並登入
6. 服務台頁面依序點擊：**開始部署** → **啟動服務** → **開啟工作台**（服務位址 `http://localhost:8080`，連接埠可設定）

> ⚠️ `runtime\` 是容器映像檔執行環境，必須與 exe 保持**同級、不可拆分或改名**。exe 本身不含映像檔，缺少 `runtime\images\` 時無法部署服務。

## 🛠️ 本機開發

### 前置要求

| 相依套件 | 版本 | 安裝方式 |
|---|---|---|
| .NET SDK | 10 | [get.dot.net/10](https://get.dot.net/10) |
| WSL | 3.0+ | 系統管理員 PowerShell：`wsl --install --no-distribution`，再 `wsl --update` |
| 容器執行環境 | WSLC | 隨 WSL 3.0+ 內建，**無需 Docker Desktop** |

### 建置步驟

```powershell
# 1. 複製倉庫
git clone https://github.com/BluesilveEmperor/Ayaka-Leaf-PCL.git
cd Ayaka-Leaf-PCL

# 2. 擺放 runtime：把上節取得的四個映像檔包放到倉庫根目錄
#    runtime\images\sharelatex-server-pro.tar.gz
#    runtime\images\git-bridge.tar.gz
#    runtime\images\mongo-8.0.tar.gz
#    runtime\images\redis-7.tar.gz

# 3. 建置並執行
dotnet run --project "Plain Craft Launcher 2"
```

也可以用 Visual Studio / Rider / VS Code 開啟方案直接 F5。

**runtime junction 機制**：倉庫根目錄的 `runtime\` 是映像檔包的真實存放處；每次建置後，csproj 中的 `LinkAyakaleafRuntime` Target 會把輸出目錄 `bin\<設定>\net10.0-windows\runtime` 以目錄聯結（junction）指向它（`dotnet publish` 輸出目錄由 `LinkAyakaleafRuntimeOnPublish` 同樣處理）——避免每次建置複製 4.2GB，應用程式執行時從 exe 旁的 `runtime\images` 讀取。對倉庫根目錄 `runtime\` 的修改會在下次執行時生效。

**首次執行流程**：註冊本機使用者並登入 → 服務台點擊 **開始部署**（自動載入映像檔、初始化 MongoDB 副本集）→ **啟動服務** → **開啟工作台**。可用 `wslc images` 確認四個映像檔、`wslc list` 確認三個容器（mongo / redis / overleaf；啟用 Git-Bridge 時另見 git-bridge 容器）均已就緒。

## 📦 發佈與分發

正式版本統一從 [GitHub Releases](https://github.com/BluesilveEmperor/Ayaka-Leaf-PCL/releases) 下載，附件包含：

| 附件 | 說明 |
|---|---|
| `AyakaLauncher_Release_x64.exe` | x64 主程式，Framework-dependent 單一檔案，需 [.NET 10 Desktop Runtime](https://get.dot.net/10) |
| `AyakaLauncher_Release_ARM64.exe` | ARM64 主程式，Framework-dependent 單一檔案，需求同上 |
| `runtime.zip` | 內嵌容器映像檔執行環境包，約 4.2GB |

### 備用下載源（阿里雲盤）

若 GitHub Releases 下載緩慢，可改用 [阿里雲盤分享連結](https://www.alipan.com/s/NYaZyrvbRmX) 取得 `runtime.7z`（由維護者後續上傳，以分享頁實際檔案為準）。以支援 7z 的解壓工具解壓到 exe 同級目錄，得到 `runtime\images\`；其餘部署步驟與 `runtime.zip` 完全相同。

### 部署步驟

1. 將 `AyakaLauncher_Release_<平台>.exe` 與 `runtime.zip` 放在同一目錄
2. 在該目錄解壓縮 `runtime.zip`，得到 `runtime\images\`（內含 `sharelatex-server-pro`、`git-bridge`、`mongo-8.0`、`redis-7` 四個映像檔包）
3. 執行 exe

`runtime\` 必須與 exe **同級、不可拆分或改名**。

### 首次發佈（runtime.zip 來源）

CI 無法憑空產生 `runtime.zip`——執行環境映像檔不進入倉庫，乾淨的建置機器上也沒有這些檔案。因此：

- **首次發佈**（此前沒有任何包含 `runtime.zip` 的 Release）：先在本機執行 `pwsh release.ps1` 得到 `release\runtime.zip`，手動上傳到該 Release；或手動觸發工作流時，在 `runtime_source_tag` 輸入中指定已含 `runtime.zip` 的 Release 標籤。
- **之後的發佈**：`package_runtime` job 會自動尋找最近一個包含 `runtime.zip` 的非草稿 Release，下載並重用其中的 `runtime.zip` 上傳到新 Release，無需再次手動上傳。

### 為何 runtime 不進入程式碼倉庫

單一映像檔包最大約 3.46GB（sharelatex-server-pro，內含 scheme-full 全量 TeX Live），遠超 GitHub 一般倉庫 100MB 的單檔上限，因此執行環境映像檔不進入 git，改由 Release 附件 `runtime.zip` 分發。倉庫內的 `runtime\` 目錄僅供本機開發使用，已在 `.gitignore` 中忽略。需要自行重建該映像檔時，倉庫提供兩種 Dockerfile：`tools/runtime/texlive-full/Dockerfile`（scheme-full 全量）與 `tools/runtime/texlive-packages/Dockerfile`（精選集合，體積更小），建置後 `wslc tag` + `wslc save` 匯出覆蓋即可。

### 本機打包

打包腳本有 PowerShell 與 Python 兩個等價實作（`release.ps1` / `release.py`），任選其一：

```powershell
# PowerShell 版
pwsh -ExecutionPolicy Bypass -File release.ps1 -Configuration Release -Platform x64
```

```bash
# Python 版（需要 Python 3.9+，僅標準庫）
python release.py --configuration Release --platform x64
```

```powershell
# ARM64：再跑一次腳本（runtime.zip 與平台無關，重跑僅重新打包覆蓋，內容不變）
pwsh -ExecutionPolicy Bypass -File release.ps1 -Configuration Release -Platform ARM64

# 可選：同時產出整合包（Python 版把 -WithBundle 換成 --with-bundle）
pwsh -ExecutionPolicy Bypass -File release.ps1 -Configuration Release -Platform x64 -WithBundle
```

打包前腳本會檢查 `runtime\images\` 下的 `sharelatex-server-pro.tar.gz`、`git-bridge.tar.gz`、`mongo-8.0.tar.gz`、`redis-7.tar.gz` 四個映像檔是否齊備，缺少時直接報錯，並提示先從 Release 附件 `runtime.zip` 解壓縮還原。

| 產物 | 產生條件 | 說明 |
|---|---|---|
| `release\AyakaLauncher_<設定>_<平台>.exe` | 預設 | 主程式（x64 / ARM64 分別建置），命名與 Release 附件一致（不含版本號） |
| `release\runtime.zip` | 預設 | 內嵌執行環境映像檔包，約 4.2GB，即隨 Release 分發的 `runtime.zip` |
| `release\AyakaLauncher_<版本>_<設定>_<平台>.zip` | 加 `-WithBundle` | exe 加 runtime 整合包，約 4.2GB，適合離線分發 |
| `dist\AyakaLauncher.exe` | `dist\` 存在時自動同步 | 本機日常執行目錄（runtime 以 junction 掛載）隨發布重新整理；啟動器正在執行時僅警告跳過 |

打包僅用各語言標準庫（PowerShell 的 `System.IO.Compression` / Python 的 `zipfile`），無第三方依賴。本機日常執行無需打包——`dist\` 與建置輸出目錄的 runtime 均已透過 junction 掛好。

## 🔒 授權條款

- `Plain Craft Launcher 2/` 使用[上游自訂授權條款](Plain%20Craft%20Launcher%202/LICENCE)
- 其餘目錄使用 [Apache License 2.0](LICENSE)

## 🙏 致謝

- [PCL Community Edition](https://github.com/PCL-Community/PCL-CE) —— UI 框架與工程基礎
- [Meloong-Git/PCL](https://github.com/Meloong-Git/PCL) —— 上游原始專案
- [Overleaf](https://github.com/overleaf/overleaf) —— 內建的協作 LaTeX 服務
- [Ayakaleaf](https://github.com/ayaka-notes/ayakaleaf-pro) —— 上游 Overleaf 客製化伺服端，本專案內嵌執行環境與工作台的伺服端來源

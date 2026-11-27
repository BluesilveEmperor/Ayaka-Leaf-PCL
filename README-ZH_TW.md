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
- **文件匯入（Pandoc）** —— 映像檔內建 Pandoc 3.x，工作台直接匯入 Word / Markdown 文件；匯入失敗給出中文原因提示，超大文件自動攔截
- **公式聯動** —— 左側選取 LaTeX 公式程式碼，右側 PDF 自動高亮對應位置並捲動定位（重用 Overleaf 內建 SyncTeX）
- **PDF 反向同步** —— 在 PDF 上點擊任意位置，編輯器自動跳到對應原始碼行並閃爍高亮（反向 SyncTeX 定位）；跨檔案定位失敗時自動回退官方雙擊流程
- **錯誤中文化** —— 編譯錯誤日誌附中文解釋（點錯誤跳原始碼行的官方互動保留）；Word / Markdown 匯入失敗同樣給出中文原因
- **首次啟動環境引導** —— 環境自檢頁列出 WSL / wslc / .NET 三項執行環境狀態，缺失項提供一鍵修復指引
- **映像檔變體** —— Overleaf 主映像檔雙變體按需部署：完整版（約 3.5GB，精選巨集套件集）與「中文常用版」精簡映像檔（約 1.7GB，中文排版 + 常用巨集套件保留集）
- **巨集套件面板** —— 頁面內搜尋 / 安裝 / 解除安裝 TeX Live 巨集套件（tlmgr 線上補裝）；精簡映像檔缺巨集套件時線上補裝即可，形成閉環
- **服務狀態頁** —— 容器執行狀態、資源占用、最近日誌一頁可見，支援一鍵重新啟動服務
- **伺服器端帳號管理** —— 頁面內列出伺服器端全部帳號：搜尋 / 停用 / 恢復 / 重設密碼 / 授予或撤銷管理員
- **中文範本庫** —— 內建中學試卷、大學畢業論文、競賽報告、國內期刊四類中文範本，一鍵建立專案即可用 XeLaTeX 直接編譯（隨映像檔內建中文字型）
- **本地資料夾同步** —— 專案與本地資料夾雙向自動同步：本地編輯器儲存自動推送、工作台修改自動拉回；同一檔案雙端都改過時雙方版本均保留（衝突副本 + 明確提示），絕不靜默覆蓋
- **輕量協作用戶端** —— AyakaCollabLite（≤ 50MB、免安裝、零執行環境依賴），異地隊友無需完整啟動器即可憑邀請碼加入協作
- **符號面板拓展** —— 內建符號面板拓展為 LaTeX-Workshop 全量符號集：10 個中文分類、760+ 符號（在 Overleaf 原生符號之上合併增量），支援按名稱 / 關鍵字 / Unicode 搜尋，服務啟動時自動生效，無需手動操作
- **映像檔管理** —— 內建映像檔 tar.gz（`runtime/images`，雙變體主映像檔 + 清單），部署時按所選變體自動載入，重複載入冪等
- **備份還原** —— 資料卷即 vhd 檔案，頁面內一鍵快照 / 還原
- **區域網路共享（團隊協作）** —— 服務台即時顯示「隊友存取位址」，同一區域網路的隊友用瀏覽器即可存取、註冊帳號並即時協同編輯；網路切換後位址自動重新整理，並自動排除 VPN / TUN 虛擬網卡（Mihomo / Clash 等），詳見「團隊協作」章節
- **遠端協作** —— 基於 EasyTier 的異地組網：房主一鍵產生邀請碼開放本機 Overleaf 服務，異地隊友輸碼自動加入虛擬網路並轉發連接埠，無需公網 IP 或連接埠映射，詳見「遠端協作」章節
- **帳號資料隔離** —— 帳號庫、憑證與使用者資料全部隨啟動器目錄存放，整個目錄複製即成可攜版，多個副本互不串號
- **Pro 功能對齊** —— 設定 → Ayakaleaf：Git-Bridge（git 操作 + MCP Server）、AI 助手、範本畫廊、GitHub / GitLab / Gitea 雙向同步、Zotero / Mendeley 文獻引用、註冊策略（開放 / 網域白名單 / 關閉）、reCAPTCHA、洩露密碼檢查、品牌化、進階環境變數（SSO 等任意透傳）；全部開關預設關閉，與 ayakaleaf-pro 行為對齊
- **多語言介面** —— 映像檔內建 18 種介面語言按需啟用（英語固定主連接埠，其餘語言依次 +1），工作台內與頁尾語言選擇器均可切換
- **主題色** —— 天空藍、龍貓藍、當機藍、營火橘（Campfire 標誌色 #E2680C）四款內建主題色隨選隨切，淺色 / 深色模式均適配
- **管理員與帳號聯動** —— 首個本機使用者自動開通伺服器端管理員；帳戶中心直達管理面板、一鍵重設 Ayakaleaf 密碼；刪除本機使用者時同步清理伺服器端帳號
- **全量備份** —— 全部資料卷 + 帳號憑證 + 本機使用者庫 + 啟動器設定一鍵打包為單一 zip，一鍵還原
- **映像檔版本管理** —— 映像檔頁顯示目前使用映像檔與版本；匯入新版本映像檔包後重新啟動服務，自動以新映像檔重建容器（資料卷跨版本保留）
- **線上更新** —— 基於 GitHub Releases 的自動更新通道：啟動時按設定檢查新版本，下載後 SHA256 校驗並自動替換程式檔案，設定中可關閉；詳見「線上更新」章節

## 📐 編輯器聯動

在工作台裡用滑鼠選取一段公式程式碼，右側 PDF 會自動跳到公式渲染位置並高亮，不必再點工具列的 Sync to PDF：

- 支援 `$…$`、`$$…$$`、`\(…\)`、`\[…\]`，以及 `equation`、`align`、`gather`、`multline`、`split`、`cases`、`matrix` 等 `\begin{}` 環境
- 只對公式選取生效；選取一般文字、複製貼上不受影響
- 右側需已成功編譯出 PDF 才會觸發（未編譯時靜默略過，不彈錯誤提示）
- 高亮框來自 SyncTeX 記錄的位置，經本機幾何消歧與墨跡掃描校正，多行環境、多欄排版下也能框住實際內容

## 🔄 線上更新

自 2.1.0 起內建基於 GitHub Releases 的線上更新通道：

- **通道行為**：啟動時按「設定 → 啟動器更新」中選擇的更新方式檢查新版本（自動下載並安裝 / 自動下載並提示更新 / 僅提示有新版本 / 不自動檢查），下載完成後進行 SHA256 校驗，再自動替換程式檔案；也可在該頁點擊「檢查更新」手動觸發
- **總開關**：取消勾選「啟用更新通道」後，啟動器完全不檢查、不下載更新
- **下載加速**：優先經 ghfast.top / gh-proxy.com 加速下載，加速站點不可用時自動回退直連
- **獨立版本線**：版本號為本專案獨立維護，與上游 PCL2-CE 的版本號無關
- **存量版本**：2.0.0 及之前的版本未啟用更新通道，需手動下載一次新版 exe 替換，此後即可線上更新；已有帳號資料與設定不受升級影響

## 🤝 團隊協作與遠端協作

按隊友與你的網路關係選擇協作方式：

| 場景 | 功能 | 要求 |
|---|---|---|
| 同一區域網路（宿舍 / 實驗室 / 辦公室內網） | 團隊協作（區域網路共享） | 隊友僅需瀏覽器，無需安裝任何軟體 |
| 異地 / 不同網路 | 遠端協作（EasyTier 虛擬組網） | 雙方都能存取網際網路；隊友執行完整啟動器或輕量用戶端 AyakaCollabLite |

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
4. 隊友加入後會自動建立協作帳號（在「帳號中心」帶「協作」標記、顯示隊友的顯示名稱），可像一般帳號一樣停用、重設密碼或刪除

**隊友**

1. 開啟本啟動器進入「協作」頁，貼上邀請碼（直接貼上包含邀請碼的整段文字也可以，會自動識別其中生效的邀請碼），可選填寫顯示名稱（預設本機使用者名稱，用於產生協作帳號），點擊「加入協作」
2. 加入過程全自動：連線公共節點 → 尋找房主 → 按房主啟用的介面語言逐個建立本機連接埠轉發（本機沿用房主同連接埠號，被占用時自動改用空閒連接埠）
3. 加入成功後頁面列出各語言的存取位址（如 `简体中文：http://localhost:8081`）；點擊「開啟工作台」進入房主的服務（可在房主已啟用的介面語言之間切換），也可以直接用瀏覽器開啟列出的位址
4. 協作帳號自動配對：加入後啟動器自動建立協作帳號並完成登入（免註冊），「開啟工作台」即以登入狀態進入，可與房主即時協同編輯；同一邀請碼重複加入會重用同一帳號，不會重複開戶。配對失敗時轉發仍可用，頁面會給出中文原因並可一鍵重試

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

### 🪶 輕量協作用戶端（AyakaCollabLite）

只需加入他人協作、不需要本地服務棧的隊友，可改用輕量用戶端 AyakaCollabLite：

- **體積 ≤ 50MB、免安裝、零執行環境依賴**：解壓即用，雙擊 `CollabLite.bat` 啟動（PowerShell 5.1 WinForms 圖形介面，Windows 10/11 內建環境即可執行）
- 功能聚焦異地協作：貼上房主邀請碼（可選填顯示名稱）→ 自動組網（內建 EasyTier v2.6.4，與主程式版本一致保證協定相容）→ 按房主啟用的介面語言建立本機連接埠轉發 → 自動建立協作帳號並把電子郵件與密碼顯示在視窗中（各帶「複製」按鈕，在瀏覽器登入頁分別貼上一次即可，此後瀏覽器保持登入）→ 瀏覽器開啟轉發位址協同編輯
- 與完整啟動器的協作功能互相相容：房主用完整啟動器「開啟協作」，隊友任選完整啟動器「加入協作」或輕量用戶端加入，組網參數與協作帳號規則一致
- 安裝包 `AyakaCollabLite_<版本>_x64.zip` 隨主程式 Release 一併分發，附 `.sha256` 校驗檔案

## 📁 本地資料夾同步（雙向）

把 Ayakaleaf 專案與本地資料夾建立雙向同步，實現「本地編輯 + Overleaf 編譯」混合工作流程：

1. 在工作台開啟要綁定的專案，點擊視窗頂部的「⟳ 本地同步」按鈕
2. 在彈出的管理視窗中點擊「綁定目前專案到資料夾…」，選擇本地資料夾（建議空資料夾）並確認
3. 綁定完成後首輪同步自動把專案內容拉到本地；此後雙向自動同步：
   - **本地 → 工作台**：本地編輯器（VS Code / TeXstudio / 記事本等）儲存檔案後自動推送到伺服器端，已連線的協作者會即時看到更新
   - **工作台 → 本地**：工作台中的修改（含協作者編輯）自動拉回本地資料夾
   - 本地監聽為準即時（約 1.5 秒防抖），遠端輪詢週期 30 秒；服務未啟動時自動略過，服務恢復後自動續傳

**衝突規則（絕不靜默覆蓋）**：同一檔案雙端都修改且內容不同時，以遠端為目前版本落盤，本地修改儲存為衝突副本 `名稱.conflict-YYYYMMDD-HHMMSS.副檔名`，並彈出明確提示——雙方版本都保留，由你決定保留哪份。

**綁定管理**：管理視窗內可對每個綁定執行「立即同步」「暫停 / 恢復」「開啟資料夾」「解除綁定」；解除綁定只停止自動同步，本地檔案與清單均保留。綁定設定隨啟動器持久化，重新啟動後自動恢復監聽。

**v1 範圍限制**（後續版本演進）：

- 刪除 / 重新命名不自動傳播：本地刪除的檔案遠端保留，遠端刪除的檔案本地保留
- 已存在二進位檔案（如圖片）的修改不支援推送（請在工作台內替換），新增二進位檔案可正常上傳
- 編譯產物與臨時檔案（`.aux` / `.log` / `.synctex.gz` 等）自動排除，不會同步到工作台

## 💻 支援平台

| 作業系統 | 支援情況 | 環境需求 |
|---|---|---|
| Windows 10 21H2+ / Windows 11 (x64 / ARM64) | ✅ 完整支援 | [.NET 10 Desktop Runtime](https://get.dot.net/10)（架構與主程式一致）+ WSL 3.0+ |
| Windows 10 1809 - 21H2 | ⚠️ 需手動升級 WSL 至 3.0+ | 同上 |
| Windows 7 / 8 / 8.1 | ❌ 不支援 | / |
| macOS / Linux | ❌ 不支援 | WPF、WebView2 與 WSLC 容器皆為 Windows 專屬 |

## 🚀 快速開始

1. 從 [Releases](https://github.com/BluesilveEmperor/Ayaka-Leaf-PCL/releases) 下載主程式 `AyakaLauncher_Release_x64.exe`（ARM64 裝置改用 `AyakaLauncher_Release_ARM64.exe`）；執行環境包 `runtime.7z` 從下文「主下載源（網盤）」取得；主程式另有備用下載，詳見下文「備用下載：主程式」
2. 將 exe 與執行環境包放在同一目錄，解壓縮得到 `runtime\images\`（約 6.3GB，含雙變體主映像檔，詳見下節）
3. 安裝 [.NET 10 Desktop Runtime](https://get.dot.net/10)（架構與主程式一致）
4. 啟用 WSL：以系統管理員 PowerShell 執行 `wsl --install --no-distribution`，完成後執行 `wsl --update`
5. 啟動 `AyakaLauncher_Release_<平台>.exe` → 註冊本機使用者並登入
6. 服務台頁面依序點擊：**開始部署** → **啟動服務** → **開啟工作台**（服務位址 `http://localhost:8080`，連接埠可設定）

> ⚠️ `runtime\` 是容器映像檔執行環境，必須與 exe 保持**同級、不可拆分或改名**。exe 本身不含映像檔，缺少 `runtime\images\` 時無法部署服務。

## ⚠️ 功能限制

- **郵件不可用**：本機實例未設定 SMTP，確認郵件 / 密碼找回郵件無法傳送（註冊已自動略過電子郵件確認）；註冊策略選「網域白名單」時啟用郵件同樣無法送達，請知悉後再選用
- **沙箱編譯**：WSL 容器環境無法執行同級（sibling）容器，TeX Live 全量沙箱編譯不可用，設定頁中置灰顯示；編譯仍在 Ayakaleaf 容器內進行，不受影響。**Pandoc 文件匯入**已隨新版執行環境映像檔恢復（映像檔內建 Pandoc 3.x，Word / Markdown 匯入可用），設定頁顯示實際檢測結果
- **OAuth 回呼可達性**：GitHub / GitLab / Gitea / Zotero / Mendeley 授權回呼要求存取者能存取網站位址；區域網路模式使用本機 IP，公網不可達時授權可能失敗
- **憑證安全**：AI 金鑰、OAuth Client Secret 等敏感設定儲存在本機加密設定來源中，容器重建指紋僅取 SHA256 雜湊，不寫日誌

## 📦 內嵌執行環境（runtime）

`runtime\images\` 存放 WSLC 容器映像檔包，部署時由應用程式自動載入（冪等，重複載入無副作用），結構如下：

```
AyakaLauncher.exe
runtime\
└─ images\
   ├─ sharelatex-server-pro.tar.gz       # Overleaf 完整版映像檔（約 3.5GB，內建 Pandoc 3.x + 精選完整巨集套件集）
   ├─ sharelatex-server-pro-cn.tar.gz    # Overleaf「中文常用版」精簡映像檔（約 1.7GB，中文排版 + 常用巨集套件保留集）
   ├─ git-bridge.tar.gz                  # Git-Bridge / MCP 服務映像檔（約 387MB，必選，Git 操作與三方同步依賴）
   ├─ mongo-8.0.tar.gz                   # MongoDB 8.0（約 300MB）
   ├─ redis-7.tar.gz                     # Redis 7（約 42MB）
   ├─ images-manifest.json               # 映像檔清單（變體、標籤、SHA256、建置時間）
   └─ *.sha256                           # 各映像檔包的 SHA256 校驗檔案
```

**雙變體主映像檔**：Overleaf 主映像檔提供「完整版 / 中文常用版」兩種變體，部署時按所選變體載入對應映像檔包（未選中的變體包自動略過；磁碟緊張時可刪除不用的變體包）：

| 變體 | 映像檔包 | 體積 | 適用 |
|---|---|---|---|
| 完整版（預設） | `sharelatex-server-pro.tar.gz` | 約 3.5GB | 精選完整巨集套件集開箱即用，涵蓋中文支援與常見期刊 / 範本巨集套件 |
| 中文常用版（精簡） | `sharelatex-server-pro-cn.tar.gz` | 約 1.7GB | 面向中文常用排版場景裁剪；後續缺巨集套件時經「巨集套件面板」tlmgr 線上補裝，形成閉環 |

全部包合計約 6.3GB，**不入 git 倉庫**（`.gitignore` 已排除），正式分發走下文「主下載源（網盤）」中的 `runtime.7z`（Release 不再附帶執行環境包）。需要自行重建映像檔時，倉庫提供三種 TeX Live 層 Dockerfile：`tools/runtime/texlive-packages/Dockerfile`（精選集合，完整版預設）、`tools/runtime/texlive-full/Dockerfile`（scheme-full 全量）、`tools/runtime/texlive-cn/Dockerfile`（中文常用版精簡），經 `tools/runtime/build-images.ps1` / `build-images.py` 一鍵建置（串聯 Pandoc 層 → TeX Live 層 → `wslc save` → SHA256 清單）。取得方式任選其一：

| 方式 | 適用映像檔 | 說明 |
|---|---|---|
| 網盤主下載源 `runtime.7z` | 全部 | 從下文「主下載源（網盤）」任選一個分享連結下載，解壓到 exe 同級即為 `runtime\images\` |
| `wslc` 自助匯出 | mongo / redis | 標準映像檔：`wslc pull mongo:8.0` 後 `wslc save -o runtime\images\mongo-8.0.tar.gz mongo:8.0`（redis 同理） |
| 本倉庫建置腳本 | sharelatex 雙變體 / git-bridge 定製映像檔 | `pwsh tools\runtime\build-images.ps1`（預設精選集合；`-Variant cn` 精簡中文；`-Variant full` scheme-full） |

**放置位置**：解壓後的 `runtime.7z` 或整合包內，均放在 exe 同級；原始碼開發放在**倉庫根目錄** `runtime\images\`（建置後自動掛到輸出目錄，見下節）。

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

# 2. 擺放 runtime：把上節取得的 4 個映像檔包放到倉庫根目錄
#    runtime\images\sharelatex-server-pro.tar.gz
#    runtime\images\git-bridge.tar.gz
#    runtime\images\mongo-8.0.tar.gz
#    runtime\images\redis-7.tar.gz

# 3. 建置並執行
dotnet run --project "Plain Craft Launcher 2"
```

也可以用 Visual Studio / Rider / VS Code 開啟方案直接 F5。

**runtime junction 機制**：倉庫根目錄的 `runtime\` 是映像檔包的真實存放處；每次建置後，csproj 中的 `LinkAyakaleafRuntime` Target 會把輸出目錄 `bin\<設定>\net10.0-windows\runtime` 以目錄聯結（junction）指向它，`dotnet publish` 輸出目錄由 `LinkAyakaleafRuntimeOnPublish` 同樣處理——避免每次建置複製約 6.3GB，應用程式執行時從 exe 旁的 `runtime\images` 讀取。對倉庫根目錄 `runtime\` 的修改在下次執行時即時生效。

**首次執行流程**：註冊本機使用者並登入 → 服務台點擊 **開始部署**（自動載入映像檔、初始化 MongoDB 副本集）→ **啟動服務** → **開啟工作台**。可用 `wslc images` 確認 4 個映像檔、`wslc list` 確認 3 個容器（mongo / redis / overleaf；啟用 Git-Bridge 時另見 git-bridge 容器）均已就緒。

## 📦 發佈與分發（Release）

主程式統一從 [GitHub Releases](https://github.com/BluesilveEmperor/Ayaka-Leaf-PCL/releases) 下載，執行環境包 `runtime.7z` 從下方「主下載源（網盤）」取得；**Release 僅附帶主程式與輕量用戶端，不再附帶執行環境包**。附件如下：

| 附件 | 說明 |
|---|---|
| `AyakaLauncher_Release_x64.exe` | x64 主程式，Framework-dependent 單一檔案，需 [.NET 10 Desktop Runtime](https://get.dot.net/10) |
| `AyakaLauncher_Release_ARM64.exe` | ARM64 主程式，Framework-dependent 單一檔案，需求同上 |
| `AyakaCollabLite_<版本>_x64.zip`（+ `.sha256`） | 輕量協作用戶端（≤ 50MB），異地隊友免完整啟動器加入協作，解壓即用 |

> 📌 **執行環境說明**：`runtime.7z` 內含雙變體主映像檔（完整版約 3.5GB / 中文常用版約 1.7GB），均內建 Pandoc 3.x 與中文支援；完整版開箱即用，中文常用版缺巨集套件時經「巨集套件面板」線上補裝。

### 主下載源（網盤）

執行環境包 `runtime.7z` 透過網盤分享分發，以下**全部為主下載源**，任選其一即可；後續會陸續補充其他網盤的分享連結。

| 網盤 | 分享連結 |
|---|---|
| 阿里雲盤 | [阿里雲盤分享連結](https://www.alipan.com/s/NYaZyrvbRmX) |
| 其他網盤 | 待補充 |

> 📁 **按版本分資料夾**：網盤分享頁按啟動器版本建立資料夾，請先進入對應版本目錄（目前 `v2.1.2`）再下載 `runtime.7z`。主程式的備用網盤分享同樣按版本分資料夾，兩者必須取自**同一版本目錄**，切勿跨版本混用。

若暫時沒在網盤裡找到，那多半是網路有點慢還沒傳完，或者作者有點懶還沒上傳——稍後再來看看。以支援 7z 的解壓工具把 `runtime.7z` 解壓到 exe 同級目錄，得到 `runtime\images\`；接著依下方「部署步驟」繼續。

### 備用下載：主程式（阿里雲盤）

GitHub Releases 仍為主程式的**主要下載管道**；下載不便時，可從以下備用分享取得 `AyakaLauncher_Release_x64.exe` / `AyakaLauncher_Release_ARM64.exe`（以分享頁實際檔案為準）：

- [阿里雲盤分享連結](https://www.alipan.com/s/XBcaUK75hrU)

> 📁 分享頁**按啟動器版本分資料夾**存放，請先進入對應版本目錄（目前 `v2.1.2`）再取 exe，並與上文「主下載源（網盤）」的 `runtime.7z` 維持同一版本。

> ⚠️ 此處僅提供**主程式 exe**。執行環境包 `runtime.7z` 屬另一份資源，仍須從上文「主下載源（網盤）」取得；兩個連結用途不同，不可互相替代。

### 部署步驟

1. 將 `AyakaLauncher_Release_<平台>.exe` 與執行環境包 `runtime.7z` 放在同一目錄
2. 在該目錄解壓執行環境包，得到 `runtime\images\`（內含 `sharelatex-server-pro`、`git-bridge`、`mongo-8.0`、`redis-7` 四個映像檔包）
3. 執行 exe，按「快速開始」完成首次部署

### 執行環境包的分發方式

執行環境映像檔不進入倉庫，乾淨的 CI 建置機器無法憑空產生執行環境包。因此**執行環境包統一由網盤主下載源 `runtime.7z` 分發，Release 不再附帶**。本機執行 `pwsh release.ps1` / `python release.py` 仍會在 `release\` 下產生 `runtime.zip`，供本機整合包與離線分發使用，但不會上傳到 Release。

### 為何 runtime 不進入程式碼倉庫

單一映像檔包最大約 3.5GB（sharelatex-server-pro 完整版），遠超 GitHub 一般倉庫 100MB 的單檔上限，因此執行環境映像檔不進入 git，改由網盤主下載源 `runtime.7z` 分發（Release 不再附帶執行環境包）。倉庫內的 `runtime\` 目錄僅供本機開發使用，已在 `.gitignore` 中忽略。

### 本機打包

打包腳本有 PowerShell 與 Python 兩個等價實作（`release.ps1` / `release.py`），任選其一：

```powershell
# PowerShell 版：x64 與 ARM64 分別建置
pwsh -ExecutionPolicy Bypass -File release.ps1 -Configuration Release -Platform x64
pwsh -ExecutionPolicy Bypass -File release.ps1 -Configuration Release -Platform ARM64
```

```bash
# Python 版（需要 Python 3.9+，僅標準庫）：x64 與 ARM64
python release.py --configuration Release --platform x64
python release.py --configuration Release --platform ARM64
```

```powershell
# 可選：同時產出整合包（Python 版把 -WithBundle 換成 --with-bundle）
pwsh -ExecutionPolicy Bypass -File release.ps1 -Configuration Release -Platform x64 -WithBundle
pwsh -ExecutionPolicy Bypass -File release.ps1 -Configuration Release -Platform ARM64 -WithBundle
```

```powershell
# 可選：隨發布一併建置輕量協作用戶端（Python 版用 --with-collab-lite）
pwsh -ExecutionPolicy Bypass -File release.ps1 -Configuration Release -Platform x64 -WithCollabLite
```

> `runtime.zip` 與平台無關，ARM64 重跑僅重新打包覆蓋、內容不變；Release 不再附帶執行環境包，對外統一由網盤 `runtime.7z` 分發。

打包前腳本會先檢查專案根目錄是否存在 `runtime\` 資料夾：若不存在，直接提示先從網盤下載 `runtime.7z`、解壓縮到專案根目錄後再執行腳本；若存在，則繼續校驗 `runtime\images\` 下的 `sharelatex-server-pro.tar.gz`、`git-bridge.tar.gz`、`mongo-8.0.tar.gz`、`redis-7.tar.gz` 四個必備映像檔是否齊備，缺少則報錯並提示補齊（可從網盤 `runtime.7z` 解壓還原）。隨後校驗映像檔清單 `images-manifest.json`（存在時）：清單宣告的每個映像檔包（含中文常用版變體）必須存在，且各自的 `.sha256` 伴生檔案與清單一致，防止產出殘缺或被竄改的執行環境包。

| 產物 | 產生條件 | 說明 |
|---|---|---|
| `release\AyakaLauncher_<設定>_<平台>.exe` | 預設 | 主程式（x64 / ARM64 分別建置），命名與 Release 附件一致（不含版本號） |
| `release\runtime.zip` | 預設 | 內嵌執行環境映像檔包（含雙變體主映像檔），約 6.3GB，僅供本機整合包使用，不再隨 Release 分發 |
| `release\AyakaLauncher_<版本>_<設定>_<平台>.zip` | 加 `-WithBundle` | exe + runtime 整合包（含雙變體主映像檔），約 6.3GB，適合離線分發 |
| `release\AyakaCollabLite_<版本>_x64.zip`（+ `.sha256`） | 預設校驗 / `-WithCollabLite` 時先建置 | 輕量協作用戶端安裝包（≤ 50MB）；已存在則校驗雜湊納入產物清單，缺少時警告提示建置 |
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
- [Campfire](https://github.com/Xocio/Campfire) —— 異地 / 異網協作方案與「營火橘」主題色的設計參考
- [LaTeX-Workshop](https://github.com/James-Yu/LaTeX-Workshop) —— 符號面板拓展所採納的全量符號集來源

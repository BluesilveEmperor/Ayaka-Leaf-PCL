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
- **服務台** —— 一鍵部署 / 啟動 / 停止內建 Overleaf 服務（MongoDB 8.0 + Redis 7 + Overleaf），自動初始化 MongoDB 副本集，服務就緒自動探測
- **容器執行時** —— 基於 Windows WSL 3.0+ 內建容器能力（WSLC），**無需 Docker Desktop**，資料卷為獨立 vhd 檔案
- **工作台** —— 內嵌 WebView2 開啟 Overleaf，自動開戶 + 自動登入，免手動輸入
- **公式聯動** —— 左側選取 LaTeX 公式程式碼，右側 PDF 自動高亮對應位置並捲動定位（重用 Overleaf 內建 SyncTeX）
- **映像檔管理** —— 內建映像檔 tar.gz（`runtime/images`），部署時自動載入，重複載入冪等
- **備份還原** —— 資料卷即 vhd 檔案，頁面內一鍵快照 / 還原

## 📐 編輯器聯動

在工作台裡用滑鼠選取一段公式程式碼，右側 PDF 會自動跳到公式渲染位置並高亮，不必再點工具列的 Sync to PDF：

- 支援 `$…$`、`$$…$$`、`\(…\)`、`\[…\]`，以及 `equation`、`align`、`gather`、`multline`、`split`、`cases`、`matrix` 等 `\begin{}` 環境
- 只對公式選取生效；選取一般文字、複製貼上不受影響
- 右側需已成功編譯出 PDF 才會觸發（未編譯時靜默略過，不彈錯誤提示）
- 高亮框來自 SyncTeX 記錄的位置，多行環境可能只涵蓋該處首行

## 💻 支援平台

| 作業系統 | 支援情況 | 環境需求 |
|---|---|---|
| Windows 10 21H2+ / Windows 11 (x64) | ✅ 完整支援 | [.NET 10 Desktop Runtime](https://get.dot.net/10) + WSL 3.0+ |
| Windows 10 1809 - 21H2 | ⚠️ 需手動升級 WSL 至 3.0+ | 同上 |
| Windows 7 / 8 / 8.1 | ❌ 不支援 | / |
| macOS / Linux | ❌ 不支援 | WPF、WebView2 與 WSLC 容器皆為 Windows 專屬 |

## 🚀 快速開始

1. 從 [Releases](https://github.com/BluesilveEmperor/Ayaka-Leaf-PCL/releases) 下載主程式 `AyakaLauncher_Release_x64.exe`（ARM64 裝置改用 `AyakaLauncher_Release_ARM64.exe`）與執行環境包 `runtime.zip`
2. 將 exe 與 `runtime.zip` 放在同一目錄，解壓縮 `runtime.zip` 得到 `runtime\images\`（約 1.6GB，詳見下文「發佈與分發」）
3. 安裝 [.NET 10 Desktop Runtime](https://get.dot.net/10)（架構與主程式一致）
4. 啟用 WSL：以系統管理員 PowerShell 執行 `wsl --install --no-distribution`，完成後執行 `wsl --update`
5. 啟動 `AyakaLauncher_Release_<平台>.exe` → 註冊本機使用者並登入
6. 服務台頁面依序點擊：**開始部署** → **啟動服務** → **開啟工作台**（服務位址 `http://localhost:8080`，連接埠可設定）

> ⚠️ `runtime\` 是容器映像檔執行環境，必須與 exe 保持**同級、不可拆分或改名**。exe 本身不含映像檔，缺少 `runtime\images\` 時無法部署服務。

## 🔨 建置

需要 [.NET 10 SDK](https://get.dot.net/10)：

```powershell
dotnet publish "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" -c Release -p:Platform=x64 --no-self-contained -o artifact
```

產物為單一檔案 `AyakaLauncher.exe`（Framework-dependent）。

## 📦 發佈與分發

正式版本統一從 [GitHub Releases](https://github.com/BluesilveEmperor/Ayaka-Leaf-PCL/releases) 下載，附件包含：

| 附件 | 說明 |
|---|---|
| `AyakaLauncher_Release_x64.exe` | x64 主程式，Framework-dependent 單一檔案，需 [.NET 10 Desktop Runtime](https://get.dot.net/10) |
| `AyakaLauncher_Release_ARM64.exe` | ARM64 主程式，Framework-dependent 單一檔案，需求同上 |
| `runtime.zip` | 內嵌容器映像檔執行環境包，約 1.6GB |

### 部署步驟

1. 將 `AyakaLauncher_Release_<平台>.exe` 與 `runtime.zip` 放在同一目錄
2. 在該目錄解壓縮 `runtime.zip`，得到 `runtime\images\`（內含 `sharelatex-server-pro`、`mongo-8.0`、`redis-7` 三個映像檔包）
3. 執行 exe

`runtime\` 必須與 exe **同級、不可拆分或改名**。

### 首次發佈（runtime.zip 來源）

CI 無法憑空產生 `runtime.zip`——執行環境映像檔不進入倉庫，乾淨的建置機器上也沒有這些檔案。因此：

- **首次發佈**（此前沒有任何包含 `runtime.zip` 的 Release）：先在本機執行 `pwsh release.ps1` 得到 `release\runtime.zip`，手動上傳到該 Release；或手動觸發工作流時，在 `runtime_source_tag` 輸入中指定已含 `runtime.zip` 的 Release 標籤。
- **之後的發佈**：`package_runtime` job 會自動尋找最近一個包含 `runtime.zip` 的非草稿 Release，下載並重用其中的 `runtime.zip` 上傳到新 Release，無需再次手動上傳。

### 為何 runtime 不進入程式碼倉庫

單一映像檔包最大為 1262.9MB，遠超 GitHub 一般倉庫 100MB 的單檔上限，因此執行環境映像檔不進入 git，改由 Release 附件 `runtime.zip` 分發。倉庫內的 `runtime\` 目錄僅供本機開發使用，已在 `.gitignore` 中忽略。

### 本機打包

```powershell
# 預設：產出 exe 與 runtime.zip
pwsh -ExecutionPolicy Bypass -File release.ps1 -Configuration Release -Platform x64

# 可選：同時產出整合包
pwsh -ExecutionPolicy Bypass -File release.ps1 -Configuration Release -Platform x64 -WithBundle
```

打包前腳本會檢查 `runtime\images\` 下的 `sharelatex-server-pro.tar.gz`、`mongo-8.0.tar.gz`、`redis-7.tar.gz` 三個映像檔是否齊備，缺少時直接報錯，並提示先從 Release 附件 `runtime.zip` 解壓縮還原。

| 產物 | 產生條件 | 說明 |
|---|---|---|
| `release\AyakaLauncher_<設定>_<平台>.exe` | 預設 | 主程式，命名與 Release 附件一致（不含版本號） |
| `release\runtime.zip` | 預設 | 內嵌執行環境映像檔包，約 1.6GB，即隨 Release 分發的 `runtime.zip` |
| `release\AyakaLauncher_<版本>_<設定>_<平台>.zip` | 加 `-WithBundle` | exe 加 runtime 整合包，約 1.7GB，適合離線分發 |

腳本使用 Windows 內建的 `C:\Windows\System32\tar.exe` 打包 zip，不依賴 PATH 中的 tar。本機日常執行無需打包——建置輸出目錄已透過 junction 掛好 runtime。

## 🔒 授權條款

- `Plain Craft Launcher 2/` 使用[上游自訂授權條款](Plain%20Craft%20Launcher%202/LICENCE)
- 其餘目錄使用 [Apache License 2.0](LICENSE)

## 🙏 致謝

- [PCL Community Edition](https://github.com/PCL-Community/PCL-CE) —— UI 框架與工程基礎
- [Meloong-Git/PCL](https://github.com/Meloong-Git/PCL) —— 上游原始專案
- [Overleaf](https://github.com/overleaf/overleaf) —— 內建的協作 LaTeX 服務
- [Ayakaleaf](https://github.com/ayaka-notes/ayakaleaf-pro) —— 上游 Overleaf 客製化伺服端，本專案內嵌執行環境與工作台的伺服端來源

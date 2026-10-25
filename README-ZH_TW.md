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

在工作台裡用滑鼠選取一段公式程式碼，右側 PDF 會自動跳到公式算圖位置並高亮，不必再點工具列的 Sync to PDF：

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

1. 安裝 [.NET 10 Desktop Runtime](https://get.dot.net/10)（x64）
2. 啟用 WSL：以系統管理員 PowerShell 執行 `wsl --install --no-distribution`，完成後執行 `wsl --update`
3. 啟動 `AyakaLauncher.exe` → 註冊本機使用者並登入
4. 服務台頁面依序點擊：**開始部署** → **啟動服務** → **開啟工作台**（服務位址 `http://localhost:8080`，連接埠可設定）

## 🔨 建置

需要 [.NET 10 SDK](https://get.dot.net/10)：

```powershell
dotnet publish "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" -c Release -p:Platform=x64 --no-self-contained -o artifact
```

產物為單一檔案 `AyakaLauncher.exe`（Framework-dependent）。

## 🔒 授權條款

- `Plain Craft Launcher 2/` 使用[上游自訂授權條款](Plain%20Craft%20Launcher%202/LICENCE)
- 其餘目錄使用 [Apache License 2.0](LICENSE)

## 🙏 致謝

- [PCL Community Edition](https://github.com/PCL-Community/PCL-CE) —— UI 框架與工程基礎
- [Meloong-Git/PCL](https://github.com/Meloong-Git/PCL) —— 上游原始專案
- [Overleaf](https://github.com/overleaf/overleaf) —— 內建的協作 LaTeX 服務

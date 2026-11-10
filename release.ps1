# AyakaLauncher 发布打包脚本
# ---------------------------------------------------------------------------
# 默认产出：
#   release\AyakaLauncher_<配置>_<平台>.exe   —— 可执行文件（命名与 Release 附件对齐）
#   release\runtime.zip                       —— 内嵌运行时（约 4.2GB，含 4 个镜像包：
#                                                sharelatex-server-pro / git-bridge /
#                                                mongo-8.0 / redis-7；主镜像约 3.5GB，
#                                                超过 GitHub 普通仓库 100MB 单文件限制，
#                                                故不入库）
# Release 不再附带运行时包；对外分发统一使用网盘提供的 runtime.7z，
# 本机生成的 release\runtime.zip 仅供整合包与离线分发，不上传到 Release。
#
# 可选产出（-WithBundle）：
#   release\AyakaLauncher_<版本>_<配置>_<平台>.zip —— exe + runtime 的整合包（约 4.2GB）
#
# 本机运行目录：dist\ 存在时（本机日常运行布局），发布后自动同步新 exe 到
# dist\AyakaLauncher.exe；exe 被占用（启动器正在运行）时只告警不失败。
#
# 本机日常运行请用 dist\（junction，不占额外空间）；
# 对外发布只用本脚本产出的 exe，运行时包由网盘 runtime.7z 分发。
#
# 输出行为：
#   dotnet publish 仅在失败时输出 MSBuild 日志，成功时保持安静（不刷 warning）；
#   打包 runtime.zip / 整合包时显示字节级进度条（Write-Progress）。
#
# 用法：pwsh release.ps1 [-Configuration Release] [-Platform x64] [-WithBundle]
param(
    [string]$Configuration = 'Release',
    [string]$Platform = 'x64',
    [switch]$WithBundle
)

$ErrorActionPreference = 'Stop'
# 控制台编码：Windows PowerShell 5.1 重定向/管道输出默认走系统 ANSI（GBK），
# 在 Git Bash / VS Code 等 UTF-8 终端里跑会把中文刷成乱码；
# 显式切到 UTF-8 修复（交互式 conhost 走 WriteConsoleW，不受影响）。
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8
$root = $PSScriptRoot
$artifact = Join-Path $root 'artifact'
$releaseDir = Join-Path $root 'release'
$runtimeDir = Join-Path $root 'runtime'
$runtimeImages = Join-Path $runtimeDir 'images'
# git-bridge.tar.gz 为 Git-Bridge / MCP / 三方同步的必选镜像（启用对应功能时容器必需）
$requiredImages = @('sharelatex-server-pro.tar.gz', 'git-bridge.tar.gz', 'mongo-8.0.tar.gz', 'redis-7.tar.gz')

# 压缩所需程序集（Windows PowerShell 5.1 自带）
Add-Type -AssemblyName System.IO.Compression | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null

# ---- 流式压缩一组条目到 zip，带字节级进度条 ----
# tar.gz 等已压缩数据用 Fastest（对已 gzip 内容再压缩几乎无收益，可大幅提速），
# 其余文件用 Optimal；每 250ms 刷新一次 Write-Progress。
function Compress-Entries {
    param(
        [string]$ZipPath,
        [string]$Activity,
        [System.Collections.IList]$Entries
    )
    if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
    if ($entries.Count -eq 0) { throw "没有可打包的条目：$ZipPath" }
    $totalBytes = [long](($entries | ForEach-Object { (Get-Item -LiteralPath $_.Src).Length } | Measure-Object -Sum).Sum)
    if ($totalBytes -le 0) { $totalBytes = 1 }
    $doneBytes = [long]0
    $lastReport = [datetime]::MinValue
    $zip = [System.IO.Compression.ZipFile]::Open($ZipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($e in $entries) {
            $level = if ($e.Name -match '\.(gz|tgz|zip|7z|rar|png|jpe?g|webp|mp4|vhdx)$') {
                [System.IO.Compression.CompressionLevel]::Fastest
            } else {
                [System.IO.Compression.CompressionLevel]::Optimal
            }
            $entry = $zip.CreateEntry($e.Name, $level)
            $src = [IO.File]::OpenRead($e.Src)
            try {
                $dst = $entry.Open()
                try {
                    $buf = New-Object byte[] (4MB)
                    while (($n = $src.Read($buf, 0, $buf.Length)) -gt 0) {
                        $dst.Write($buf, 0, $n)
                        $doneBytes += $n
                        $now = [datetime]::Now
                        if (($now - $lastReport).TotalMilliseconds -ge 250) {
                            $pct = [math]::Min(100, [int](100.0 * $doneBytes / $totalBytes))
                            Write-Progress -Activity $Activity `
                                -Status ("{0}　{1:N0} / {2:N0} MB（{3}%）" -f $e.Name, ($doneBytes / 1MB), ($totalBytes / 1MB), $pct) `
                                -PercentComplete $pct
                            $lastReport = $now
                        }
                    }
                } finally { $dst.Dispose() }
            } finally { $src.Dispose() }
        }
    } finally { $zip.Dispose() }
    Write-Progress -Activity $Activity -Completed
}

# runtime 目录全部文件 → 条目列表（zip 内前缀 runtime/，与原 bsdtar 产物一致）
function Get-RuntimeEntries {
    Get-ChildItem -LiteralPath $runtimeDir -Recurse -File | Sort-Object FullName | ForEach-Object {
        $rel = $_.FullName.Substring($runtimeDir.Length).TrimStart('\', '/').Replace('\', '/')
        @{ Src = $_.FullName; Name = ('runtime/' + $rel) }
    }
}

# ---- 0. 前置校验 ----
# 0.1 项目根必须存在 runtime 目录；缺失时提示先获取网盘分发的 runtime.7z。
if (-not (Test-Path -LiteralPath $runtimeDir -PathType Container)) {
    throw "未检测到项目根目录下的 runtime\ 目录（$runtimeDir）。请先从网盘下载 runtime.7z 并解压到项目根目录后，再运行 release.ps1。"
}

# 0.2 runtime/images 四个镜像必须齐备，避免产出残缺的运行时包。
foreach ($image in $requiredImages) {
    $imagePath = Join-Path $runtimeImages $image
    if (-not (Test-Path $imagePath)) {
        throw "缺少运行时镜像：$imagePath。请先补齐镜像包（可从网盘 runtime.7z 解压到 runtime\ 目录）后重试。"
    }
}

# ---- 1. Release 发布（成功时保持安静，仅失败时输出 MSBuild 日志）----
Write-Host "发布 $Configuration/$Platform …"
Write-Progress -Activity 'AyakaLauncher 发布' -Status "[1/3] dotnet publish $Configuration/$Platform …" -PercentComplete 10
$publishOutput = dotnet publish (Join-Path $root 'Plain Craft Launcher 2\Plain Craft Launcher 2.csproj') `
    -p:Configuration=$Configuration -p:Platform=$Platform `
    -p:DeleteExistingFiles=true -o $artifact --no-self-contained --nologo -v q -clp:ErrorsOnly 2>&1
Write-Progress -Activity 'AyakaLauncher 发布' -Status "[1/3] dotnet publish $Configuration/$Platform …" -Completed
if ($LASTEXITCODE -ne 0) {
    $publishOutput | ForEach-Object { Write-Host $_ }
    throw "dotnet publish 失败（$LASTEXITCODE）"
}

# ---- 2. 复制 exe 到 release（命名与 Release 附件一致）----
$exe = Join-Path $artifact 'AyakaLauncher.exe'
if (-not (Test-Path $exe)) { throw "未找到 $exe" }
$version = (Get-Item $exe).VersionInfo.FileVersion -replace '\.0$', ''
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null

$publishedExe = Join-Path $releaseDir "AyakaLauncher_${Configuration}_${Platform}.exe"
Copy-Item $exe $publishedExe -Force

# ---- 2.5 同步本机运行目录 dist\（本机日常运行布局：exe + runtime 联接 + 数据）----
# dist 不存在时静默跳过（CI / 其他机器）；exe 被占用（启动器正在运行）时只告警不失败
$distDir = Join-Path $root 'dist'
if (Test-Path $distDir) {
    $distExe = Join-Path $distDir 'AyakaLauncher.exe'
    try {
        Copy-Item $exe $distExe -Force
        Write-Host "已同步本机运行目录：$distExe"
    } catch {
        Write-Host '警告：dist\AyakaLauncher.exe 被占用（启动器正在运行？），本次未同步；关闭启动器后重跑或手动复制 artifact\AyakaLauncher.exe。'
    }
}

# ---- 3. 打包 runtime.zip（字节级进度条；条目结构与原 bsdtar 产物一致）----
$zipRuntime = Join-Path $releaseDir 'runtime.zip'
Write-Host '打包 runtime.zip（runtime 约 4.2GB，稍候）…'
$runtimeEntries = @(Get-RuntimeEntries)
Compress-Entries -ZipPath $zipRuntime -Activity '打包 runtime.zip' -Entries $runtimeEntries

# ---- 4. 可选：整合包（exe + runtime）----
$bundle = $null
if ($WithBundle) {
    $bundle = Join-Path $releaseDir "AyakaLauncher_${version}_${Configuration}_${Platform}.zip"
    Write-Host '打包整合包（exe + runtime，约 4.2GB，稍候）…'
    $bundleEntries = @(@{ Src = (Join-Path $artifact 'AyakaLauncher.exe'); Name = 'AyakaLauncher.exe' }) + $runtimeEntries
    Compress-Entries -ZipPath $bundle -Activity '打包整合包' -Entries $bundleEntries
}

# ---- 5. 产物清单 ----
Write-Progress -Activity 'AyakaLauncher 发布' -Completed
Write-Host ''
Write-Host '==== 发布产物 ===='
$exeSize = [math]::Round((Get-Item $publishedExe).Length / 1MB, 2)
Write-Host ("  {0}（{1} MB）" -f $publishedExe, $exeSize)

$runtimeSize = [math]::Round((Get-Item $zipRuntime).Length / 1GB, 2)
Write-Host ("  {0}（{1} GB）" -f $zipRuntime, $runtimeSize)

if ($WithBundle) {
    $bundleSize = [math]::Round((Get-Item $bundle).Length / 1GB, 2)
    Write-Host ("  {0}（{1} GB）" -f $bundle, $bundleSize)
}

Write-Host ''
Write-Host '发布完成。release\runtime.zip 仅供本机整合包与离线分发，不再随 Release 发布；运行时包统一由网盘 runtime.7z 分发。'
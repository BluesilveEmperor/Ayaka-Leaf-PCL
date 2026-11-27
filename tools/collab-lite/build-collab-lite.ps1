# ============================================================================
#  Ayaka 协作轻量端构建脚本（collab-lite）
# ----------------------------------------------------------------------------
#  产出：release\AyakaCollabLite_<版本>_<平台>.zip（≤ 50MB）+ .sha256 校验文件
#
#  打包内容（解压得到 AyakaCollabLite\ 文件夹）：
#    CollabLite.ps1   PowerShell 5.1 WinForms GUI（零运行时依赖）
#    CollabLite.bat   启动器（绕过执行策略启动 ps1）
#    README.txt       使用说明
#    easytier\        EasyTier v2.6.4 官方二进制（core / cli / Packet.dll）
#
#  EasyTier 版本与 SHA-256 和主程序 PCL.Core\Ayakaleaf\Collab\EasyTierPackage.cs
#  保持一致，保证组网协议兼容。首次构建从官方渠道（Gitee → GitHub → 镜像）
#  下载并逐一校验；通过后缓存到 %TEMP%\ayaka-collab-lite-cache\，离线可复用。
#
#  用法：pwsh build-collab-lite.ps1 [-Platform auto|x64|arm64] [-OutDir <目录>]
# ============================================================================
param(
    [ValidateSet('auto', 'x64', 'arm64')]
    [string]$Platform = 'auto',
    [string]$OutDir = ''
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$root = $PSScriptRoot                      # tools\collab-lite
$repoRoot = Split-Path -Parent (Split-Path -Parent $root)
if (-not $OutDir) { $OutDir = Join-Path $repoRoot 'release' }

# ---- EasyTier 版本与哈希（与 EasyTierPackage.cs 完全一致） ----
$EasyTierVersion = '2.6.4'
$EasyTierFiles = @('easytier-core.exe', 'easytier-cli.exe', 'Packet.dll')
$EasyTierHashes = @{
    'x86_64' = @{
        'easytier-core.exe' = 'da7eb2d24b5416f3d3407636949e964a0750e3f9dc53a828cb6799a57ead445d'
        'easytier-cli.exe'  = 'd8783e851e944b44a9b71b39fd02f227ec0a2a82b3165c55ead5dd32dcde53a1'
        'Packet.dll'        = 'c7c03a87eac7243ccbe331554624b18803010b740e311fc8cfddb573096eacac'
    }
    'arm64'   = @{
        'easytier-core.exe' = 'da912e2befd4c59c81228c0dac7e19f0190aa4221475117108ad2f0045b3065d'
        'easytier-cli.exe'  = '295852b6d68ab5c675437f19f37e975e90d41814ec76eabc135d3eb367bfccd0'
        'Packet.dll'        = 'fe56bf638c1f05751a4f36377d71f48fb7f43446e92475eda32dc0f4f2ebd60c'
    }
}

# ---- 平台探测：x64/AMD64 → x86_64；ARM64/AArch64 → arm64 ----
if ($Platform -eq 'auto') {
    $raw = $env:PROCESSOR_ARCHITEW6432
    if (-not $raw) { $raw = $env:PROCESSOR_ARCHITECTURE }
    switch ("$raw".Trim().ToUpperInvariant()) {
        'AMD64'   { $Platform = 'x64' }
        'X86_64'  { $Platform = 'x64' }
        'X64'     { $Platform = 'x64' }
        'ARM64'   { $Platform = 'arm64' }
        'AARCH64' { $Platform = 'arm64' }
        default   { throw "无法识别本机处理器架构：$raw；请用 -Platform 显式指定 x64 或 arm64。" }
    }
}
$arch = if ($Platform -eq 'x64') { 'x86_64' } else { 'arm64' }

# ---- 读取轻量端版本号（写在 CollabLite.ps1 里） ----
$litePs1Path = Join-Path $root 'CollabLite.ps1'
if (-not (Test-Path -LiteralPath $litePs1Path)) { throw "未找到 $litePs1Path" }
$liteSource = [System.IO.File]::ReadAllText($litePs1Path)
$versionMatch = [regex]::Match($liteSource, "\`$script:Version\s*=\s*'([^']+)'")
if (-not $versionMatch.Success) { throw '未能从 CollabLite.ps1 解析出 \$script:Version。' }
$liteVersion = $versionMatch.Groups[1].Value

# ---- 前置校验：三个源文件齐备 ----
foreach ($name in @('CollabLite.ps1', 'CollabLite.bat', 'README.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $name))) {
        throw "缺少源文件：$(Join-Path $root $name)"
    }
}

# ---- 工具函数 ----
function Get-FileSha256 {
    param([string]$Path)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [System.IO.File]::OpenRead($Path)
        try {
            return ([BitConverter]::ToString($sha.ComputeHash($stream)) -replace '-', '').ToLowerInvariant()
        }
        finally { $stream.Dispose() }
    }
    finally { $sha.Dispose() }
}

function Copy-WithBom {
    # 复制文本文件并确保 UTF-8 BOM（Windows PowerShell 5.1 无 BOM 会把中文读成乱码）
    param([string]$Source, [string]$Target)
    $text = [System.IO.File]::ReadAllText($Source, (New-Object System.Text.UTF8Encoding($false)))
    [System.IO.File]::WriteAllText($Target, $text, (New-Object System.Text.UTF8Encoding($true)))
}

# ---- 1. 准备 EasyTier 组件（缓存命中则直接校验复用，否则下载） ----
$cacheDir = Join-Path ([System.IO.Path]::GetTempPath()) 'ayaka-collab-lite-cache'
$archCache = Join-Path $cacheDir $arch
$needDownload = $false
foreach ($file in $EasyTierFiles) {
    $cached = Join-Path $archCache $file
    if (-not (Test-Path -LiteralPath $cached) -or (Get-FileSha256 $cached) -ne $EasyTierHashes[$arch][$file]) {
        $needDownload = $true
        break
    }
}

if ($needDownload) {
    $asset = "easytier-windows-$arch-v$EasyTierVersion.zip"
    $sources = @(
        "https://gitee.com/easytier/EasyTier/releases/download/v$EasyTierVersion/$asset"
        "https://github.com/EasyTier/EasyTier/releases/download/v$EasyTierVersion/$asset"
        "https://ghfast.top/https://github.com/EasyTier/EasyTier/releases/download/v$EasyTierVersion/$asset"
        "https://gh-proxy.com/https://github.com/EasyTier/EasyTier/releases/download/v$EasyTierVersion/$asset"
    )
    $archivePath = Join-Path $cacheDir $asset
    New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null

    $ok = $false
    $lastError = $null
    foreach ($url in $sources) {
        try {
            Write-Host "下载 $url …"
            Invoke-WebRequest -Uri $url -OutFile $archivePath -UseBasicParsing -TimeoutSec 300
            $ok = $true
            break
        }
        catch {
            $lastError = $_
            Write-Host "  下载失败：$($_.Exception.Message)"
        }
    }
    if (-not $ok) { throw "EasyTier 组件下载失败（所有源均不可用）：$($lastError.Exception.Message)" }

    # 解压并逐一校验 SHA-256（与主程序 EasyTierPackage 相同的防篡改策略）
    Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null
    New-Item -ItemType Directory -Path $archCache -Force | Out-Null
    $zip = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        foreach ($file in $EasyTierFiles) {
            $entry = $null
            foreach ($e in $zip.Entries) {
                $name = $e.FullName.Replace('\', '/')
                if ($name.EndsWith('/' + $file, [System.StringComparison]::OrdinalIgnoreCase) -or
                    $name.Equals($file, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $entry = $e
                    break
                }
            }
            if (-not $entry) { throw "发布包内未找到 $file" }
            $temp = Join-Path $archCache ($file + '.tmp')
            # ExtractToFile 是扩展方法，pwsh 下不能以实例方法调用，用静态形式
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $temp, $true)
            $hash = Get-FileSha256 $temp
            if ($hash -ne $EasyTierHashes[$arch][$file]) {
                Remove-Item -LiteralPath $temp -Force
                throw "$file 哈希校验失败（$hash）"
            }
            $target = Join-Path $archCache $file
            if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force }
            Move-Item -LiteralPath $temp -Destination $target
        }
    }
    finally { $zip.Dispose() }
    Remove-Item -LiteralPath $archivePath -Force -ErrorAction SilentlyContinue
    Write-Host 'EasyTier 组件下载并校验完成'
}
else {
    Write-Host 'EasyTier 组件缓存命中（哈希校验通过），跳过下载'
}

# ---- 2. 组装暂存目录 ----
$stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('ayaka-collab-lite-stage-' + [Guid]::NewGuid().ToString('N'))
$staging = Join-Path $stagingRoot 'AyakaCollabLite'
$easytierDir = Join-Path $staging 'easytier'
New-Item -ItemType Directory -Path $easytierDir -Force | Out-Null

# 脚本与说明：转为 UTF-8 BOM，避免 PowerShell 5.1 / 记事本读乱码
Copy-WithBom -Source $litePs1Path -Target (Join-Path $staging 'CollabLite.ps1')
Copy-Item -LiteralPath (Join-Path $root 'CollabLite.bat') -Destination $staging -Force
Copy-WithBom -Source (Join-Path $root 'README.txt') -Target (Join-Path $staging 'README.txt')
foreach ($file in $EasyTierFiles) {
    Copy-Item -LiteralPath (Join-Path $archCache $file) -Destination $easytierDir -Force
}

# ---- 3. 打包 zip（≤ 50MB 硬约束） ----
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$zipPath = Join-Path $OutDir "AyakaCollabLite_${liteVersion}_${Platform}.zip"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Write-Host "打包 $zipPath …"
[System.IO.Compression.ZipFile]::CreateFromDirectory($stagingRoot, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)

$zipSize = (Get-Item -LiteralPath $zipPath).Length
$sizeLimit = 50MB
if ($zipSize -gt $sizeLimit) {
    Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    throw ('轻量端安装包超出体积限制：{0:N1} MB > 50 MB。请检查 EasyTier 组件体积。' -f ($zipSize / 1MB))
}

# ---- 4. 生成 SHA-256 校验文件（与 runtime\images\*.sha256 同格式） ----
$zipHash = Get-FileSha256 $zipPath
$sidecarPath = $zipPath + '.sha256'
[System.IO.File]::WriteAllText($sidecarPath, "$zipHash  $(Split-Path -Leaf $zipPath)`n", (New-Object System.Text.UTF8Encoding($false)))

Remove-Item -LiteralPath $stagingRoot -Recurse -Force

# ---- 5. 产物清单 ----
Write-Host ''
Write-Host '==== 协作轻量端产物 ===='
Write-Host ('  {0}（{1:N1} MB / 上限 50 MB）' -f $zipPath, ($zipSize / 1MB))
Write-Host ('  {0}' -f $sidecarPath)
Write-Host ('  平台：{0}（EasyTier {1} / {2}），版本 {3}' -f $Platform, $EasyTierVersion, $arch, $liteVersion)

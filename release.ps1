# AyakaLauncher 发布打包脚本
# ---------------------------------------------------------------------------
# 默认产出（与 GitHub Release 附件命名对齐）：
#   release\AyakaLauncher_<配置>_<平台>.exe   —— 可执行文件
#   release\runtime.zip                       —— 内嵌运行时（约 1.8GB，含 4 个镜像包：
#                                                sharelatex-server-pro / git-bridge /
#                                                mongo-8.0 / redis-7；主镜像 1262.9MB，
#                                                超过 GitHub 普通仓库 100MB 单文件限制，
#                                                故不入库，仅由 Release 附件分发）
# 可选产出（-WithBundle）：
#   release\AyakaLauncher_<版本>_<配置>_<平台>.zip —— exe + runtime 的整合包（约 1.8GB）
#
# 本机日常运行请用 dist\（junction，不占额外空间）；
# 对外发布用本脚本产出的 exe 与 runtime.zip。
#
# 用法：pwsh release.ps1 [-Configuration Release] [-Platform x64] [-WithBundle]
param(
    [string]$Configuration = 'Release',
    [string]$Platform = 'x64',
    [switch]$WithBundle
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$artifact = Join-Path $root 'artifact'
$releaseDir = Join-Path $root 'release'
$runtimeDir = Join-Path $root 'runtime'
# 显式使用 Windows 自带的 bsdtar（System32\tar.exe），
# 避免 PATH 中的 GNU tar（如 Git 附带的 tar）不支持 -a 生成 zip 归档。
$tarExe = Join-Path $env:SystemRoot 'System32\tar.exe'
if (-not (Test-Path $tarExe)) {
    throw "未找到 Windows 自带的 tar.exe：$tarExe（需要 Windows 10 1803 及以上版本）"
}
$runtimeImages = Join-Path $runtimeDir 'images'
# git-bridge.tar.gz 为 Git-Bridge / MCP / 三方同步的必选镜像（启用对应功能时容器必需）
$requiredImages = @('sharelatex-server-pro.tar.gz', 'git-bridge.tar.gz', 'mongo-8.0.tar.gz', 'redis-7.tar.gz')

# ---- 0. 前置校验：runtime/images 三个镜像必须齐备 ----
# 缺失时提示从 Release 附件 runtime.zip 解压恢复，避免产出残缺的运行时包。
foreach ($image in $requiredImages) {
    $imagePath = Join-Path $runtimeImages $image
    if (-not (Test-Path $imagePath)) {
        throw "缺少运行时镜像：$imagePath。请先从 Release 附件 runtime.zip 解压到 runtime\ 目录后重试。"
    }
}

# ---- 1. Release 发布 ----
Write-Host "发布 $Configuration/$Platform …"
dotnet publish (Join-Path $root 'Plain Craft Launcher 2\Plain Craft Launcher 2.csproj') `
    -p:Configuration=$Configuration -p:Platform=$Platform `
    -p:DeleteExistingFiles=true -o $artifact --no-self-contained --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（$LASTEXITCODE）" }

# ---- 2. 复制 exe 到 release（命名与 Release 附件一致）----
$exe = Join-Path $artifact 'AyakaLauncher.exe'
if (-not (Test-Path $exe)) { throw "未找到 $exe" }
$version = (Get-Item $exe).VersionInfo.FileVersion -replace '\.0$', ''
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null

$publishedExe = Join-Path $releaseDir "AyakaLauncher_${Configuration}_${Platform}.exe"
Copy-Item $exe $publishedExe -Force

# ---- 3. 打包 runtime.zip（约 1.8GB）----
# bsdtar（Windows 自带）：-a 按 .zip 扩展名选择 zip 格式，读取真实文件，不经 junction。
$zipRuntime = Join-Path $releaseDir 'runtime.zip'
if (Test-Path $zipRuntime) { Remove-Item $zipRuntime -Force }

Write-Host '打包 runtime.zip（runtime 约 1.8GB，稍候）…'
& $tarExe -a -cf $zipRuntime -C $root runtime
if ($LASTEXITCODE -ne 0) { throw "tar 打包 runtime.zip 失败（$LASTEXITCODE）" }

# ---- 4. 可选：整合包（exe + runtime，约 1.7GB）----
$bundle = $null
if ($WithBundle) {
    $bundle = Join-Path $releaseDir "AyakaLauncher_${version}_${Configuration}_${Platform}.zip"
    if (Test-Path $bundle) { Remove-Item $bundle -Force }

    Write-Host '打包整合包（exe + runtime，约 1.7GB，稍候）…'
    & $tarExe -a -cf $bundle -C $artifact AyakaLauncher.exe -C $root runtime
    if ($LASTEXITCODE -ne 0) { throw "tar 打包整合包失败（$LASTEXITCODE）" }
}

# ---- 5. 产物清单 ----
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
Write-Host '发布完成。runtime.zip 需随 Release 一同分发；如仅本地自用则无需上传。'
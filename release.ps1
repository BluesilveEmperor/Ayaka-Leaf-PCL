# AyakaLauncher 发布打包脚本
# ---------------------------------------------------------------------------
# 产出 release\AyakaLauncher_<版本>_<配置>_<平台>.zip
# zip 内真实包含 AyakaLauncher.exe + runtime\images（约 1.9GB 镜像包），可直接分发。
#
# 本机日常运行请用 dist\（junction，不占额外空间）；
# 对外发布用本脚本产出的 zip。
#
# 用法：pwsh release.ps1 [-Configuration Release] [-Platform x64]
param(
    [string]$Configuration = 'Release',
    [string]$Platform = 'x64'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$artifact = Join-Path $root 'artifact'
$releaseDir = Join-Path $root 'release'

# ---- 1. Release 发布 ----
Write-Host "发布 $Configuration/$Platform …"
dotnet publish (Join-Path $root 'Plain Craft Launcher 2\Plain Craft Launcher 2.csproj') `
    -p:Configuration=$Configuration -p:Platform=$Platform `
    -p:DeleteExistingFiles=true -o $artifact --no-self-contained --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（$LASTEXITCODE）" }

# ---- 2. 组装发布 zip（exe + runtime，真实文件）----
$exe = Join-Path $artifact 'AyakaLauncher.exe'
if (-not (Test-Path $exe)) { throw "未找到 $exe" }
$version = (Get-Item $exe).VersionInfo.FileVersion -replace '\.0$', ''
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null
$zip = Join-Path $releaseDir "AyakaLauncher_${version}_${Configuration}_${Platform}.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }

Write-Host '打包 zip（runtime 约 1.9GB，稍候）…'
# bsdtar：exe 放 zip 根，runtime 目录整体放入（读取真实文件，不经 junction）
tar -a -cf $zip -C $artifact AyakaLauncher.exe -C $root runtime
if ($LASTEXITCODE -ne 0) { throw "tar 打包失败（$LASTEXITCODE）" }

$zipSize = [math]::Round((Get-Item $zip).Length / 1GB, 2)
Write-Host "发布完成：$zip （$zipSize GB）"

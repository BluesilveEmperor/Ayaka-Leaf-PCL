# 运行时镜像构建入口（pandoc → texlive 变体 → wslc save → SHA256 清单）
# 实际编排逻辑在 build-images.py，本脚本只做参数透传，便于在 pwsh 中一键调用。
# 用法：
#   pwsh tools/runtime/build-images.ps1                       # 精选集合变体（默认）
#   pwsh tools/runtime/build-images.ps1 -Variant full         # scheme-full 超集
#   pwsh tools/runtime/build-images.ps1 -SkipPandoc           # 复用已构建的 pandoc 层
#   pwsh tools/runtime/build-images.ps1 -NoSave               # 只构建不导出
param(
    [ValidateSet("packages", "full", "cn")]
    [string]$Variant = "packages",
    [switch]$SkipPandoc,
    [switch]$NoSave,
    [string]$Mirror,
    [string]$OutDir
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

$pyArgs = @((Join-Path $scriptDir "build-images.py"), "--variant", $Variant)
if ($SkipPandoc) { $pyArgs += "--skip-pandoc" }
if ($NoSave) { $pyArgs += "--no-save" }
if ($Mirror) { $pyArgs += @("--mirror", $Mirror) }
if ($OutDir) { $pyArgs += @("--out-dir", $OutDir) }

& python @pyArgs
exit $LASTEXITCODE

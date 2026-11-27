# =====================================================================
# build-templates.ps1 - 打包内置中文模板库
# 用法: pwsh -NoProfile -ExecutionPolicy Bypass -File build-templates.ps1
# 输入: tools/ayakaleaf/templates/<id>/{template.json, preview.png, content/**}
# 输出: Plain Craft Launcher 2/Resources/ayakaleaf/templates/<id>.zip
#       zip 结构: template.json + preview.png + content/**（服务端上传时
#       由 TemplateLibraryService 仅提取 content/** 重新打包）
# =====================================================================
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$templatesRoot = Join-Path $scriptDir 'templates'
$outDir = Join-Path $scriptDir '..\..\Plain Craft Launcher 2\Resources\ayakaleaf\templates'

if (-not (Test-Path -LiteralPath $outDir)) {
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
}

# zip 内条目使用正斜杠分隔符，UTF-8 文件名（本库文件名均为 ASCII，天然安全）
function Add-DirEntries([System.IO.Compression.ZipArchive]$zip, [string]$srcDir, [string]$prefix) {
    Get-ChildItem -LiteralPath $srcDir -Recurse -File | ForEach-Object {
        $rel = [System.IO.Path]::GetFullPath($_.FullName).Substring([System.IO.Path]::GetFullPath($srcDir).Length + 1)
        $entryName = $prefix + ($rel -replace '\\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}

$templates = Get-ChildItem -LiteralPath $templatesRoot -Directory | Sort-Object Name
if ($templates.Count -eq 0) {
    throw "模板源目录为空: $templatesRoot"
}

foreach ($tpl in $templates) {
    $metaPath = Join-Path $tpl.FullName 'template.json'
    $contentDir = Join-Path $tpl.FullName 'content'
    $previewPath = Join-Path $tpl.FullName 'preview.png'
    foreach ($required in @($metaPath, $contentDir, $previewPath)) {
        if (-not (Test-Path -LiteralPath $required)) {
            throw "模板 $($tpl.Name) 缺少必需文件: $required"
        }
    }

    # 校验 template.json 元数据
    $meta = Get-Content -LiteralPath $metaPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($meta.id -ne $tpl.Name) {
        throw "模板 $($tpl.Name) 的 template.json id 不匹配: $($meta.id)"
    }

    $zipPath = Join-Path $outDir "$($tpl.Name).zip"
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    $fs = [System.IO.File]::Open($zipPath, [System.IO.FileMode]::CreateNew)
    $zip = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $metaPath, 'template.json', [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $previewPath, 'preview.png', [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        Add-DirEntries $zip $contentDir 'content/'
    }
    finally {
        $zip.Dispose()
        $fs.Dispose()
    }

    $size = (Get-Item -LiteralPath $zipPath).Length
    Write-Host ("已打包 {0}.zip ({1:N0} 字节, 版本 {2})" -f $tpl.Name, $size, $meta.version)
}

Write-Host "模板库打包完成，共 $($templates.Count) 个模板。"

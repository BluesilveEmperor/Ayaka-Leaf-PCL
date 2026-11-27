# =====================================================================
# gen-template-previews.ps1 - 为内置模板库生成 preview.png 预览图
# 用法: pwsh -NoProfile -ExecutionPolicy Bypass -File gen-template-previews.ps1
# 说明: 使用 GDI+ 绘制 A4 版式示意图（600x420），按模板分类着色。
#       预览图为静态资源，模板内容变更后无需重新生成。
# =====================================================================
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# 脚本所在目录 = tools/ayakaleaf；模板根目录 = tools/ayakaleaf/templates
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$templatesRoot = Join-Path $scriptDir 'templates'

# 分类 -> 主题色
$categoryColors = @{
    'exam'    = [System.Drawing.Color]::FromArgb(230, 126, 34)   # 橙
    'thesis'  = [System.Drawing.Color]::FromArgb(46, 134, 193)   # 蓝
    'contest' = [System.Drawing.Color]::FromArgb(39, 174, 96)    # 绿
    'journal' = [System.Drawing.Color]::FromArgb(142, 68, 172)   # 紫
}

# 分类 -> 布局（journal 为双栏，其余单栏）
$twoColumnCategories = @('journal')

function New-RoundedRectPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc(($x + $w - $d), $y, $d, $d, 270, 90)
    $path.AddArc(($x + $w - $d), ($y + $h - $d), $d, $d, 0, 90)
    $path.AddArc($x, ($y + $h - $d), $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Draw-TextLine([System.Drawing.Graphics]$g, [single]$x, [single]$y, [single]$w, [single]$h, [System.Drawing.Color]$color) {
    $brush = New-Object System.Drawing.SolidBrush($color)
    $g.FillRectangle($brush, $x, $y, $w, $h)
    $brush.Dispose()
}

function New-TemplatePreview([string]$templateDir, [hashtable]$meta) {
    $width = 600
    $height = 420
    $bmp = New-Object System.Drawing.Bitmap($width, $height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias

    # 背景
    $bgBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(238, 242, 247))
    $g.FillRectangle($bgBrush, 0, 0, $width, $height)
    $bgBrush.Dispose()

    $theme = $categoryColors[$meta.category]
    $themeBrush = New-Object System.Drawing.SolidBrush($theme)

    # 页面卡片（A4 比例）
    $pageW = [single]216
    $pageH = [single]305
    $pageX = [single](($width - $pageW) / 2)
    $pageY = [single](($height - $pageH) / 2 + 8)
    $shadowPath = New-RoundedRectPath ($pageX + 4) ($pageY + 5) $pageW $pageH 6
    $shadowBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(60, 40, 60, 80))
    $g.FillPath($shadowBrush, $shadowPath)
    $shadowBrush.Dispose()
    $shadowPath.Dispose()

    $pagePath = New-RoundedRectPath $pageX $pageY $pageW $pageH 6
    $pageBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $g.FillPath($pageBrush, $pagePath)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(200, 210, 220), [single]1)
    $g.DrawPath($pen, $pagePath)
    $pageBrush.Dispose()
    $pen.Dispose()
    $pagePath.Dispose()

    # 分类徽章（左上）
    $badgeText = $meta.badge
    $badgeFont = New-Object System.Drawing.Font('Microsoft YaHei', 11, [System.Drawing.FontStyle]::Bold)
    $badgeSize = $g.MeasureString($badgeText, $badgeFont)
    $badgeW = [single]($badgeSize.Width + 20)
    $badgeH = [single]28
    $badgeX = [single]($pageX - 6)
    $badgeY = [single]($pageY - 34)
    $badgePath = New-RoundedRectPath $badgeX $badgeY $badgeW $badgeH 5
    $g.FillPath($themeBrush, $badgePath)
    $badgePath.Dispose()
    $textBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $g.DrawString($badgeText, $badgeFont, $textBrush, ($badgeX + 10), ($badgeY + 4))
    $badgeFont.Dispose()

    # 页面内容版式
    $margin = [single]22
    $innerX = $pageX + $margin
    $innerW = $pageW - 2 * $margin
    $lineColor = [System.Drawing.Color]::FromArgb(190, 198, 208)
    $headColor = $theme
    $y = $pageY + 24

    # 标题块
    $titleFont = New-Object System.Drawing.Font('Microsoft YaHei', 10, [System.Drawing.FontStyle]::Bold)
    $g.DrawString($meta.title, $titleFont, $textBrush, ($innerX + $innerW / 2 - $g.MeasureString($meta.title, $titleFont).Width / 2), $y)
    $titleFont.Dispose()
    $y += 26
    Draw-TextLine $g $innerX $y ($innerW * 0.5) 3 $headColor
    $y += 12

    # 正文行
    $twoColumn = $twoColumnCategories -contains $meta.category
    if ($twoColumn) {
        # 双栏示意：左侧摘要块 + 双栏正文
        Draw-TextLine $g ($innerX + $innerW * 0.12) $y ($innerW * 0.76) 4 $lineColor
        $y += 8
        Draw-TextLine $g ($innerX + $innerW * 0.20) $y ($innerW * 0.60) 4 $lineColor
        $y += 16
        $colW = [single](($innerW - 14) / 2)
        $colY1 = $y
        $colY2 = $y
        for ($i = 0; $i -lt 14; $i++) {
            $left = (($i % 2) -eq 0)
            $yy = if ($left) { $colY1 } else { $colY2 }
            $xx = if ($left) { $innerX } else { ($innerX + $colW + 14) }
            $ww = $colW * (0.72 + (($i * 7) % 10) / 24)
            Draw-TextLine $g $xx $yy ([single]$ww) 4 $lineColor
            if ($i -eq 3) {
                Draw-TextLine $g $xx ($yy + 10) ([single]($colW * 0.86)) 4 $headColor
            }
            if ($left) { $colY1 += 14 } else { $colY2 += 14 }
        }
    }
    else {
        # 单栏示意：交替段落行 + 小节标题
        for ($i = 0; $i -lt 16; $i++) {
            $ww = $innerW * (0.62 + (($i * 11) % 12) / 30)
            if (($i % 5) -eq 0) {
                Draw-TextLine $g $innerX $y ([single]($innerW * 0.28)) 5 $headColor
                $y += 12
            }
            Draw-TextLine $g $innerX $y ([single]$ww) 4 $lineColor
            $y += 14
        }
    }

    $textBrush.Dispose()
    $themeBrush.Dispose()
    $g.Dispose()

    $outPath = Join-Path $templateDir 'preview.png'
    $bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "已生成 $outPath"
}

# 四类模板的预览元数据（badge/title 仅用于绘图，不进 template.json）
$previewMeta = @(
    @{ id = 'exam';    category = 'exam';    title = '期末考试试卷'; badge = '试卷' }
    @{ id = 'thesis';  category = 'thesis';  title = '本科毕业论文'; badge = '毕设' }
    @{ id = 'contest'; category = 'contest'; title = '竞赛报告';     badge = '竞赛' }
    @{ id = 'journal'; category = 'journal'; title = '期刊论文';     badge = '期刊' }
)

foreach ($meta in $previewMeta) {
    $dir = Join-Path $templatesRoot $meta.id
    if (-not (Test-Path -LiteralPath (Join-Path $dir 'template.json'))) {
        throw "未找到模板目录: $dir"
    }
    New-TemplatePreview $dir $meta
}

# ============================================================================
#  Ayaka 协作轻量端（CollabLite）
# ----------------------------------------------------------------------------
#  给未安装 AyakaLauncher 的队友使用的最小协作客户端：
#  输入房主邀请码（A/XXXX-XXXX-XXXX-XXXX）→ 加入 EasyTier 虚拟网络 →
#  寻找房主 → 建立本地端口转发 → 浏览器访问 http://localhost:<端口> 协同编辑。
#
#  实现与主程序 PCL.Core\Ayakaleaf\Collab\ 保持同一契约（加入流程逐行对齐
#  CollabSession.JoinAsync / EasyTierConfig.ToToml / CollabCode）：
#   - 邀请码：16 位 base31 + 位置加权模 31 校验（字母表去除 0/1/I/O/L）
#   - 网络：network_name = ayaka-collab-<前 8 位>，network_secret = <后 8 位>
#   - 发现协议：房主 hostname 编码端口结构 ayaka-host-<主端口>-<语言数>，
#     队友 hostname 为 ayaka-guest-<8 位十六进制>
#   - 转发：本地沿用房主同端口号，被占用时改用空闲端口
#   - 内置 EasyTier v2.6.4 官方二进制（与主程序同版本，协议兼容）
#
#  运行要求：Windows 10 1607+ / 64 位，系统自带 PowerShell 5.1，无需任何安装。
#
#  状态机（UI 定时器驱动，每步只做一次快速阻塞调用，避免长阻塞卡死界面）：
#   Idle → WaitCore → FindHost → Forward → Probe → Ready / Error
# ============================================================================
#requires -Version 5.1

$ErrorActionPreference = 'Stop'

# ---- 常量 ----
$script:Version = '1.0.0'
$script:BaseDir = $PSScriptRoot
$script:CoreExe = Join-Path $script:BaseDir 'easytier\easytier-core.exe'
$script:CliExe = Join-Path $script:BaseDir 'easytier\easytier-cli.exe'

# 邀请码字母表（与主程序 CollabCode 一致：去除 0/1/I/O/L 防混淆）
$script:Alphabet = '23456789ABCDEFGHJKMNPQRSTUVWXYZ'
$script:HostPrefix = 'ayaka-host-'
$script:GuestPrefix = 'ayaka-guest-'

# 内置公共节点（与主程序 CollabNodes.BuiltIn 一致）
$script:BuiltInNodes = @(
    'tcp://225284.xyz:11010'
    'tcp://easytier.weiai.org.cn:11010'
    'udp://et.basd1.de:11010'
    'wss://et.chinokou.cn'
    'https://et-public.lctn.top'
)

# ---- 运行状态（全部挂在 $script: 上，事件处理器跨作用域可见） ----
$script:State = 'Idle'            # Idle / WaitCore / FindHost / Forward / Probe / Ready / Error
$script:CoreProcess = $null
$script:RpcPort = 0
$script:ConfigPath = ''
$script:InviteDigits = ''         # 16 位数字串（大写无分隔符）
$script:HostInfo = $null          # 房主成员（hostname/IPv4/latency/direct）
$script:MainPort = 0              # 房主主端口
$script:PortCount = 0             # 房主语言端口数
$script:Forwards = @()            # 已建立的转发：@{ Local=..; Remote=.. }
$script:Languages = @()           # 探测到的语言：@{ Name=..; LocalPort=.. }
$script:FindAttempts = 0
$script:ForwardIndex = 0
$script:ForwardRetry = 0
$script:ProbeIndex = 0
$script:Tick = 0
$script:HostMissCount = 0

# ============================================================
#  工具函数
# ============================================================

function Write-Log {
    param([string]$Text)
    $line = ('[{0:HH:mm:ss}] {1}' -f [datetime]::Now, $Text)
    if ($script:TxtLog) {
        $script:TxtLog.AppendText($line + [Environment]::NewLine)
        # 控制台同步输出（ bat 直接 -File 运行时通常无控制台，忽略报错）
        try { Write-Host $line } catch { }
    }
}

function Get-RandomHex {
    param([int]$Digits)
    $bytes = New-Object byte[] (($Digits + 1) / 2)
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    return ([BitConverter]::ToString($bytes) -replace '-', '').ToLowerInvariant().Substring(0, $Digits)
}

function Test-LocalPortFree {
    param([int]$Port)
    $listener = $null
    try {
        $listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, $Port)
        $listener.Start()
        return $true
    } catch [System.Net.Sockets.SocketException] {
        return $false
    } finally {
        if ($listener) { $listener.Stop() }
    }
}

function Get-FreePort {
    $listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        return ($listener.LocalEndpoint -as [System.Net.IPEndPoint]).Port
    } finally {
        $listener.Stop()
    }
}

# 从任意粘贴文本中提取邀请码并校验（与主程序 CollabCode.Find 逻辑一致）
function Find-InviteCode {
    param([string]$Text)
    if (-not $Text) { return $null }
    $m = [regex]::Match($Text, '(?i)(?:A/)?([2-9A-HJKMNP-Z]{4})[-\s]*([2-9A-HJKMNP-Z]{4})[-\s]*([2-9A-HJKMNP-Z]{4})[-\s]*([2-9A-HJKMNP-Z]{4})')
    if (-not $m.Success) { return $null }
    $digits = ($m.Groups[1].Value + $m.Groups[2].Value + $m.Groups[3].Value + $m.Groups[4].Value).ToUpperInvariant()
    if ($digits.Length -ne 16) { return $null }
    $sum = 0
    for ($i = 0; $i -lt 16; $i++) {
        $idx = $script:Alphabet.IndexOf($digits[$i])
        if ($idx -lt 0) { return $null }
        $sum += $idx * ($i + 1)
    }
    if (($sum % 31) -ne 0) { return $null }
    return $digits
}

# 生成队友侧 EasyTier 配置（与主程序 EasyTierConfig.ToToml 队友参数一致）
function New-GuestToml {
    $hostname = '{0}{1}' -f $script:GuestPrefix, (Get-RandomHex 8)
    $networkName = 'ayaka-collab-' + $script:InviteDigits.Substring(0, 8)
    $networkSecret = $script:InviteDigits.Substring(8)

    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine('hostname = "' + $hostname + '"')
    [void]$sb.AppendLine('dhcp = true')
    [void]$sb.AppendLine('listeners = ["tcp://0.0.0.0:0", "udp://0.0.0.0:0"]')
    [void]$sb.AppendLine('tcp_whitelist = ["0"]')
    [void]$sb.AppendLine('udp_whitelist = []')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('[network_identity]')
    [void]$sb.AppendLine('network_name = "' + $networkName + '"')
    [void]$sb.AppendLine('network_secret = "' + $networkSecret + '"')
    [void]$sb.AppendLine()
    foreach ($node in $script:BuiltInNodes) {
        [void]$sb.AppendLine('[[peer]]')
        [void]$sb.AppendLine('uri = "' + $node + '"')
        [void]$sb.AppendLine()
    }
    [void]$sb.AppendLine('[flags]')
    [void]$sb.AppendLine('no_tun = true')
    [void]$sb.AppendLine('latency_first = true')
    [void]$sb.AppendLine('enable_kcp_proxy = true')
    [void]$sb.AppendLine('enable_quic_proxy = true')
    [void]$sb.AppendLine('data_compress_algo = 2')
    [void]$sb.AppendLine('private_mode = true')
    [void]$sb.AppendLine('p2p_only = false')
    return $sb.ToString()
}

# 调用 easytier-cli（本地 RPC），返回 @{ Ok; Output; Error }
function Invoke-EasyTierCli {
    param([string[]]$Arguments)
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $script:CliExe
    $psi.Arguments = '-p 127.0.0.1:' + $script:RpcPort + ' ' + ($Arguments -join ' ')
    $psi.WorkingDirectory = Split-Path -Parent $script:CliExe
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    $cli = New-Object System.Diagnostics.Process
    $cli.StartInfo = $psi
    try {
        [void]$cli.Start()
    } catch {
        return @{ Ok = $false; Output = ''; Error = $_.Exception.Message }
    }
    $stdout = $cli.StandardOutput.ReadToEndAsync()
    $stderr = $cli.StandardError.ReadToEndAsync()
    if (-not $cli.WaitForExit(10000)) {
        try { $cli.Kill() } catch { }
        return @{ Ok = $false; Output = ''; Error = 'CLI 超时' }
    }
    $out = $stdout.Result
    $err = $stderr.Result
    if ($cli.ExitCode -ne 0) {
        return @{ Ok = $false; Output = $out; Error = $err.Trim() }
    }
    return @{ Ok = $true; Output = $out; Error = $err }
}

# 查询成员列表（peer）；失败返回 $null
function Get-Peers {
    $r = Invoke-EasyTierCli @('-o', 'json', 'peer')
    if (-not $r.Ok) { return $null }
    try {
        return ($r.Output.Trim() | ConvertFrom-Json)
    } catch {
        return $null
    }
}

# 已连接的公共节点数；失败返回 -1（与主程序语义一致）
function Get-ConnectedNodeCount {
    $r = Invoke-EasyTierCli @('-o', 'json', 'connector')
    if (-not $r.Ok) { return -1 }
    try {
        $items = @($r.Output.Trim() | ConvertFrom-Json)
        $count = 0
        foreach ($item in $items) {
            $status = "$($item.status)"
            if ($status -eq '0') { $count++ }
        }
        return $count
    } catch {
        return -1
    }
}

# 置为错误态：停进程、复位界面
function Set-Failed {
    param([string]$Reason)
    Stop-Network -Quiet
    $script:State = 'Error'
    $script:LabStatus.Text = '加入失败：' + $Reason
    $script:LabStatus.ForeColor = [System.Drawing.Color]::Firebrick
    $script:BtnJoin.Text = '加入协作'
    $script:BtnJoin.Enabled = $true
    $script:BtnOpen.Enabled = $false
    Write-Log ('加入失败：' + $Reason)
}

# 停止网络组件并清理配置文件
function Stop-Network {
    param([switch]$Quiet)
    if ($script:CoreProcess) {
        try {
            if (-not $script:CoreProcess.HasExited) {
                $script:CoreProcess.Kill()
                $script:CoreProcess.WaitForExit(3000) | Out-Null
            }
        } catch { }
        try { $script:CoreProcess.Dispose() } catch { }
        $script:CoreProcess = $null
        if (-not $Quiet) { Write-Log '网络组件已停止' }
    }
    if ($script:ConfigPath -and (Test-Path -LiteralPath $script:ConfigPath)) {
        try { Remove-Item -LiteralPath $script:ConfigPath -Force } catch { }
    }
    $script:ConfigPath = ''
    $script:RpcPort = 0
    $script:HostInfo = $null
    $script:Forwards = @()
    $script:Languages = @()
    $script:HostMissCount = 0
}

# 复位到空闲态（断开按钮）
function Reset-ToIdle {
    Stop-Network
    $script:State = 'Idle'
    $script:LabStatus.Text = '未加入协作。请粘贴房主分享的邀请码后点击「加入协作」。'
    $script:LabStatus.ForeColor = [System.Drawing.SystemColors]::ControlText
    $script:LabNetwork.Text = ''
    $script:LabAddress.Text = ''
    $script:LabLanguages.Text = ''
    $script:BtnJoin.Text = '加入协作'
    $script:BtnJoin.Enabled = $true
    $script:BtnOpen.Enabled = $false
}

# ============================================================
#  状态机（500ms 定时器驱动）
# ============================================================

function Update-StateMachine {
    $script:Tick++
    switch ($script:State) {
        'WaitCore' {
            # 等待 RPC 就绪（core 启动需要一点时间），随后进入寻主
            if ($script:CoreProcess.HasExited) {
                Set-Failed ('网络组件异常退出（退出码 ' + $script:CoreProcess.ExitCode + '）')
                return
            }
            if ($script:Tick -ge 2) {
                $script:State = 'FindHost'
                $script:FindAttempts = 0
                $script:LabStatus.Text = '正在寻找房主…（首次连接公共节点可能需要十几秒）'
            }
        }
        'FindHost' {
            if ($script:CoreProcess.HasExited) {
                Set-Failed ('网络组件异常退出（退出码 ' + $script:CoreProcess.ExitCode + '）')
                return
            }
            $script:FindAttempts++
            $peers = Get-Peers
            $hostPeer = $null
            if ($peers) {
                foreach ($p in @($peers)) {
                    if (-not $p.hostname) { continue }
                    if ($p.hostname.StartsWith($script:HostPrefix) -and $p.ipv4) {
                        $hostPeer = $p
                        break
                    }
                }
            }
            if ($hostPeer) {
                # 解码房主端口结构：ayaka-host-<主端口>-<语言数>
                $m = [regex]::Match($hostPeer.hostname, ('^' + [regex]::Escape($script:HostPrefix) + '(\d+)-(\d+)$'))
                if (-not $m.Success) {
                    Set-Failed ('房主端口信息解析失败：' + $hostPeer.hostname)
                    return
                }
                $script:MainPort = [int]$m.Groups[1].Value
                $script:PortCount = [int]$m.Groups[2].Value
                if ($script:MainPort -le 0 -or $script:MainPort -gt 65535 -or $script:PortCount -le 0 -or $script:PortCount -gt 18) {
                    Set-Failed ('房主端口信息非法：' + $hostPeer.hostname)
                    return
                }
                $script:HostInfo = @{
                    Hostname = $hostPeer.hostname
                    IPv4     = "$($hostPeer.ipv4)"
                }
                $script:State = 'Forward'
                $script:ForwardIndex = 0
                $script:ForwardRetry = 0
                $script:LabStatus.Text = '已找到房主，正在建立端口转发…'
                Write-Log ('找到房主：' + $hostPeer.hostname + '（' + $hostPeer.ipv4 + '），共 ' + $script:PortCount + ' 个端口')
                return
            }
            if ($script:FindAttempts -ge 60) {
                Set-Failed '未能找到房主（已等待 30 秒）。请确认邀请码正确且房主已开启协作。'
                return
            }
            if (($script:FindAttempts % 4) -eq 0) {
                $nodes = Get-ConnectedNodeCount
                if ($nodes -gt 0) {
                    $script:LabNetwork.Text = '已连接 ' + $nodes + ' 个公共中转节点，正在等待房主上线…'
                } else {
                    $script:LabNetwork.Text = '正在连接公共节点…（若长时间无进展请检查网络或放行防火墙）'
                }
            }
        }
        'Forward' {
            if ($script:CoreProcess.HasExited) {
                Set-Failed ('网络组件异常退出（退出码 ' + $script:CoreProcess.ExitCode + '）')
                return
            }
            $i = $script:ForwardIndex
            $remotePort = $script:MainPort + $i
            # 本地沿用房主同端口号（浏览器地址与房主一致）；被占用时改用空闲端口
            $localPort = $remotePort
            if (-not (Test-LocalPortFree $remotePort)) { $localPort = Get-FreePort }
            $target = $script:HostInfo.IPv4 + ':' + $remotePort
            $bind = '127.0.0.1:' + $localPort
            $r = Invoke-EasyTierCli @('port-forward', 'add', 'tcp', $bind, $target)
            if ($r.Ok) {
                Write-Log ('端口转发：127.0.0.1:' + $localPort + ' → ' + $target)
                $script:Forwards += @{ Local = $localPort; Remote = $remotePort }
                $script:ForwardIndex++
                $script:ForwardRetry = 0
            } else {
                $script:ForwardRetry++
                Write-Log ('端口转发失败（' + $bind + ' → ' + $target + '，第 ' + $script:ForwardRetry + ' 次）：' + $r.Error)
                if ($script:ForwardRetry -ge 3) {
                    Set-Failed ('端口转发建立失败（远程端口 ' + $remotePort + '），请重试')
                    return
                }
            }
            if ($script:ForwardIndex -ge $script:PortCount) {
                $script:State = 'Probe'
                $script:ProbeIndex = 0
                $script:LabStatus.Text = '正在探测服务语言…'
            }
        }
        'Probe' {
            # 主端口固定为 English；其余端口读取 /login 页面的 lang 属性
            $i = $script:ProbeIndex
            $forward = $script:Forwards[$i]
            if ($i -eq 0) {
                $script:Languages += @{ Name = 'English'; LocalPort = $forward.Local }
            } else {
                $name = $null
                try {
                    $resp = Invoke-WebRequest -Uri ('http://localhost:' + $forward.Local + '/login') -TimeoutSec 3 -UseBasicParsing
                    $m = [regex]::Match($resp.Content, '<html[^>]*\blang\s*=\s*"([a-zA-Z\-]+)"')
                    if ($m.Success) { $name = $m.Groups[1].Value }
                } catch { }
                if ($name) {
                    # 已知语言代码转显示名（与主程序 SupportedLanguages 的自称对齐）
                    switch ($name.ToLowerInvariant()) {
                        'en'      { $name = 'English' }
                        'en-us'   { $name = 'English' }
                        'en-gb'   { $name = 'English' }
                        'zh-cn'   { $name = '简体中文' }
                        'zh-tw'   { $name = '繁體中文' }
                        'ja-jp'   { $name = '日本語' }
                        'fr-fr'   { $name = 'Français' }
                        'de-de'   { $name = 'Deutsch' }
                        'ru-ru'   { $name = 'Русский' }
                        'es-es'   { $name = 'Español' }
                        'pt-br'   { $name = 'Português' }
                        'ko-kr'   { $name = '한국어' }
                        'it-it'   { $name = 'Italiano' }
                        default   { }
                    }
                    $script:Languages += @{ Name = $name; LocalPort = $forward.Local }
                }
            }
            $script:ProbeIndex++
            if ($script:ProbeIndex -ge $script:Forwards.Count) {
                # 全部就绪
                $script:State = 'Ready'
                $script:Tick = 0
                $main = $script:Forwards[0].Local
                $script:LabStatus.Text = '已加入协作。点击「打开协作页面」即可在浏览器中编辑房主的文档。'
                $script:LabStatus.ForeColor = [System.Drawing.Color]::ForestGreen
                $script:LabAddress.Text = 'http://localhost:' + $main
                if ($script:Languages.Count -gt 1) {
                    $parts = @($script:Languages | ForEach-Object { $_.Name + '：http://localhost:' + $_.LocalPort })
                    $script:LabLanguages.Text = '可用语言入口 — ' + ($parts -join '　|　')
                } else {
                    $script:LabLanguages.Text = '语言入口与上方地址相同（English）。'
                }
                $script:BtnJoin.Text = '断开'
                $script:BtnJoin.Enabled = $true
                $script:BtnOpen.Enabled = $true
                Write-Log '已加入协作，端口转发与语言探测完成'
            }
        }
        'Ready' {
            if ($script:CoreProcess.HasExited) {
                Set-Failed '网络组件意外退出，协作已中断'
                return
            }
            # 每 2 秒（4 tick）刷新一次成员/节点状态
            if (($script:Tick % 4) -ne 0) { return }
            $nodes = Get-ConnectedNodeCount
            $peers = Get-Peers
            $hostPeer = $null
            $memberCount = 0
            if ($peers) {
                foreach ($p in @($peers)) {
                    if (-not $p.ipv4) { continue }
                    if ("$($p.cost)" -eq 'Local') { continue }
                    $memberCount++
                    if ($p.hostname -and $p.hostname.StartsWith($script:HostPrefix)) { $hostPeer = $p }
                }
            }
            if ($hostPeer) {
                $script:HostMissCount = 0
                $link = '中继'
                if ("$($hostPeer.cost)" -eq 'p2p') { $link = '直连' }
                $lat = '—'
                $latRaw = "$($hostPeer.lat_ms)"
                $latVal = 0.0
                if ([double]::TryParse($latRaw, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$latVal)) {
                    $lat = '{0:0}ms' -f $latVal
                }
                $script:LabNetwork.Text = ('已连接 {0} 个公共节点 · 网络 {1} 位成员 · 房主 {2}（{3}）· {4} · {5}' -f `
                    $nodes, $memberCount, $hostPeer.hostname, $hostPeer.ipv4, $link, $lat)
            } else {
                $script:HostMissCount++
                $script:LabNetwork.Text = ('已连接 {0} 个公共节点 · 暂时未发现房主成员（重连中…）' -f $nodes)
                if ($script:HostMissCount -ge 5) {
                    Write-Log '连续 10 秒未发现房主成员，可能已掉线'
                }
            }
        }
        default { }
    }
}

# ============================================================
#  界面
# ============================================================

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$form = New-Object System.Windows.Forms.Form
$form.Text = 'Ayaka 协作轻量端 v' + $script:Version
$form.ClientSize = New-Object System.Drawing.Size(580, 432)
$form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedSingle
$form.MaximizeBox = $false
$form.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterScreen
$form.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 9.0)

# ---- 邀请码 ----
$grpJoin = New-Object System.Windows.Forms.GroupBox
$grpJoin.Text = '邀请码'
$grpJoin.Bounds = New-Object System.Drawing.Rectangle(12, 8, 556, 92)
[void]$form.Controls.Add($grpJoin)

$script:TxtCode = New-Object System.Windows.Forms.TextBox
$script:TxtCode.Bounds = New-Object System.Drawing.Rectangle(15, 28, 360, 28)
$script:TxtCode.Font = New-Object System.Drawing.Font('Consolas', 12.0)
[void]$grpJoin.Controls.Add($script:TxtCode)

$btnPaste = New-Object System.Windows.Forms.Button
$btnPaste.Text = '粘贴'
$btnPaste.Bounds = New-Object System.Drawing.Rectangle(385, 27, 60, 29)
[void]$btnPaste.Add_Click({
        try {
            $text = [System.Windows.Forms.Clipboard]::GetText()
            if ($text) { $script:TxtCode.Text = $text.Trim() }
        } catch { }
    })
[void]$grpJoin.Controls.Add($btnPaste)

$script:BtnJoin = New-Object System.Windows.Forms.Button
$script:BtnJoin.Text = '加入协作'
$script:BtnJoin.Bounds = New-Object System.Drawing.Rectangle(455, 27, 85, 29)
[void]$script:BtnJoin.Add_Click({ OnJoinClick })
[void]$grpJoin.Controls.Add($script:BtnJoin)

$labCodeHint = New-Object System.Windows.Forms.Label
$labCodeHint.Text = '输入房主分享的邀请码（A/XXXX-XXXX-XXXX-XXXX），仅凭邀请码即可加入，等同密钥请勿公开。'
$labCodeHint.Bounds = New-Object System.Drawing.Rectangle(15, 62, 526, 20)
$labCodeHint.ForeColor = [System.Drawing.Color]::Gray
[void]$grpJoin.Controls.Add($labCodeHint)

# ---- 状态 ----
$grpStatus = New-Object System.Windows.Forms.GroupBox
$grpStatus.Text = '状态'
$grpStatus.Bounds = New-Object System.Drawing.Rectangle(12, 106, 556, 152)
[void]$form.Controls.Add($grpStatus)

$script:LabStatus = New-Object System.Windows.Forms.Label
$script:LabStatus.Text = '未加入协作。请粘贴房主分享的邀请码后点击「加入协作」。'
$script:LabStatus.Bounds = New-Object System.Drawing.Rectangle(15, 24, 526, 40)
$script:LabStatus.TextAlign = [System.Drawing.ContentAlignment]::TopLeft
[void]$grpStatus.Controls.Add($script:LabStatus)

$script:LabNetwork = New-Object System.Windows.Forms.Label
$script:LabNetwork.Text = ''
$script:LabNetwork.Bounds = New-Object System.Drawing.Rectangle(15, 64, 526, 20)
$script:LabNetwork.ForeColor = [System.Drawing.Color]::DimGray
[void]$grpStatus.Controls.Add($script:LabNetwork)

$script:LabAddress = New-Object System.Windows.Forms.Label
$script:LabAddress.Text = ''
$script:LabAddress.Bounds = New-Object System.Drawing.Rectangle(15, 86, 526, 22)
$script:LabAddress.Font = New-Object System.Drawing.Font('Consolas', 11.0, [System.Drawing.FontStyle]::Bold)
$script:LabAddress.ForeColor = [System.Drawing.Color]::RoyalBlue
[void]$grpStatus.Controls.Add($script:LabAddress)

$script:LabLanguages = New-Object System.Windows.Forms.Label
$script:LabLanguages.Text = ''
$script:LabLanguages.Bounds = New-Object System.Drawing.Rectangle(15, 110, 526, 18)
$script:LabLanguages.ForeColor = [System.Drawing.Color]::Gray
[void]$grpStatus.Controls.Add($script:LabLanguages)

$script:BtnOpen = New-Object System.Windows.Forms.Button
$script:BtnOpen.Text = '打开协作页面'
$script:BtnOpen.Bounds = New-Object System.Drawing.Rectangle(15, 118, 110, 26)
$script:BtnOpen.Enabled = $false
[void]$script:BtnOpen.Add_Click({
        if ($script:Forwards.Count -eq 0) { return }
        $url = 'http://localhost:' + $script:Forwards[0].Local
        try {
            Start-Process $url
            Write-Log ('已在默认浏览器打开 ' + $url)
        } catch {
            Write-Log ('打开浏览器失败：' + $_.Exception.Message)
        }
    })
[void]$grpStatus.Controls.Add($script:BtnOpen)

# ---- 日志 ----
$grpLog = New-Object System.Windows.Forms.GroupBox
$grpLog.Text = '日志'
$grpLog.Bounds = New-Object System.Drawing.Rectangle(12, 264, 556, 158)
[void]$form.Controls.Add($grpLog)

$script:TxtLog = New-Object System.Windows.Forms.TextBox
$script:TxtLog.Multiline = $true
$script:TxtLog.ReadOnly = $true
$script:TxtLog.ScrollBars = [System.Windows.Forms.ScrollBars]::Vertical
$script:TxtLog.Bounds = New-Object System.Drawing.Rectangle(15, 22, 526, 126)
$script:TxtLog.Font = New-Object System.Drawing.Font('Consolas', 8.5)
$script:TxtLog.BackColor = [System.Drawing.SystemColors]::Window
[void]$grpLog.Controls.Add($script:TxtLog)

# ---- 加入/断开 ----
function OnJoinClick {
    if ($script:State -in @('WaitCore', 'FindHost', 'Forward', 'Probe', 'Ready')) {
        Reset-ToIdle
        return
    }
    # 校验组件完整性
    if (-not (Test-Path -LiteralPath $script:CoreExe)) {
        $script:LabStatus.Text = '未找到 ' + $script:CoreExe + '，请确认轻量端目录完整（easytier 子文件夹须与启动器同目录）。'
        $script:LabStatus.ForeColor = [System.Drawing.Color]::Firebrick
        return
    }
    # 解析邀请码
    $digits = Find-InviteCode $script:TxtCode.Text
    if (-not $digits) {
        $script:LabStatus.Text = '邀请码无效，请检查后重新输入（形如 A/XXXX-XXXX-XXXX-XXXX）。'
        $script:LabStatus.ForeColor = [System.Drawing.Color]::Firebrick
        return
    }
    $script:InviteDigits = $digits
    $formatted = '{0}/{1}-{2}-{3}-{4}' -f 'A', $digits.Substring(0, 4), $digits.Substring(4, 4), $digits.Substring(8, 4), $digits.Substring(12, 4)
    Write-Log ('邀请码校验通过：' + $formatted)

    # 清理上次运行可能残留的实例（异常退出遗留的 easytier-core）
    try {
        Get-Process -Name 'easytier-core' -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and $_.Path.StartsWith((Split-Path -Parent $script:CoreExe), [System.StringComparison]::OrdinalIgnoreCase) } |
            Stop-Process -Force -ErrorAction SilentlyContinue
    } catch { }

    # 写入配置并启动网络组件
    $workDir = Join-Path $env:TEMP 'ayaka-collab-lite'
    if (-not (Test-Path -LiteralPath $workDir)) { New-Item -ItemType Directory -Path $workDir -Force | Out-Null }
    $script:ConfigPath = Join-Path $workDir ([Guid]::NewGuid().ToString('N') + '.toml')
    [System.IO.File]::WriteAllText($script:ConfigPath, (New-GuestToml), (New-Object System.Text.UTF8Encoding($false)))
    $script:RpcPort = Get-FreePort

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $script:CoreExe
    $psi.Arguments = ('-c "{0}" -r 127.0.0.1:{1} --multi-thread' -f $script:ConfigPath, $script:RpcPort)
    $psi.WorkingDirectory = Split-Path -Parent $script:CoreExe
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    try {
        $script:CoreProcess = New-Object System.Diagnostics.Process
        $script:CoreProcess.StartInfo = $psi
        [void]$script:CoreProcess.Start()
    } catch {
        Set-Failed ('网络组件启动失败：' + $_.Exception.Message)
        return
    }
    $script:State = 'WaitCore'
    $script:Tick = 0
    $script:LabStatus.Text = '正在启动虚拟网络…'
    $script:LabStatus.ForeColor = [System.Drawing.SystemColors]::ControlText
    $script:LabNetwork.Text = ''
    $script:LabAddress.Text = ''
    $script:LabLanguages.Text = ''
    $script:BtnJoin.Text = '取消'
    $script:BtnOpen.Enabled = $false
    Write-Log ('已启动网络组件（RPC 127.0.0.1:' + $script:RpcPort + '）')
}

# 关窗时停止网络组件（否则 easytier-core 会成为孤儿进程继续运行）
$form.Add_FormClosing({
    try { Stop-Network -Quiet } catch { }
})

# ---- 主循环 ----
$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 500
$timer.Add_Tick({ Update-StateMachine })
$timer.Start()

Write-Log ('Ayaka 协作轻量端 v' + $script:Version + ' 已启动')
if (Test-Path -LiteralPath $script:CoreExe) {
    Write-Log ('网络组件就绪：' + $script:CoreExe)
} else {
    Write-Log ('警告：未找到网络组件 ' + $script:CoreExe + '，请确认轻量端目录完整')
}

[void]$form.ShowDialog()
$timer.Stop()
Stop-Network -Quiet

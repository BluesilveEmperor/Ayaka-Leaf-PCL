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
#  自动配对（受邀端免注册自动开户 + 登录）与主程序同一契约，逐行对齐位置：
#   - 显示名清洗 ConvertTo-SanitizedName
#     ← CollabGuestAccountStore.SanitizeDisplayName
#     （去空白 → 小写 → 仅保留 [a-z0-9._-] 其余替换 _ → 空回退 guest → 截断 32 字符）
#   - 协作邮箱 {清洗名}.{4 位十六进制实例标识}@collab.ayaka.local
#     ← CollabGuestAccountStore.BuildCollabEmail；
#     实例标识持久存解压目录 guest-id.txt（一次生成只读）
#     ← CollabGuestAccountStore.GetOrCreateGuestId
#   - 随机密码 New-CollabPassword（24 字符，排除易混字符同一字符集）
#     ← CollabGuestAccountStore.GenerateCollabPassword
#   - 凭据 Read/Write-CollabCredential（Email 明文 + DPAPI（CurrentUser）密文，
#     临时文件 + 原子替换，存解压目录 collab-accounts\{16 位邀请码}.json）
#     ← CollabGuestAccountStore.TryLoadCredential / SaveCredential；
#     同码既有凭据直接登录不开户，损坏凭据按无凭据重新开户
#   - 注册/登录 Invoke-CollabPost（/register /login 表单 _csrf/email/password、
#     CSRF 解析 ol-csrfToken、预热重试 5 次 × 2 秒、只认本次 Set-Cookie 的
#     overleaf.sid、「已存在」→冲突建议、「停用」→停用文案）
#     ← CollabAccountClient.RegisterAsync / LoginAsync
#   - Pair 失败不回滚转发（对齐主程序 D4：配对是转发就绪后的独立子状态，可重试）
#
#  运行要求：Windows 10 1607+ / 64 位，系统自带 PowerShell 5.1，无需任何安装。
#
#  状态机（UI 定时器驱动，每步只做一次快速阻塞调用，避免长阻塞卡死界面）：
#   Idle → WaitCore → FindHost → Forward → Probe → Pair → Ready / Error
# ============================================================================
#requires -Version 5.1

$ErrorActionPreference = 'Stop'

# DPAPI（ProtectedData，CurrentUser）—— 协作凭据加密，与主程序同一保护方式
Add-Type -AssemblyName System.Security

# ---- 常量 ----
$script:Version = '1.1.0'
$script:BaseDir = $PSScriptRoot
$script:CoreExe = Join-Path $script:BaseDir 'easytier\easytier-core.exe'
$script:CliExe = Join-Path $script:BaseDir 'easytier\easytier-cli.exe'

# 邀请码字母表（与主程序 CollabCode 一致：去除 0/1/I/O/L 防混淆）
$script:Alphabet = '23456789ABCDEFGHJKMNPQRSTUVWXYZ'
$script:HostPrefix = 'ayaka-host-'
$script:GuestPrefix = 'ayaka-guest-'

# 自动配对契约常量（与主程序 CollabGuestAccountStore 逐行对齐）
$script:CollabDomain = 'collab.ayaka.local'      # 协作账号邮箱域名（CollabEmailDomain）
$script:PasswordChars = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789' # GenerateCollabPassword 字符集
$script:GuestIdFile = Join-Path $script:BaseDir 'guest-id.txt'     # 实例标识（一次生成只读）
$script:CollabAccountsDir = Join-Path $script:BaseDir 'collab-accounts' # 凭据目录（按邀请码隔离）

# 内置公共节点（与主程序 CollabNodes.BuiltIn 一致）
$script:BuiltInNodes = @(
    'tcp://225284.xyz:11010'
    'tcp://easytier.weiai.org.cn:11010'
    'udp://et.basd1.de:11010'
    'wss://et.chinokou.cn'
    'https://et-public.lctn.top'
)

# ---- 运行状态（全部挂在 $script: 上，事件处理器跨作用域可见） ----
$script:State = 'Idle'            # Idle / WaitCore / FindHost / Forward / Probe / Pair / Ready / Error
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

# ---- 自动配对状态（Pair 阶段子状态机，对齐主程序 CollabSession.RunPairingAsync） ----
$script:PairStage = ''            # '' / Prepare / Csrf / Register / Login
$script:PairWaitTicks = 0         # CSRF 预热重试退避（500ms × 4 = 2 秒）
$script:CsrfAttempts = 0
$script:CsrfToken = ''
$script:CollabEmail = ''
$script:CollabPassword = ''       # 仅存在于内存与界面展示，绝不写日志/文件（FR-003）
$script:NeedRegister = $false
$script:PairError = ''
$script:WebSession = $null        # 配对专用 Web 会话（CSRF + 注册/登录共用，与浏览器无关）

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

# ============================================================
#  自动配对（契约逐行对齐 PCL.Core\Ayakaleaf\Collab\，对齐位置见文件头注释）
# ============================================================

# 显示名清洗 ← CollabGuestAccountStore.SanitizeDisplayName：
# 去首尾空白 → 转小写 → 仅保留 [a-z0-9._-] 其余替换 _ → 空回退 guest → 截断 32 字符（MaxDisplayNameLength）
function ConvertTo-SanitizedName {
    param([string]$DisplayName)
    $lower = ''
    if ($DisplayName) { $lower = $DisplayName.Trim().ToLowerInvariant() }
    $sb = New-Object System.Text.StringBuilder
    foreach ($c in $lower.ToCharArray()) {
        if ("$c" -cmatch '^[a-z0-9._-]$') { [void]$sb.Append($c) } else { [void]$sb.Append('_') }
    }
    $name = $sb.ToString()
    if ($name.Length -eq 0) { return 'guest' }
    if ($name.Length -gt 32) { return $name.Substring(0, 32) }
    return $name
}

# 实例标识 ← CollabGuestAccountStore.GetOrCreateGuestId：
# 4 位小写十六进制，一次生成持久只读（重复加入身份稳定的基石）
function Get-OrCreateGuestId {
    if (Test-Path -LiteralPath $script:GuestIdFile) {
        try {
            $value = ([System.IO.File]::ReadAllText($script:GuestIdFile)).Trim()
            if ($value -cmatch '^[0-9a-f]{4}$') { return $value }
        } catch { }
    }
    $guestId = Get-RandomHex 4
    $tempFile = $script:GuestIdFile + '.tmp'
    try {
        [System.IO.File]::WriteAllText($tempFile, $guestId, (New-Object System.Text.UTF8Encoding($false)))
        [System.IO.File]::Move($tempFile, $script:GuestIdFile)
    } catch {
        # 写入失败（如另一进程已并发生成）：重读文件保留先写进程的标识；临时文件尽力清理
        try { if (Test-Path -LiteralPath $tempFile) { Remove-Item -LiteralPath $tempFile -Force } } catch { }
        try {
            $value = ([System.IO.File]::ReadAllText($script:GuestIdFile)).Trim()
            if ($value -cmatch '^[0-9a-f]{4}$') { return $value }
        } catch { }
    }
    Write-Log ('已生成协作实例标识（guest-id），本实例协作账号后缀固定为 .' + $guestId)
    return $guestId
}

# 随机密码 ← CollabGuestAccountStore.GenerateCollabPassword：24 字符，排除易混字符同一字符集
function New-CollabPassword {
    $bytes = New-Object byte[] 24
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $sb = New-Object System.Text.StringBuilder 24
    foreach ($b in $bytes) { [void]$sb.Append($script:PasswordChars[$b % $script:PasswordChars.Length]) }
    return $sb.ToString()
}

# 凭据文件路径（按 16 位邀请码隔离 ← NormalizeInviteKey 语义；InviteDigits 已是 16 位数字串）
function Get-CredentialPath {
    return (Join-Path $script:CollabAccountsDir ($script:InviteDigits + '.json'))
}

# 读取既有协作凭据 ← CollabGuestAccountStore.TryLoadCredential：
# 不存在/文件损坏/JSON 非法/DPAPI 解密失败统一返回 $null，按「无凭据」走重新开户路径
function Read-CollabCredential {
    $path = Get-CredentialPath
    try {
        if (-not (Test-Path -LiteralPath $path)) { return $null }
        $saved = ([System.IO.File]::ReadAllText($path)) | ConvertFrom-Json
        $email = "$($saved.Email)"
        $encrypted = "$($saved.Password)"
        if (-not $email -or -not $encrypted) { return $null }
        $plain = [System.Text.Encoding]::UTF8.GetString([System.Security.Cryptography.ProtectedData]::Unprotect(
                [Convert]::FromBase64String($encrypted), $null,
                [System.Security.Cryptography.DataProtectionScope]::CurrentUser))
        if (-not $plain) { return $null }
        return @{ Email = $email; Password = $plain }
    } catch {
        return $null
    }
}

# 原子写入协作凭据 ← CollabGuestAccountStore.SaveCredential：
# Email 明文 + DPAPI（CurrentUser）Base64 密文（绝不明文落盘），临时文件 + 原子替换
function Write-CollabCredential {
    param([string]$Email, [string]$Password)
    $path = Get-CredentialPath
    if (-not (Test-Path -LiteralPath $script:CollabAccountsDir)) {
        New-Item -ItemType Directory -Path $script:CollabAccountsDir -Force | Out-Null
    }
    $encrypted = [Convert]::ToBase64String([System.Security.Cryptography.ProtectedData]::Protect(
            [System.Text.Encoding]::UTF8.GetBytes($Password), $null,
            [System.Security.Cryptography.DataProtectionScope]::CurrentUser))
    $json = ConvertTo-Json ([ordered]@{ Email = $Email; Password = $encrypted }) -Compress
    $tempFile = $path + '.tmp'
    try {
        [System.IO.File]::WriteAllText($tempFile, $json, (New-Object System.Text.UTF8Encoding($false)))
        Move-Item -LiteralPath $tempFile -Destination $path -Force
    } catch {
        try { if (Test-Path -LiteralPath $tempFile) { Remove-Item -LiteralPath $tempFile -Force } } catch { }
        throw
    }
}

# 表单编码（PS 5.1 的 IWR 不支持 Hashtable 直转表单，手工编码，语义同 FormUrlEncodedContent）
function ConvertTo-FormBody {
    param([hashtable]$Fields)
    $sb = New-Object System.Text.StringBuilder
    foreach ($key in @($Fields.Keys)) {
        if ($sb.Length -gt 0) { [void]$sb.Append('&') }
        [void]$sb.Append([System.Uri]::EscapeDataString("$key"))
        [void]$sb.Append('=')
        [void]$sb.Append([System.Uri]::EscapeDataString("$($Fields[$key])"))
    }
    return $sb.ToString()
}

# 汇总响应的全部 Set-Cookie 头（PS 5.1 / 7 均兼容，多段以逗号连接）
function Get-SetCookieHeader {
    param($Response)
    if (-not $Response -or -not $Response.Headers) { return '' }
    $header = $null
    try { $header = $Response.Headers['Set-Cookie'] } catch { }
    if (-not $header) { return '' }
    return ((@($header)) -join ', ')
}

# 判断本次响应是否下发非空 overleaf.sid
# ← CollabAccountClient._ResponseSetsSessionCookie + _ReadCookie（只认本次响应，禁止复用陈旧会话）
function Test-ResponseSetsSid {
    param([string]$SetCookieHeader)
    if (-not $SetCookieHeader) { return $false }
    foreach ($segment in ($SetCookieHeader -split ',')) {
        $trim = $segment.TrimStart()
        if ($trim.StartsWith('overleaf.sid=', [System.StringComparison]::OrdinalIgnoreCase)) {
            $value = $trim.Substring('overleaf.sid='.Length)
            $semi = $value.IndexOf(';')
            if ($semi -ge 0) { $value = $value.Substring(0, $semi) }
            if ($value.Trim().Length -gt 0) { return $true }
        }
    }
    return $false
}

# 注册响应体是否表明「已存在」← CollabAccountClient._ContainsAlreadyRegistered
function Test-AlreadyRegistered {
    param([string]$Body)
    if (-not $Body) { return $false }
    return ($Body -match 'already|已被使用|已被注册|已经存在')
}

# 登录失败响应体是否表明「已被停用」← CollabAccountClient._ContainsSuspended
function Test-Suspended {
    param([string]$Body)
    if (-not $Body) { return $false }
    return ($Body -match 'suspend|停用|禁用')
}

# GET 房主页面（首次建立 WebSession；localhost 默认绕过系统代理，对齐 UseProxy = false）
function Invoke-CollabGet {
    param([string]$Path)
    $url = 'http://localhost:' + $script:Forwards[0].Local + $Path
    try {
        if ($script:WebSession) {
            $resp = Invoke-WebRequest -Uri $url -WebSession $script:WebSession -TimeoutSec 10 -UseBasicParsing -ErrorAction Stop
        } else {
            $resp = Invoke-WebRequest -Uri $url -SessionVariable webSession -TimeoutSec 10 -UseBasicParsing -ErrorAction Stop
            $script:WebSession = $webSession
        }
        return @{ Ok = $true; Code = [int]$resp.StatusCode; Body = "$($resp.Content)"; SetCookie = ''; Error = '' }
    } catch {
        $code = 0
        try {
            if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode }
        } catch { }
        return @{ Ok = $false; Code = $code; Body = ''; SetCookie = ''; Error = $_.Exception.Message }
    }
}

# POST 表单（/register /login）← CollabAccountClient.RegisterAsync / LoginAsync：
# 表单 _csrf/email/password、30 秒超时；4xx/5xx 不抛出，返回状态码与响应体
function Invoke-CollabPost {
    param([string]$Path, [hashtable]$Fields)
    if (-not $script:WebSession) { return @{ Ok = $false; Code = 0; Body = ''; SetCookie = ''; Error = '会话未建立' } }
    $url = 'http://localhost:' + $script:Forwards[0].Local + $Path
    try {
        $resp = Invoke-WebRequest -Uri $url -Method Post `
            -Body (ConvertTo-FormBody $Fields) -ContentType 'application/x-www-form-urlencoded' `
            -WebSession $script:WebSession -TimeoutSec 30 -UseBasicParsing -ErrorAction Stop
        return @{ Ok = $true; Code = [int]$resp.StatusCode; Body = "$($resp.Content)"; SetCookie = (Get-SetCookieHeader $resp); Error = '' }
    } catch {
        # 4xx/5xx：IWR 抛异常，从 Response 取状态码与响应体（PS 5.1 / 7 均兼容）
        $code = 0
        $body = ''
        try {
            $response = $_.Exception.Response
            if ($response) {
                $code = [int]$response.StatusCode
                if ($response -is [System.Net.HttpWebResponse]) {
                    # PS 5.1：WebException.Response
                    $stream = $response.GetResponseStream()
                    if ($stream) {
                        $reader = New-Object System.IO.StreamReader($stream)
                        $body = $reader.ReadToEnd()
                    }
                } else {
                    # PS 7：HttpResponseException.Response
                    $task = $response.Content.ReadAsStringAsync()
                    if ($task) { $body = "$($task.GetAwaiter().GetResult())" }
                }
            }
        } catch { }
        return @{ Ok = $false; Code = $code; Body = $body; SetCookie = ''; Error = $_.Exception.Message }
    }
}

# 配对失败（不回滚转发，对齐主程序 D4）：展示中文原因与重试按钮；
# 已注册成功的凭据仍展示在下方供手动登录（密码仅显示给本人，不落日志）
function Set-PairFailed {
    param([string]$Reason)
    $script:State = 'Ready'
    $script:PairStage = ''
    $script:PairError = $Reason
    $script:LabPairInfo.Text = '协作账号自动配对失败：' + $Reason
    $script:LabPairInfo.ForeColor = [System.Drawing.Color]::Firebrick
    $script:LabPairInfo.Visible = $true
    $script:BtnRetryPair.Visible = $true
    $script:BtnRetryPair.Enabled = $true
    if ($script:CollabEmail -and $script:CollabPassword) {
        $script:LabPairEmail.Text = $script:CollabEmail
        $script:LabPairPassword.Text = $script:CollabPassword
    }
    $script:LabStatus.Text = '已加入协作，但协作账号配对未完成。可点击「重试配对」；若下方已展示账号，也可打开页面手动登录。'
    $script:LabStatus.ForeColor = [System.Drawing.Color]::Firebrick
    Write-Log ('协作账号配对失败：' + $Reason)
}

# 配对成功：展示协作账号凭据与复制按钮，提示浏览器登录页各粘贴一次、此后保持登录
function Complete-Pair {
    $script:State = 'Ready'
    $script:PairStage = ''
    $script:PairError = ''
    $script:LabPairEmail.Text = $script:CollabEmail
    $script:LabPairPassword.Text = $script:CollabPassword
    $script:LabPairInfo.Text = '协作账号已就绪。首次使用请点击「打开协作页面」，在登录页分别粘贴下方邮箱与密码（各粘贴一次），此后浏览器会保持登录。'
    $script:LabPairInfo.ForeColor = [System.Drawing.SystemColors]::ControlText
    $script:LabPairInfo.Visible = $true
    $script:BtnRetryPair.Visible = $false
    $script:BtnRetryPair.Enabled = $false
    $script:LabStatus.Text = '已加入协作，协作账号已自动配对。点击「打开协作页面」即可在浏览器中编辑房主的文档。'
    $script:LabStatus.ForeColor = [System.Drawing.Color]::ForestGreen
    Write-Log ('协作账号配对完成：' + $script:CollabEmail)
}

# 复位配对状态与界面（断开/重开/出错时调用；不动 State，由调用方管理）
function Reset-PairState {
    $script:PairStage = ''
    $script:PairWaitTicks = 0
    $script:CsrfAttempts = 0
    $script:CsrfToken = ''
    $script:CollabEmail = ''
    $script:CollabPassword = ''
    $script:NeedRegister = $false
    $script:PairError = ''
    $script:WebSession = $null
    if ($script:LabPairInfo) {
        $script:LabPairInfo.Text = ''
        $script:LabPairInfo.Visible = $false
    }
    if ($script:BtnRetryPair) {
        $script:BtnRetryPair.Visible = $false
        $script:BtnRetryPair.Enabled = $false
    }
    if ($script:LabPairEmail) { $script:LabPairEmail.Text = '' }
    if ($script:LabPairPassword) { $script:LabPairPassword.Text = '' }
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
    Reset-PairState   # 同步清理配对状态与界面（若配对进行中）
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
    Reset-PairState
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
                # 转发与探测全部就绪：进入自动配对阶段（配对失败不回滚转发，对齐主程序 D4）
                $script:State = 'Pair'
                $script:Tick = 0
                $script:PairStage = 'Prepare'
                $script:PairWaitTicks = 0
                $script:CsrfAttempts = 0
                $script:PairError = ''
                $main = $script:Forwards[0].Local
                $script:LabStatus.Text = '已加入协作，正在自动创建协作账号…'
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
                Write-Log '端口转发与语言探测完成，开始自动配对协作账号'
            }
        }
        'Pair' {
            # 自动配对子状态机（对齐主程序 CollabSession.RunPairingAsync；失败不回滚转发）
            if ($script:CoreProcess.HasExited) {
                Set-Failed '网络组件意外退出，协作已中断'
                return
            }
            if ($script:PairWaitTicks -gt 0) {
                $script:PairWaitTicks--   # CSRF 预热重试退避（500ms × 4 = 2 秒）
                return
            }
            switch ($script:PairStage) {
                'Prepare' {
                    # 查既有凭据：同码复用直接登录不开户；损坏/解密失败按无凭据重新开户
                    $cred = Read-CollabCredential
                    if ($cred) {
                        $script:CollabEmail = $cred.Email
                        $script:CollabPassword = $cred.Password
                        $script:NeedRegister = $false
                        Write-Log ('发现本邀请码的既有协作凭据，直接登录不开户：' + $script:CollabEmail)
                    } else {
                        # 无凭据：清洗显示名 + 实例标识组成协作邮箱，随机密码开户（显示名留空回退 guest）
                        $script:CollabEmail = (ConvertTo-SanitizedName $script:TxtName.Text) + '.' + (Get-OrCreateGuestId) + '@' + $script:CollabDomain
                        $script:CollabPassword = New-CollabPassword
                        $script:NeedRegister = $true
                    }
                    $script:CsrfAttempts = 0
                    $script:PairStage = 'Csrf'
                }
                'Csrf' {
                    # GET /login 解析 CSRF（← CollabAccountClient._GetCsrfTokenAsync：预热重试 5 次 × 2 秒）
                    $r = Invoke-CollabGet '/login'
                    $token = $null
                    if ($r.Ok) {
                        $m = [regex]::Match($r.Body, 'ol-csrfToken" content="([^"]+)"')
                        if ($m.Success) { $token = $m.Groups[1].Value }
                    }
                    if ($token) {
                        $script:CsrfToken = $token
                        if ($script:NeedRegister) { $script:PairStage = 'Register' } else { $script:PairStage = 'Login' }
                        return
                    }
                    if ($r.Code -eq 0) {
                        Set-PairFailed ('无法连接房主服务（http://localhost:' + $script:Forwards[0].Local + '）：协作转发可能已中断。请确认房主仍在线、网络正常后重试。')
                        return
                    }
                    $script:CsrfAttempts++
                    if ($script:CsrfAttempts -ge 5) {
                        Set-PairFailed ('房主服务已响应但尚未就绪：未能获取登录页的 CSRF 令牌（HTTP ' + $r.Code + '）。服务可能正在预热，请稍候点击「重试配对」。')
                        return
                    }
                    Write-Log ('房主登录页尚未就绪（第 ' + $script:CsrfAttempts + '/5 次探测失败），2 秒后重试')
                    $script:PairWaitTicks = 4
                }
                'Register' {
                    $r = Invoke-CollabPost '/register' @{ _csrf = $script:CsrfToken; email = $script:CollabEmail; password = $script:CollabPassword }
                    if ($r.Code -eq 0) {
                        Set-PairFailed '无法连接房主服务：协作转发可能已中断。请确认房主仍在线、网络正常后重试。'
                        return
                    }
                    if ($r.Code -eq 200) {
                        # 注册成功：立即保存凭据（对齐主程序——此后重试只登录不开户）
                        try {
                            Write-CollabCredential -Email $script:CollabEmail -Password $script:CollabPassword
                        } catch {
                            Set-PairFailed ('保存协作账号凭据失败：' + $_.Exception.Message + '。请检查目录读写权限后点击「重试配对」。')
                            return
                        }
                        Write-Log ('协作账号注册成功：' + $script:CollabEmail)
                        $script:NeedRegister = $false
                        $script:CsrfAttempts = 0
                        $script:PairStage = 'Csrf'   # 登录前重新获取 CSRF（对齐 LoginAsync 独立取令牌）
                        return
                    }
                    if (($r.Code -eq 400 -or $r.Code -eq 409 -or $r.Code -eq 422) -and (Test-AlreadyRegistered $r.Body)) {
                        Set-PairFailed ('协作账号 ' + $script:CollabEmail + ' 已在房主服务端存在，但本机没有它的登录凭据，无法自动登录。可任选其一：①联系房主在「账号中心」重置该账号密码；②修改显示名重新开户。')
                        return
                    }
                    if ($r.Code -eq 400 -or $r.Code -eq 409 -or $r.Code -eq 422) {
                        Set-PairFailed ('注册协作账号 ' + $script:CollabEmail + ' 失败（HTTP ' + $r.Code + '）。')
                        return
                    }
                    Set-PairFailed ('房主服务尚未就绪（HTTP ' + $r.Code + '），请稍候点击「重试配对」。')
                }
                'Login' {
                    $r = Invoke-CollabPost '/login' @{ _csrf = $script:CsrfToken; email = $script:CollabEmail; password = $script:CollabPassword }
                    if ($r.Code -eq 0) {
                        Set-PairFailed '无法连接房主服务：协作转发可能已中断。请确认房主仍在线、网络正常后重试。'
                        return
                    }
                    # 成功判定只认本次响应下发的非空 overleaf.sid（禁止复用陈旧会话）
                    if ($r.Code -eq 200 -and (Test-ResponseSetsSid $r.SetCookie)) {
                        Complete-Pair
                        return
                    }
                    if (Test-Suspended $r.Body) {
                        Set-PairFailed ('协作账号 ' + $script:CollabEmail + ' 登录失败（HTTP ' + $r.Code + '）：该账号已被房主停用。请联系房主在「账号中心」启用该账号后重试。')
                        return
                    }
                    Set-PairFailed ('协作账号 ' + $script:CollabEmail + ' 登录失败（HTTP ' + $r.Code + '）：本机保存的密码与服务端不一致（可能已被房主重置），或该账号已被删除。请联系房主在「账号中心」重置密码后重试。')
                }
                default { }
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
$form.ClientSize = New-Object System.Drawing.Size(580, 500)
$form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedSingle
$form.MaximizeBox = $false
$form.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterScreen
$form.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 9.0)

# ---- 加入协作（邀请码 + 显示名） ----
$grpJoin = New-Object System.Windows.Forms.GroupBox
$grpJoin.Text = '加入协作'
$grpJoin.Bounds = New-Object System.Drawing.Rectangle(12, 8, 556, 92)
[void]$form.Controls.Add($grpJoin)

$labCodeTag = New-Object System.Windows.Forms.Label
$labCodeTag.Text = '邀请码'
$labCodeTag.Bounds = New-Object System.Drawing.Rectangle(17, 12, 190, 14)
$labCodeTag.ForeColor = [System.Drawing.Color]::Gray
[void]$grpJoin.Controls.Add($labCodeTag)

$script:TxtCode = New-Object System.Windows.Forms.TextBox
$script:TxtCode.Bounds = New-Object System.Drawing.Rectangle(15, 28, 185, 24)
$script:TxtCode.Font = New-Object System.Drawing.Font('Consolas', 10.0)
[void]$grpJoin.Controls.Add($script:TxtCode)

$btnPaste = New-Object System.Windows.Forms.Button
$btnPaste.Text = '粘贴'
$btnPaste.Bounds = New-Object System.Drawing.Rectangle(210, 27, 54, 25)
[void]$btnPaste.Add_Click({
        try {
            $text = [System.Windows.Forms.Clipboard]::GetText()
            if ($text) { $script:TxtCode.Text = $text.Trim() }
        } catch { }
    })
[void]$grpJoin.Controls.Add($btnPaste)

$labNameTag = New-Object System.Windows.Forms.Label
$labNameTag.Text = '显示名（自动创建协作账号，可留空）'
$labNameTag.Bounds = New-Object System.Drawing.Rectangle(277, 12, 250, 14)
$labNameTag.ForeColor = [System.Drawing.Color]::Gray
[void]$grpJoin.Controls.Add($labNameTag)

$script:TxtName = New-Object System.Windows.Forms.TextBox
$script:TxtName.Bounds = New-Object System.Drawing.Rectangle(275, 28, 160, 24)
[void]$grpJoin.Controls.Add($script:TxtName)

$script:BtnJoin = New-Object System.Windows.Forms.Button
$script:BtnJoin.Text = '加入协作'
$script:BtnJoin.Bounds = New-Object System.Drawing.Rectangle(445, 27, 85, 25)
[void]$script:BtnJoin.Add_Click({ OnJoinClick })
[void]$grpJoin.Controls.Add($script:BtnJoin)

$labCodeHint = New-Object System.Windows.Forms.Label
$labCodeHint.Text = '仅凭邀请码即可加入，等同密钥请勿公开；显示名用于自动创建协作账号（留空默认 guest）。'
$labCodeHint.Bounds = New-Object System.Drawing.Rectangle(15, 62, 526, 20)
$labCodeHint.ForeColor = [System.Drawing.Color]::Gray
[void]$grpJoin.Controls.Add($labCodeHint)

# ---- 状态 ----
$grpStatus = New-Object System.Windows.Forms.GroupBox
$grpStatus.Text = '状态'
$grpStatus.Bounds = New-Object System.Drawing.Rectangle(12, 106, 556, 160)
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
$script:BtnOpen.Bounds = New-Object System.Drawing.Rectangle(15, 132, 110, 26)
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

# ---- 协作账号（自动配对结果展示：邮箱/密码 + 复制 + 重试配对） ----
$grpPair = New-Object System.Windows.Forms.GroupBox
$grpPair.Text = '协作账号'
$grpPair.Bounds = New-Object System.Drawing.Rectangle(12, 272, 556, 96)
[void]$form.Controls.Add($grpPair)

$script:LabPairInfo = New-Object System.Windows.Forms.Label
$script:LabPairInfo.Text = ''
$script:LabPairInfo.Bounds = New-Object System.Drawing.Rectangle(15, 22, 526, 18)
$script:LabPairInfo.ForeColor = [System.Drawing.SystemColors]::ControlText
$script:LabPairInfo.Visible = $false
[void]$grpPair.Controls.Add($script:LabPairInfo)

$labEmailTag = New-Object System.Windows.Forms.Label
$labEmailTag.Text = '邮箱：'
$labEmailTag.Bounds = New-Object System.Drawing.Rectangle(15, 49, 45, 18)
[void]$grpPair.Controls.Add($labEmailTag)

$script:LabPairEmail = New-Object System.Windows.Forms.Label
$script:LabPairEmail.Text = ''
$script:LabPairEmail.Bounds = New-Object System.Drawing.Rectangle(60, 49, 316, 18)
$script:LabPairEmail.Font = New-Object System.Drawing.Font('Consolas', 9.5, [System.Drawing.FontStyle]::Bold)
[void]$grpPair.Controls.Add($script:LabPairEmail)

$btnCopyEmail = New-Object System.Windows.Forms.Button
$btnCopyEmail.Text = '复制'
$btnCopyEmail.Bounds = New-Object System.Drawing.Rectangle(385, 46, 74, 25)
[void]$btnCopyEmail.Add_Click({
        try {
            $text = $script:LabPairEmail.Text
            if ($text) {
                Set-Clipboard -Value $text
                Write-Log '已复制协作账号邮箱'
            }
        } catch { Write-Log ('复制邮箱失败：' + $_.Exception.Message) }
    })
[void]$grpPair.Controls.Add($btnCopyEmail)

$labPwdTag = New-Object System.Windows.Forms.Label
$labPwdTag.Text = '密码：'
$labPwdTag.Bounds = New-Object System.Drawing.Rectangle(15, 75, 45, 18)
[void]$grpPair.Controls.Add($labPwdTag)

$script:LabPairPassword = New-Object System.Windows.Forms.Label
$script:LabPairPassword.Text = ''
$script:LabPairPassword.Bounds = New-Object System.Drawing.Rectangle(60, 75, 316, 18)
$script:LabPairPassword.Font = New-Object System.Drawing.Font('Consolas', 9.5, [System.Drawing.FontStyle]::Bold)
[void]$grpPair.Controls.Add($script:LabPairPassword)

$btnCopyPassword = New-Object System.Windows.Forms.Button
$btnCopyPassword.Text = '复制'
$btnCopyPassword.Bounds = New-Object System.Drawing.Rectangle(385, 72, 74, 25)
[void]$btnCopyPassword.Add_Click({
        try {
            $text = $script:LabPairPassword.Text
            if ($text) {
                Set-Clipboard -Value $text
                Write-Log '已复制协作账号密码'
            }
        } catch { Write-Log ('复制密码失败：' + $_.Exception.Message) }
    })
[void]$grpPair.Controls.Add($btnCopyPassword)

$script:BtnRetryPair = New-Object System.Windows.Forms.Button
$script:BtnRetryPair.Text = '重试配对'
$script:BtnRetryPair.Bounds = New-Object System.Drawing.Rectangle(466, 59, 74, 25)
$script:BtnRetryPair.Visible = $false
[void]$script:BtnRetryPair.Add_Click({
        if ($script:State -ne 'Ready') { return }
        # 重试重新走配对子状态机：既有凭据则直接登录；无凭据则按当前显示名重新开户（改显示名即换邮箱）
        $script:State = 'Pair'
        $script:PairStage = 'Prepare'
        $script:PairWaitTicks = 0
        $script:CsrfAttempts = 0
        $script:PairError = ''
        $script:LabPairInfo.Visible = $false
        $script:BtnRetryPair.Visible = $false
        $script:LabStatus.Text = '正在重试协作账号配对…'
        $script:LabStatus.ForeColor = [System.Drawing.SystemColors]::ControlText
        Write-Log '重试协作账号配对'
    })
[void]$grpPair.Controls.Add($script:BtnRetryPair)

# ---- 日志 ----
$grpLog = New-Object System.Windows.Forms.GroupBox
$grpLog.Text = '日志'
$grpLog.Bounds = New-Object System.Drawing.Rectangle(12, 374, 556, 116)
[void]$form.Controls.Add($grpLog)

$script:TxtLog = New-Object System.Windows.Forms.TextBox
$script:TxtLog.Multiline = $true
$script:TxtLog.ReadOnly = $true
$script:TxtLog.ScrollBars = [System.Windows.Forms.ScrollBars]::Vertical
$script:TxtLog.Bounds = New-Object System.Drawing.Rectangle(15, 22, 526, 88)
$script:TxtLog.Font = New-Object System.Drawing.Font('Consolas', 8.5)
$script:TxtLog.BackColor = [System.Drawing.SystemColors]::Window
[void]$grpLog.Controls.Add($script:TxtLog)

# ---- 加入/断开 ----
function OnJoinClick {
    if ($script:State -in @('WaitCore', 'FindHost', 'Forward', 'Probe', 'Pair', 'Ready')) {
        Reset-ToIdle
        return
    }
    Reset-PairState   # 清理上次配对状态与界面
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

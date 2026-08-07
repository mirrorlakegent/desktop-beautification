<#
.SYNOPSIS
    guest 侧一站式状态采集器 —— 把判定一条用例所需的全部事实固化成一份 JSON。

.DESCRIPTION
    采集内容：
      * 桌面图标断言（内部调用 Assert-DesktopIcons.ps1，真值源）
      * %LocalAppData%\DesktopSuite\settings.json 的原文与关键字段
        （DesiredIconsHidden / RestoreIconsOnExit / LaunchOnStartup / ActiveSceneName …）
      * scenes.json 是否存在
      * HKCU\...\Run\DesktopSuite 自启项（含是否带 --background）
      * 当前壁纸路径（注册表 Control Panel\Desktop\Wallpaper）
      * DesktopSuite 进程存活情况，并区分主进程与 --wallpaper-host 渲染子进程
      * wallpaper.log 尾部 + 整份日志副本（跨重启用例必须留档，日志会滚动截断）
      * 系统层锚点：LastBootUpTime、当前会话、自动登录配置、事件 1074（关机/注销发起者）

    所有产物落到 -EvidenceDir（默认 C:\gstack\evidence），文件名带 Label 与时间戳，
    供宿主机 copyFileFromGuestToHost 回收。

.PARAMETER Label
    本次采集的标签，例如 'V11A-after-login'。会出现在文件名与 JSON 里。

.PARAMETER EvidenceDir
    证据输出目录。

.PARAMETER AppDir
    被测应用所在目录（默认 C:\gstack\app），用于校验 WallpaperLibrary 是否随发布包落地（V8）。

.PARAMETER LogTailLines
    日志尾部截取行数（默认 200）。

.PARAMETER SkipIconAssert
    跳过图标断言（极少用；仅当明确知道当前无交互桌面且不想产生 blocked 噪声时）。

.OUTPUTS
    退出码：0 = 采集完成（不代表用例 PASS）；4 = 采集器自身异常。
    注意：本脚本的退出码**不表达用例结论**，结论一律由宿主机读 JSON 得出。
#>
[CmdletBinding()]
param(
    [string] $Label        = 'adhoc',
    [string] $EvidenceDir  = 'C:\gstack\evidence',
    [string] $AppDir       = 'C:\gstack\app',
    [int]    $LogTailLines = 200,
    [switch] $SkipIconAssert
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'   # 采集器要尽最大努力收集，单项失败不能中断整体

$stamp     = (Get-Date).ToString('yyyyMMdd-HHmmss-fff')
$safeLabel = ($Label -replace '[^\w\-\.]', '_')
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not (Test-Path -LiteralPath $EvidenceDir)) {
    New-Item -ItemType Directory -Path $EvidenceDir -Force | Out-Null
}

$appDataDir  = Join-Path $env:LOCALAPPDATA 'DesktopSuite'
$settingsPath = Join-Path $appDataDir 'settings.json'
$scenesPath   = Join-Path $appDataDir 'scenes.json'
$logPath      = Join-Path $appDataDir 'logs\wallpaper.log'

# 计时追踪：每次采集写一份 trace，便于事后定位「到底哪一步慢/卡」。
$tracePath = Join-Path $EvidenceDir "collect-trace-$safeLabel-$stamp.log"
function Trace-Step {
    param([string] $Mark)
    try { "$(Get-Date -Format 'HH:mm:ss.fff')  $Mark" | Out-File -Append -FilePath $tracePath -Encoding UTF8 } catch {}
}
Trace-Step "start"

# 统一的「尽力而为」包装：采集失败返回调用方给定的 $Default（绝不返回字符串，
# 否则失败会被误判成真实数据 —— 例如把 $dsProcs 变成字符串会导致 mainAlive 误判）。
function Try-Get {
    param([scriptblock] $Block, $Default = $null)
    try { & $Block } catch { return $Default }
}

# 单条超时兜底：把查询放进独立 runspace，超时（默认 20s）即返回 $Default。
# 目的——任何一条慢/卡的 WMI 或事件日志查询都不能再拖垮整次采集，
# 让采集器永远在宿主机的 240s vmrun 上限内产出 state.json。
function Invoke-WithTimeout {
    param(
        [scriptblock] $ScriptBlock,
        [int]    $TimeoutMs = 20000,
        $Default = $null
    )
    $rs = $null; $ps = $null; $abandoned = $false
    try {
        $rs = [runspacefactory]::CreateRunspace()
        $rs.Open()
        $ps = [powershell]::Create()
        $ps.Runspace = $rs
        [void]$ps.AddScript($ScriptBlock.ToString())
        $handle = $ps.BeginInvoke()
        if ($handle.AsyncWaitHandle.WaitOne($TimeoutMs)) {
            try {
                # 关键：EndInvoke 返回的是 PSDataCollection[PSObject]。这个类型带
                # blocking enumerator 语义，一旦被 ConvertTo-Json 之类的下游枚举，
                # 有几率永远等下去（这正是整份采集器卡死的形态之一）。
                # 这里立刻物化成普通 Object[] / 标量，绝不把活的 PSDataCollection 外泄。
                $raw = $ps.EndInvoke($handle)
                $mat = @()
                foreach ($item in $raw) {
                    if ($item -is [System.Management.Automation.PSObject]) { $mat += $item.BaseObject }
                    else { $mat += $item }
                }
                if ($mat.Count -eq 0) { return $Default }
                if ($mat.Count -eq 1) { return $mat[0] }
                return $mat
            }
            catch { return $Default }
        }
        # 超时：**故意不调用 $ps.Stop() / Dispose()**。
        # 血泪教训：PowerShell.Stop() 会一直等到流水线真的停下来，
        # 被卡在非托管代码里的命令根本停不下来 —— 于是「超时兜底」自己也挂死，
        # 整个超时机制形同虚设。这里直接弃用该 runspace（进程马上就退出，泄漏无所谓）。
        $script:LeakedRunspaces++
        Trace-Step "TIMEOUT(<=$TimeoutMs ms) on guarded call -> abandon runspace"
        $abandoned = $true
        return $Default
    }
    catch { return $Default }
    finally {
        if (-not $abandoned) {
            if ($ps) { try { $ps.Dispose() } catch {} }
            if ($rs) { try { $rs.Dispose() } catch {} }
        }
    }
}
$script:LeakedRunspaces = 0

# ---------------------------------------------------------------- 图标断言
$iconResult = $null
if (-not $SkipIconAssert) {
    $assertScript = Join-Path $scriptDir 'Assert-DesktopIcons.ps1'
    $iconJsonPath = Join-Path $EvidenceDir "icons-$safeLabel-$stamp.json"
    if (Test-Path -LiteralPath $assertScript) {
        Trace-Step "icon-assert:begin"
        try {
            # 用 Start-Process 起独立 powershell，超时强杀，避免图标断言本身卡死拖垮整轮。
            $assertProc = Start-Process -FilePath 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' `
                -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',$assertScript,'-OutFile',$iconJsonPath,'-Label',$Label,'-Quiet') `
                -PassThru -WindowStyle Hidden -ErrorAction Stop
            if ($assertProc.WaitForExit(30000)) {
                $assertExit = $assertProc.ExitCode
            }
            else {
                try { $assertProc.Kill() } catch {}
                Trace-Step "icon-assert:TIMEOUT(30s)"
                $iconResult = [ordered]@{ verdict = 'error'; reasonCode = 'assert-timeout'; exitCode = $null }
            }
        }
        catch {
            $iconResult = [ordered]@{ verdict = 'error'; reasonCode = 'assert-invoke-failed'; reasonText = "$($_.Exception.Message)" }
        }
        if ($null -eq $iconResult) {
            if (Test-Path -LiteralPath $iconJsonPath) {
                try {
                    $iconResult = Get-Content -LiteralPath $iconJsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
                }
                catch {
                    $iconResult = [ordered]@{ verdict = 'error'; reasonCode = 'assert-parse-failed'; reasonText = "$($_.Exception.Message)" }
                }
            }
            else {
                $iconResult = [ordered]@{ verdict = 'error'; reasonCode = 'assert-no-output'; exitCode = $(if (Test-Path variable:assertExit) { $assertExit } else { $null }) }
            }
        }
        Trace-Step "icon-assert:end"
    }
    else {
        $iconResult = [ordered]@{ verdict = 'error'; reasonCode = 'assert-script-missing'; reasonText = $assertScript }
    }
}

# ---------------------------------------------------------------- settings.json
Trace-Step "settings:begin"
$settingsRaw    = $null
$settingsParsed = $null
$settingsError  = $null
if (Test-Path -LiteralPath $settingsPath) {
    try {
        $settingsRaw    = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8
        $settingsParsed = $settingsRaw | ConvertFrom-Json
        # 归档一份原文快照 —— V2/V3/V9 都要求「操作前/操作后」逐字对照
        Copy-Item -LiteralPath $settingsPath -Destination (Join-Path $EvidenceDir "settings-$safeLabel-$stamp.json") -Force
    }
    catch {
        # settings.json 损坏本身就是 V14 的一条判据，必须如实记录而不是静默
        $settingsError = "$($_.Exception.GetType().Name): $($_.Exception.Message)"
    }
}
Trace-Step "settings:end"

function Get-SettingField {
    param([string] $Name)
    if ($null -eq $settingsParsed) { return $null }
    if ($settingsParsed.PSObject.Properties.Name -contains $Name) { return $settingsParsed.$Name }
    return $null
}

# ---------------------------------------------------------------- 进程
# 关键：主进程与渲染子进程都叫 DesktopSuite.exe，只能靠命令行里的 --wallpaper-host 区分。
# 把两者混为一谈会让 V14「主进程被强杀后渲染进程仍存活」这条判据彻底失效。
Trace-Step "process:begin"
$dsProcs = Try-Get {
    @(Get-CimInstance Win32_Process -Filter "Name='DesktopSuite.exe'" -ErrorAction Stop | ForEach-Object {
        [ordered]@{
            pid           = $_.ProcessId
            commandLine   = $_.CommandLine
            isRenderer    = [bool]($_.CommandLine -and $_.CommandLine -match '--wallpaper-host')
            isBackground  = [bool]($_.CommandLine -and $_.CommandLine -match '--background')
            createdUtc    = $(try { $_.CreationDate.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ') } catch { $null })
            sessionId     = $_.SessionId
        }
    })
} @()
if ($dsProcs -isnot [array]) { $dsProcs = @($dsProcs) }

$mainProcs     = @($dsProcs | Where-Object { $_ -is [System.Collections.IDictionary] -and -not $_.isRenderer })
$rendererProcs = @($dsProcs | Where-Object { $_ -is [System.Collections.IDictionary] -and $_.isRenderer })
Trace-Step "process:end"

# ---------------------------------------------------------------- 自启注册表
$runValue = Try-Get {
    (Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' `
                      -Name 'DesktopSuite' -ErrorAction Stop).DesktopSuite
}
if ($runValue -is [string] -and $runValue.StartsWith('(error')) { $runValue = $null }

# ---------------------------------------------------------------- 自动登录（无人值守重启的前提）
$winlogonKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
$autoLogon = [ordered]@{
    autoAdminLogon   = Try-Get { (Get-ItemProperty -Path $winlogonKey -Name 'AutoAdminLogon'   -ErrorAction Stop).AutoAdminLogon }
    defaultUserName  = Try-Get { (Get-ItemProperty -Path $winlogonKey -Name 'DefaultUserName'  -ErrorAction Stop).DefaultUserName }
    # 刻意不采集 DefaultPassword —— 证据文件会被回收到宿主机，不能带明文口令
    passwordStored   = Try-Get { $null -ne (Get-ItemProperty -Path $winlogonKey -Name 'DefaultPassword' -ErrorAction Stop).DefaultPassword }
    autoLogonCount   = Try-Get { (Get-ItemProperty -Path $winlogonKey -Name 'AutoLogonCount'   -ErrorAction Stop).AutoLogonCount }
}

# ---------------------------------------------------------------- 日志
$logInfo = [ordered]@{
    path      = $logPath
    exists    = (Test-Path -LiteralPath $logPath)
    sizeBytes = $null
    lineCount = $null
    tail      = @()
    archived  = $null
}
if ($logInfo.exists) {
    try {
        $logInfo.sizeBytes = (Get-Item -LiteralPath $logPath).Length
        $allLines = Get-Content -LiteralPath $logPath -Encoding UTF8
        $logInfo.lineCount = @($allLines).Count
        $logInfo.tail = @($allLines | Select-Object -Last $LogTailLines)
        # 整份归档：wallpaper.log 超过 512KB 会被产品自己删掉重建，跨重启用例必须先落一份副本
        $archive = Join-Path $EvidenceDir "wallpaper-$safeLabel-$stamp.log"
        Copy-Item -LiteralPath $logPath -Destination $archive -Force
        $logInfo.archived = $archive
    }
    catch {
        $logInfo.tail = @("(读取日志失败: $($_.Exception.Message))")
    }
}

# ---------------------------------------------------------------- 壁纸库（V8 守门）
$libRoot   = Join-Path $AppDir 'WallpaperLibrary'
$focusMp4  = Join-Path $libRoot '深夜\动态壁纸\milkyway-1.mp4'
$demoMp4   = Join-Path $libRoot '晚上\动态壁纸\night-city-1.mp4'
$library = [ordered]@{
    root          = $libRoot
    exists        = (Test-Path -LiteralPath $libRoot)
    focusMedia    = $focusMp4
    focusExists   = (Test-Path -LiteralPath $focusMp4)
    focusSize     = $(if (Test-Path -LiteralPath $focusMp4) { (Get-Item -LiteralPath $focusMp4).Length } else { 0 })
    demoMedia     = $demoMp4
    demoExists    = (Test-Path -LiteralPath $demoMp4)
    demoSize      = $(if (Test-Path -LiteralPath $demoMp4) { (Get-Item -LiteralPath $demoMp4).Length } else { 0 })
}

# ---------------------------------------------------------------- 系统锚点
$os = Try-Get { Get-CimInstance Win32_OperatingSystem -ErrorAction Stop }
$system = [ordered]@{
    computerName    = $env:COMPUTERNAME
    userName        = $env:USERNAME
    sessionId       = [System.Diagnostics.Process]::GetCurrentProcess().SessionId
    nowLocal        = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff')
    nowUtc          = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
    # LastBootUpTime 是「真的重启过」的硬证据。V2/V11 必须靠它排除「其实没重启」的假象。
    lastBootUtc     = Try-Get { $os.LastBootUpTime.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ') }
    uptimeMinutes   = Try-Get { [math]::Round(((Get-Date) - $os.LastBootUpTime).TotalMinutes, 2) }
    osCaption       = Try-Get { $os.Caption }
    osBuild         = Try-Get { $os.BuildNumber }
    explorerRunning = [bool](Get-Process -Name explorer -ErrorAction SilentlyContinue)
    # 已知慢查询之一：Win32_VideoController。加单条超时兜底（20s）。
    monitorCount    = Invoke-WithTimeout -TimeoutMs 20000 -Default 0 -ScriptBlock {
                          @(Get-CimInstance Win32_VideoController -ErrorAction Stop).Count
                      }
    # 快速启动（Fast Startup）会把「关机」变成混合关机，是 V11 的已知干扰源，如实记录
    hiberbootEnabled = Try-Get {
        (Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power' `
                          -Name 'HiberbootEnabled' -ErrorAction Stop).HiberbootEnabled
    }
}
Trace-Step "system:done(monitorCount=$($system.monitorCount))"

# 事件 1074：谁发起了关机/重启。用于给 V11/V12 的日志行做时间锚定。
# 已知慢查询之一：Get-WinEvent -FilterHashtable。加单条超时兜底（20s），超时返回空数组。
Trace-Step "winevent:begin"
$shutdownEvents = Invoke-WithTimeout -TimeoutMs 20000 -Default @() -ScriptBlock {
    @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; Id = 1074 } -MaxEvents 5 -ErrorAction Stop |
      ForEach-Object {
          [ordered]@{
              timeUtc = $_.TimeCreated.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
              message = ($_.Message -replace '\s+', ' ').Trim()
          }
      })
} @()
Trace-Step "winevent:end(count=$($shutdownEvents.Count))"

# ---------------------------------------------------------------- 汇总
# 预计算两个「曾在 $state 字面量内联求值、可能阻塞」的调用，并加超时兜底：
#   * Get-Process -Name mpv      （进程存在且僵尸时偶发卡住）
#   * Get-ItemProperty HKCU:\Control Panel\Desktop -Name Wallpaper （注册表读取偶发阻塞）
# 把它们移出 $state 字面量，避免单个调用拖垮整轮序列化。
Trace-Step "build:precompute-members:begin"
$mpvCountVal = Invoke-WithTimeout -TimeoutMs 10000 -Default 0 -ScriptBlock {
    @(Get-Process -Name mpv -ErrorAction SilentlyContinue).Count
}
$wallpaperRegVal = Invoke-WithTimeout -TimeoutMs 10000 -Default $null -ScriptBlock {
    (Get-ItemProperty -Path 'HKCU:\Control Panel\Desktop' -Name 'Wallpaper' -ErrorAction Stop).Wallpaper
}
Trace-Step "build:precompute-members:end(mpv=$mpvCountVal)"

# 逐成员离散赋值 + 计时，便于下次 trace 精确定位「卡在哪个成员」。
# schema/label/stamp 必须最先写入：宿主机据此确认这份 state 属于哪一次采集。
$state = [ordered]@{}
$state.schema = 'gstack.desktop-state/1'
$state.label  = $Label
$state.stamp  = $stamp
Trace-Step "build:system"
$state.system = $system
Trace-Step "build:icons"
$state.icons = $iconResult
Trace-Step "build:settings"
$state.settings = [ordered]@{
    path                = $settingsPath
    exists              = (Test-Path -LiteralPath $settingsPath)
    parseError          = $settingsError
    raw                 = $settingsRaw
    DesiredIconsHidden  = Get-SettingField 'DesiredIconsHidden'
    RestoreIconsOnExit  = Get-SettingField 'RestoreIconsOnExit'
    LaunchOnStartup     = Get-SettingField 'LaunchOnStartup'
    ActiveSceneName     = Get-SettingField 'ActiveSceneName'
    RotationEnabled     = Get-SettingField 'RotationEnabled'
    AudioEnabled        = Get-SettingField 'AudioEnabled'
    Volume              = Get-SettingField 'Volume'
    RendererPid         = Get-SettingField 'RendererPid'
    LastMedia           = Get-SettingField 'LastMedia'
}
Trace-Step "build:scenesJson"
$state.scenesJson = [ordered]@{
    path   = $scenesPath
    exists = (Test-Path -LiteralPath $scenesPath)
}
Trace-Step "build:processes"
$state.processes = [ordered]@{
    all              = @($dsProcs)
    mainCount        = $mainProcs.Count
    rendererCount    = $rendererProcs.Count
    mainAlive        = ($mainProcs.Count -gt 0)
    mainIsBackground = [bool](@($mainProcs | Where-Object { $_.isBackground }).Count -gt 0)
    mpvCount         = $mpvCountVal
}
Trace-Step "build:startup"
$state.startup = [ordered]@{
    runKeyPath      = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    runValueName    = 'DesktopSuite'
    runValue        = $runValue
    registered      = [bool]($runValue)
    hasBackgroundArg = [bool]($runValue -and $runValue -match '--background')
}
Trace-Step "build:autoLogon"
$state.autoLogon = $autoLogon
Trace-Step "build:wallpaper"
$state.wallpaper = [ordered]@{
    registryPath = $wallpaperRegVal
    note         = '动态壁纸由 mpv 渲染到 WorkerW，不会改写此注册表值；判断动态壁纸请看 processes.rendererCount / mpvCount。'
}
Trace-Step "build:library"
$state.library = $library
Trace-Step "build:log"
$state.log = $logInfo
Trace-Step "build:shutdownEvents"
# 注意：PowerShell 函数 `return @()` 会被拆解成「无输出」，调用处拿到的是 $null 而不是空数组。
# 直接 @($null) 会得到「含一个 null 元素的数组」，让下游 .Count 读成 1，属于隐性脏数据，这里滤掉。
$state.shutdownEvents = @(@($shutdownEvents) | Where-Object { $null -ne $_ })
$state.collectorTimedOut = $false
Trace-Step "build:done"

# ---------------------------------------------------------------- 序列化
# 历史教训（本轮真机复现）：整份 $state 一把梭 `ConvertTo-Json -Depth 8` 会卡死 ——
# 进度标记停在 convert 之前、之后再无任何输出，powershell.exe 一直不退出，
# 宿主机 240s vmrun 上限到点强杀，state-*.json 根本不落地，
# 于是所有依赖 $State.startup 的判据（V12 的自启前置核对首当其冲）一律 BLOCKED。
#
# 改法：不再整体序列化，而是「逐顶层成员各自序列化 + 单成员超时 + 手工拼装」。
#   * 某个成员真卡死时只丢那一个成员（占位 {"_serializeFailed":true}），
#     startup / settings / processes / icons 等其余事实全部保住，用例照样能判；
#   * trace 里直接写出卡在哪个成员，不需要再二分定位；
#   * 最坏情况总耗时可控（成员数 × 15s），永远在 240s 内落盘。
# 为什么不用 ConvertTo-Json：
#   真机 trace 实测（collect-trace-v12-pre-*.log）显示，序列化走到 `settings` 成员时
#   ConvertTo-Json 直接挂死且**永不返回**；更糟的是把它放进 runspace 加 10s 超时也救不了 ——
#   PowerShell.Stop() 会一直等流水线停下来，卡在非托管代码里的命令根本停不下来，
#   于是超时兜底自己也挂死。结论：ConvertTo-Json 在本靶机上不可信，必须彻底绕开。
# 这里手写一个纯 PowerShell 的 JSON 写出器：只用 StringBuilder 和类型判断，
# 不加载任何模块、不建 runspace、不碰 COM，行为完全确定，不存在挂死可能。
function Write-JsonString {
    param([string] $Text)
    if ($null -eq $Text) { return '""' }
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('"')
    foreach ($ch in $Text.ToCharArray()) {
        $code = [int]$ch
        if     ($ch -eq '"')  { [void]$sb.Append('\"') }
        elseif ($ch -eq '\')  { [void]$sb.Append('\\') }
        elseif ($code -eq 8)  { [void]$sb.Append('\b') }
        elseif ($code -eq 12) { [void]$sb.Append('\f') }
        elseif ($code -eq 10) { [void]$sb.Append('\n') }
        elseif ($code -eq 13) { [void]$sb.Append('\r') }
        elseif ($code -eq 9)  { [void]$sb.Append('\t') }
        elseif ($code -lt 32 -or $code -gt 126) {
            # 非 ASCII 一律转义成 \uXXXX：证据文件要跨 guest→host 传输，
            # 中文路径/日志行不能因为编码问题把整份 JSON 变成乱码或不可解析。
            [void]$sb.Append(('\u{0:x4}' -f $code))
        }
        else { [void]$sb.Append($ch) }
    }
    [void]$sb.Append('"')
    return $sb.ToString()
}

function Write-JsonValue {
    param($Value, [int] $Depth = 8)
    if ($Depth -le 0)      { return '"<max-depth>"' }
    if ($null -eq $Value)  { return 'null' }
    if ($Value -is [bool]) { return $(if ($Value) { 'true' } else { 'false' }) }
    if ($Value -is [string]) { return (Write-JsonString $Value) }
    if ($Value -is [int] -or $Value -is [long] -or $Value -is [int16] -or $Value -is [byte] -or
        $Value -is [uint32] -or $Value -is [uint64] -or $Value -is [double] -or
        $Value -is [single] -or $Value -is [decimal]) {
        $s = [string]::Format([System.Globalization.CultureInfo]::InvariantCulture, '{0}', $Value)
        if ($s -match '^(NaN|Infinity|-Infinity)$') { return (Write-JsonString $s) }
        return $s
    }
    if ($Value -is [datetime]) { return (Write-JsonString $Value.ToString('yyyy-MM-ddTHH:mm:ss.fffK')) }
    if ($Value -is [System.Collections.IDictionary]) {
        $items = New-Object 'System.Collections.Generic.List[string]'
        foreach ($k in @($Value.Keys)) {
            $items.Add((Write-JsonString ([string]$k)) + ':' + (Write-JsonValue -Value $Value[$k] -Depth ($Depth - 1)))
        }
        return '{' + ($items -join ',') + '}'
    }
    if ($Value -is [System.Management.Automation.PSObject] -or $Value -is [psobject]) {
        $props = @($Value.PSObject.Properties)
        if ($props.Count -gt 0) {
            $items = New-Object 'System.Collections.Generic.List[string]'
            foreach ($p in $props) {
                $pv = $null
                try { $pv = $p.Value } catch { $pv = "(error: $($_.Exception.Message))" }
                $items.Add((Write-JsonString $p.Name) + ':' + (Write-JsonValue -Value $pv -Depth ($Depth - 1)))
            }
            return '{' + ($items -join ',') + '}'
        }
    }
    if ($Value -is [System.Collections.IEnumerable]) {
        $items = New-Object 'System.Collections.Generic.List[string]'
        foreach ($e in $Value) { $items.Add((Write-JsonValue -Value $e -Depth ($Depth - 1))) }
        return '[' + ($items -join ',') + ']'
    }
    return (Write-JsonString ([string]$Value))
}

Trace-Step "serialize:begin(members=$(@($state.Keys).Count),writer=pure-ps)"
$failedMembers = @()
$parts = New-Object 'System.Collections.Generic.List[string]'
foreach ($key in @($state.Keys)) {
    Trace-Step "serialize:member:$key"
    $frag = $null
    try { $frag = Write-JsonValue -Value $state[$key] -Depth 8 }
    catch { Trace-Step "serialize:member:$key -> EX $($_.Exception.Message)"; $frag = $null }
    if ([string]::IsNullOrWhiteSpace($frag)) {
        $failedMembers += $key
        Trace-Step "serialize:member:$key -> FAILED"
        $frag = '{"_serializeFailed":true}'
    }
    $parts.Add('"' + $key + '":' + $frag)
}
$failedJson = if ($failedMembers.Count -gt 0) { '["' + ($failedMembers -join '","') + '"]' } else { '[]' }
$parts.Add('"serializeFailedMembers":' + $failedJson)
$parts.Add('"leakedRunspaces":' + $script:LeakedRunspaces)
$json = '{' + ($parts -join ',') + '}'
Trace-Step "serialize:end(failed=$($failedMembers.Count))"

$outFile = Join-Path $EvidenceDir "state-$safeLabel-$stamp.json"
try {
    Set-Content -LiteralPath $outFile -Value $json -Encoding UTF8
    Trace-Step "write-ok:$outFile"
    Write-Output $outFile
    exit 0
}
catch {
    Write-Error "写入状态文件失败：$($_.Exception.Message)"
    Trace-Step "write-FAIL:$($_.Exception.Message)"
    exit 4
}

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
function _M($t) { "$(Get-Date -Format 'HH:mm:ss.fff') $t" | Add-Content -LiteralPath 'C:\gstack\evidence\collect-diag.txt' -Encoding ASCII }
"START" | Set-Content -LiteralPath 'C:\gstack\evidence\collect-diag.txt' -Encoding ASCII
_M 'M0 start'

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

# 统一的「尽力而为」包装：任何一项采集失败都记成 error 字符串，绝不抛出。
function Try-Get {
    param([scriptblock] $Block, $Default = $null)
    try { & $Block } catch { return "(error: $($_.Exception.Message))" }
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
    $rs = $null; $ps = $null
    try {
        $rs = [runspacefactory]::CreateRunspace()
        $rs.Open()
        $ps = [powershell]::Create()
        $ps.Runspace = $rs
        [void]$ps.AddScript($ScriptBlock.ToString())
        $handle = $ps.BeginInvoke()
        if ($handle.AsyncWaitHandle.WaitOne($TimeoutMs)) {
            try { return $ps.EndInvoke($handle) } catch { return $Default }
        }
        # 超时：强制停掉并吐默认
        try { $ps.Stop() } catch {}
        Trace-Step "TIMEOUT(<=$TimeoutMs ms) on guarded call"
        return $Default
    }
    catch { return $Default }
    finally {
        if ($ps) { try { $ps.Dispose() } catch {} }
        if ($rs) { try { $rs.Dispose() } catch {} }
    }
}

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
_M 'M1 after-icon-assert'
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
$state = [ordered]@{}
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
$state.shutdownEvents = @($shutdownEvents)
$state.collectorTimedOut = $false
Trace-Step "build:done"

# 序列化（已知会卡死的一步）：放进独立 runspace，30s 硬超时，绝不拖垮 240s vmrun 上限。
Trace-Step "serialize:begin"
$json = $null
$serializeErr = $null
try {
    $rs = [runspacefactory]::CreateRunspace()
    $rs.Open()
    $ps = [powershell]::Create()
    $ps.Runspace = $rs
    [void]$ps.AddCommand('ConvertTo-Json').AddParameter('Depth', 8).AddArgument($state)
    $h = $ps.BeginInvoke()
    if ($h.AsyncWaitHandle.WaitOne(30000)) {
        try { $json = ($ps.EndInvoke($h)) -join '' } catch { $serializeErr = "endinvoke: $($_.Exception.Message)" }
    }
    else {
        try { $ps.Stop() } catch {}
        $serializeErr = 'serialize-timeout(30s)'
    }
    if ($ps) { try { $ps.Dispose() } catch {} }
    if ($rs) { try { $rs.Dispose() } catch {} }
}
catch { $serializeErr = "setup: $($_.Exception.Message)" }
Trace-Step "serialize:end(status=$(if($json){'ok'}else{'FAIL'}))"

if ($null -eq $json) {
    # 降级：仅保留可序列化的标量字段，确保宿主机一定拿到一份 state json
    # （响亮失败而非超时 BLOCKED）。下一步再据此 bisect 真正卡死的成员。
    Trace-Step "serialize:FALLBACK-minimal"
    $state.collectorTimedOut = $true
    $state.serializeError    = $serializeErr
    $json = [ordered]@{
        schema               = 'gstack.desktop-state/1'
        label                = $Label
        stamp                = $stamp
        collectorTimedOut    = $true
        serializeError       = $serializeErr
        system_monitorCount  = $system.monitorCount
        shutdownEvents_count = @($shutdownEvents).Count
        settings_exists      = (Test-Path -LiteralPath $settingsPath)
        processes_mainAlive  = ($mainProcs.Count -gt 0)
        processes_mainCount  = $mainProcs.Count
        processes_rendererCount = $rendererProcs.Count
        icons_verdict        = if ($iconResult -and $iconResult.verdict) { $iconResult.verdict } else { 'unknown' }
        mpvCount             = $mpvCountVal
    } | ConvertTo-Json -Depth 4
}

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

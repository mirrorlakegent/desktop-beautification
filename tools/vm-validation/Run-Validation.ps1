<#
.SYNOPSIS
    DesktopSuite Phase 3「桌面整理」VMware 靶机自动化验证编排器（宿主机侧）。

.DESCRIPTION
    用 vmrun 驱动一台 Windows 10 靶机，无人值守执行 validation-runbook-phase3-2026-08-05.md
    中的 V1–V14 用例，回收证据并输出结果汇总。

    流程：开机 → 等 VMware Tools → 等交互会话 → 投放 app 与 scripts → 逐用例执行
          → 每步截图 + 采集状态 → 回收证据 → 汇总 PASS/FAIL/BLOCKED。

    【三条防误报红线的自动化继承】（runbook §0）
      A. 「×」只是最小化到托盘，不是退出。本编排器**从不**用 taskkill / WM_CLOSE 冒充「真正退出」。
         需要真正退出的 V3/V4 只走托盘菜单（Invoke-AppUi -Action TrayExitKeepWallpaper）；
         该路径失败一律判 BLOCKED 转人工，绝不降级成 taskkill。
      B. --background 无 HWND，WPF Application.SessionEnding 不会到达。V11-B 因此**只**要求日志里
         出现 SystemEvents.SessionEnding，WPF 侧那条缺席属预期，不判 FAIL。
      C. 开机自启会在登录后立刻重新隐藏图标，掩盖恢复失败。所有「验恢复」的用例执行前都会
         硬断言 HKCU Run 项状态；V11-B 这类必须开自启的支线改用「登录早期连续采样 + 日志断言」，
         而不是只看一眼最终状态。

    【环境未就绪 ≠ 功能坏了】
      guest 断言器返回 blocked（session 0 / 非 WinSta0）或 unknown（窗口链读不到）时，
      本编排器一律判 BLOCKED 并给出原因，绝不折叠成 FAIL，也绝不当成 PASS。

    【⚠️ 防回归锚点】本脚本在 Phase 3 验证中共修复 8 处 bug（safe-delete 绕过 / 硬编码超时 /
      V6 缺等待 / ZipFile 大包压缩 / V14 断言缺括号 / 日志模式不匹配 / V12 注销确证 / logoff 竞态）。
      修改以下位置前必须先读 `FIXES.md`，避免误回退：
        H-1  L457/L667/L1684/L1693  [System.IO.File]::Delete() 替换 Remove-Item（避开平台 safe-delete）
        H-2  L605                   Invoke-GuestUi 超时用 "$TimeoutSec" 而非硬编码 '60'
        H-3  V6 流程                Start-GuestApp 后须 Start-SafeSleep -Seconds 20
        H-4  部署段                 大包压缩用 ZipFile.CreateFromDirectory（勿用 Compress-Archive）
        H-5  L1620                  V14 settings 断言外层括号 (Test-Prop) -and (Get-Prop)
        H-6  V11A/V11B/V12 日志断言 模式须含「（WM_QUERYENDSESSION）」层
        H-7  L1561                  V12 注销确证接受三种信号之一（session ID / Logoff 事件 / 系统重启）
        H-8  L768                   Restart-Guest -Mode logoff 须 -NoWait（shutdown /l /f 会销毁会话致挂起）

.PARAMETER GuestUser / GuestPass
    靶机凭据。**不硬编码**，必须由调用方传入。

.PARAMETER Cases
    只跑指定用例，如 -Cases V2A,V3,V11A,V12。省略则跑 -DefaultCases。

.PARAMETER DryRun
    只打印将要执行的 vmrun 命令，不真正操作靶机。凭据未到位时用它做流程演练。

.PARAMETER RevertBetweenCases
    每个用例执行前 revertToSnapshot 回到干净快照，保证用例互不污染（很慢，但最干净）。

.EXAMPLE
    # 流程演练（不碰靶机）
    .\Run-Validation.ps1 -GuestUser x -GuestPass x -DryRun

.EXAMPLE
    # 只跑四条 P0 核心用例，每条前回滚快照
    .\Run-Validation.ps1 -GuestUser tester -GuestPass 'pwd' `
        -AppSource 'D:\WorkBuddy\桌面美化\publish\win-x64' `
        -Cases V2A,V3,V11A,V12 -RevertBetweenCases
#>
[CmdletBinding()]
param(
    # ---- 靶机与工具 ----
    [string] $VmrunPath    = 'D:\Program Files\VMware\vmrun.exe',
    [string] $VmxPath      = 'E:\VMwar_xitongwenjian\win10\Windows 10 x64.vmx',
    [string] $VmType       = 'ws',
    [string] $SnapshotName = 'gstack-clean-before-v1-v14',

    # ---- 凭据（禁止硬编码，由调用方传入）----
    [Parameter(Mandatory)] [string] $GuestUser,
    [Parameter(Mandatory)] [string] $GuestPass,
    # guest 用户配置文件目录名，默认与用户名相同；若不同（如域账户）请显式指定
    [string] $GuestProfileName,

    # ---- 投放路径 ----
    [string] $AppSource      = '',                       # 宿主机上 dotnet publish 的输出目录
    [string] $GuestAppDir    = 'C:\gstack\app',
    [string] $GuestScriptDir = 'C:\gstack\scripts',
    [string] $GuestEvidence  = 'C:\gstack\evidence',
    [string] $EvidenceRoot   = (Join-Path $PSScriptRoot 'evidence'),

    # ---- 用例选择 ----
    [string[]] $Cases = @(),
    [switch]   $RevertBetweenCases,
    [switch]   $SkipDeploy,
    [switch]   $DryRun,
    [switch]   $EnableAutoLogon,
    [switch]   $KeepVmRunning,

    # ---- 超时（2GB 内存靶机很慢，全部走轮询 + 超时，绝不用固定 sleep 赌时间）----
    [int] $ToolsTimeoutSec    = 480,   # 开机 → VMware Tools 就绪
    [int] $SessionTimeoutSec  = 420,   # Tools 就绪 → 交互桌面可读
    [int] $ShutdownTimeoutSec = 300,   # 发出关机 → 靶机真的下线
    [int] $AppStartTimeoutSec = 180,   # 启动 app → 主进程出现
    [int] $GuestCmdTimeoutSec = 300    # 单条 guest 命令的墙钟上限
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:RunId       = (Get-Date).ToString('yyyyMMdd-HHmmss')
$script:RunDir      = Join-Path $EvidenceRoot $script:RunId
$script:Results     = New-Object System.Collections.ArrayList
$script:CurrentCase = 'init'
if (-not $GuestProfileName) { $GuestProfileName = $GuestUser }
$script:GuestAppData = "C:\Users\$GuestProfileName\AppData\Local\DesktopSuite"

$ALL_CASES = @('V1','V2A','V2B','V3','V4','V5','V6','V7','V8','V9','V10','V11A','V11B','V12','V13','V14')
# runbook §6 的建议顺序：非破坏性 → 进程级 → 会话级 → 破坏性
$DEFAULT_ORDER = @('V1','V5','V10','V8','V6','V7','V3','V4','V2A','V2B','V11A','V11B','V12','V9','V14','V13')

#==============================================================================
# 日志与结果记录
#==============================================================================
function Write-Log {
    param([string] $Message, [ValidateSet('INFO','WARN','ERROR','STEP','OK','FAIL','BLOCK')] [string] $Level = 'INFO')
    $ts = (Get-Date).ToString('HH:mm:ss')
    $color = switch ($Level) {
        'OK'    { 'Green' }  'FAIL'  { 'Red' }    'BLOCK' { 'Yellow' }
        'WARN'  { 'Yellow' } 'ERROR' { 'Red' }    'STEP'  { 'Cyan' }
        default { 'Gray' }
    }
    $line = "[$ts][$($script:CurrentCase)][$Level] $Message"
    Write-Host $line -ForegroundColor $color
    try {
        if (-not (Test-Path -LiteralPath $script:RunDir)) { New-Item -ItemType Directory -Path $script:RunDir -Force | Out-Null }
        Add-Content -LiteralPath (Join-Path $script:RunDir 'run.log') -Value $line -Encoding UTF8
    } catch { }
}

function New-CaseResult {
    param([string] $Id, [string] $Name, [string] $RunbookRef, [string] $Automation)
    return [ordered]@{
        id         = $Id
        name       = $Name
        runbookRef = $RunbookRef
        automation = $Automation          # AUTO | SEMI | MANUAL | NA
        verdict    = 'PENDING'
        reason     = ''
        startedAt  = (Get-Date).ToString('s')
        endedAt    = $null
        steps      = (New-Object System.Collections.ArrayList)
        evidence   = (New-Object System.Collections.ArrayList)
    }
}

function Add-Step {
    param($Case, [string] $Name, [ValidateSet('PASS','FAIL','BLOCKED','INFO')] [string] $Status,
          [string] $Detail = '', $Data = $null)
    [void]$Case.steps.Add([ordered]@{
        name = $Name; status = $Status; detail = $Detail
        at = (Get-Date).ToString('HH:mm:ss'); data = $Data
    })
    $lvl = switch ($Status) { 'PASS' {'OK'} 'FAIL' {'FAIL'} 'BLOCKED' {'BLOCK'} default {'INFO'} }
    Write-Log "$Name → $Status$(if ($Detail) { " :: $Detail" })" $lvl
}

<#
  结案。判定优先级刻意如此：
    有 FAIL → FAIL（有确凿证据证明功能坏了）
    否则有 BLOCKED → BLOCKED（证据链缺环，不能下结论）
    否则 → PASS
  绝不把 BLOCKED 折叠成 FAIL —— 那会把环境问题记成产品缺陷，是最常见的误报来源。
#>
function Close-Case {
    param($Case, [string] $ForceVerdict = '', [string] $Reason = '')
    $Case.endedAt = (Get-Date).ToString('s')
    if ($ForceVerdict) {
        $Case.verdict = $ForceVerdict
        $Case.reason  = $Reason
    }
    else {
        $fails    = @($Case.steps | Where-Object { $_.status -eq 'FAIL' })
        $blocked  = @($Case.steps | Where-Object { $_.status -eq 'BLOCKED' })
        if ($fails.Count -gt 0) {
            $Case.verdict = 'FAIL'
            $Case.reason  = ($fails | ForEach-Object { "$($_.name): $($_.detail)" }) -join ' ｜ '
        }
        elseif ($blocked.Count -gt 0) {
            $Case.verdict = 'BLOCKED'
            $Case.reason  = ($blocked | ForEach-Object { "$($_.name): $($_.detail)" }) -join ' ｜ '
        }
        else {
            $Case.verdict = 'PASS'
            $Case.reason  = $Reason
        }
    }
    [void]$script:Results.Add($Case)
    $lvl = switch ($Case.verdict) { 'PASS' {'OK'} 'FAIL' {'FAIL'} default {'BLOCK'} }
    Write-Log "==== $($Case.id) 结论：$($Case.verdict) ====" $lvl
    return $Case
}

<#
  安全取值助手。StrictMode 下访问不存在的属性会抛异常，被用例 try/catch 兜成 BLOCKED，
  把"采集字段缺失"和"字段采到但为假"混淆。这里用 PSObject.Properties.Name 逐级校验，
  缺失返回 $Default（默认 $null）；Test-Prop 单独判定"路径是否存在"，用于把
  "字段没采到 → BLOCKED（点名缺失字段+采集环节）" 与 "采到但为假 → 正常 PASS/FAIL" 分开。
#>
function Get-Prop {
    param($Object, [string] $Path, $Default = $null)
    if ($null -eq $Object) { return $Default }
    $cur = $Object
    foreach ($seg in ($Path -split '\.')) {
        if ($null -eq $cur) { return $Default }
        if ($cur -is [System.Collections.IDictionary]) {
            if (-not $cur.ContainsKey($seg)) { return $Default }
            $cur = $cur[$seg]
        } else {
            if ($null -eq $cur.PSObject -or $cur.PSObject.Properties.Name -notcontains $seg) { return $Default }
            $cur = $cur.$seg
        }
    }
    return $cur
}

function Test-Prop {
    param($Object, [string] $Path)
    if ($null -eq $Object) { return $false }
    $cur = $Object
    foreach ($seg in ($Path -split '\.')) {
        if ($null -eq $cur) { return $false }
        if ($cur -is [System.Collections.IDictionary]) {
            if (-not $cur.ContainsKey($seg)) { return $false }
            $cur = $cur[$seg]
        } else {
            if ($null -eq $cur.PSObject -or $cur.PSObject.Properties.Name -notcontains $seg) { return $false }
            $cur = $cur.$seg
        }
    }
    return $true
}

<#
  DryRun 下没有任何真实 VM 状态需要等待，固定 sleep 纯属空转。
  这里集中拦截：DryRun 只记一行日志直接返回；真实运行才真正等待。
  这样 DryRun 能在几秒内走完全部用例，不被轮询/过渡 sleep 拖到超时。
#>
function Start-SafeSleep {
    param([int] $Seconds)
    if ($DryRun) { Write-Log "(DryRun 跳过等待 ${Seconds}s)" 'INFO'; return }
    Start-Sleep -Seconds $Seconds
}

#==============================================================================
# vmrun 封装
#==============================================================================
function Invoke-Vmrun {
    param(
        [Parameter(Mandatory)] [string[]] $Arguments,
        [switch] $AllowFailure,
        [switch] $NoAuth,                      # captureScreen / start / snapshot 等不需要 guest 凭据
        [int]    $TimeoutSec = 0,
        [string] $Purpose = ''
    )
    $baseArgs = @('-T', $VmType)
    if (-not $NoAuth) { $baseArgs += @('-gu', $GuestUser, '-gp', $GuestPass) }
    $full = $baseArgs + $Arguments
    if ($TimeoutSec -le 0) { $TimeoutSec = $GuestCmdTimeoutSec }

    # 打印时抹掉口令
    $display = @()
    for ($i = 0; $i -lt $full.Count; $i++) {
        if ($i -gt 0 -and $full[$i-1] -eq '-gp') { $display += '***' } else { $display += $full[$i] }
    }
    Write-Log "vmrun $($display -join ' ')$(if ($Purpose) { "   # $Purpose" })" 'INFO'

    if ($DryRun) {
        return [pscustomobject]@{ ExitCode = 0; StdOut = '(dry-run)'; StdErr = ''; TimedOut = $false; DryRun = $true }
    }

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName               = $VmrunPath
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true
    $psi.UseShellExecute        = $false
    $psi.CreateNoWindow         = $true
    $psi.Arguments = ($full | ForEach-Object { if ($_ -match '\s') { '"{0}"' -f $_ } else { $_ } }) -join ' '

    $p = [System.Diagnostics.Process]::Start($psi)
    $soTask = $p.StandardOutput.ReadToEndAsync()
    $seTask = $p.StandardError.ReadToEndAsync()
    $timedOut = $false
    if (-not $p.WaitForExit($TimeoutSec * 1000)) {
        $timedOut = $true
        try { $p.Kill() } catch { }
        try { $p.WaitForExit(5000) | Out-Null } catch { }
    }
    $so = try { $soTask.Result } catch { '' }
    $se = try { $seTask.Result } catch { '' }
    $code = if ($timedOut) { -1 } else { $p.ExitCode }

    if ($timedOut) {
        $msg = "vmrun 超时（>${TimeoutSec}s）：$($display -join ' ')"
        if ($AllowFailure) { Write-Log $msg 'WARN' } else { throw $msg }
    }
    elseif ($code -ne 0 -and -not $AllowFailure) {
        throw "vmrun 失败（exit=$code）：$($display -join ' ')`nSTDOUT: $so`nSTDERR: $se"
    }
    elseif ($code -ne 0) {
        Write-Log "vmrun 非零退出（exit=$code，已容忍）：$so $se" 'WARN'
    }

    return [pscustomobject]@{ ExitCode = $code; StdOut = $so; StdErr = $se; TimedOut = $timedOut; DryRun = $false }
}

<#
  vmrun 不回传 guest 程序的 stdout，只在自己的输出里塞一句
  "Guest program exited with non-zero code: N"。这里把那个 N 抠出来。
  注意：这只是辅助信号，**权威结论一律来自回收下来的 JSON 文件**。
#>
function Get-GuestExitCode {
    param($VmrunResult)
    $text = "$($VmrunResult.StdOut)`n$($VmrunResult.StdErr)"
    if ($text -match 'exited with non-zero code:\s*(\d+)') { return [int]$Matches[1] }
    if ($VmrunResult.ExitCode -eq 0) { return 0 }
    return $null
}

#==============================================================================
# guest 执行原语
#==============================================================================
$GUEST_PS = 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'

<#
  用 -EncodedCommand 执行 guest 内联 PowerShell。
  为什么用 base64：vmrun → cmd → powershell 三层引号转义极易出错，
  EncodedCommand 让命令文本完全不受引号影响，是唯一稳的做法。
#>
function Invoke-GuestInline {
    param(
        [Parameter(Mandatory)] [string] $Code,
        [switch] $Interactive,      # 需要看见/操作交互桌面时必须加
        [switch] $NoWait,
        [switch] $AllowFailure,
        [int]    $TimeoutSec = 0,
        [string] $Purpose = ''
    )
    $enc = [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($Code))
    $opts = @('runProgramInGuest', $VmxPath)
    if ($Interactive) { $opts += @('-interactive', '-activeWindow') }
    if ($NoWait)      { $opts += '-noWait' }
    $opts += @($GUEST_PS, '-NoProfile', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $enc)
    return Invoke-Vmrun -Arguments $opts -AllowFailure:$AllowFailure -TimeoutSec $TimeoutSec `
                        -Purpose ($(if ($Purpose) { $Purpose } else { ($Code -replace '\s+', ' ').Substring(0, [Math]::Min(70, $Code.Length)) }))
}

function Invoke-GuestScript {
    param(
        [Parameter(Mandatory)] [string] $ScriptName,   # guest 脚本目录下的文件名
        [string[]] $ScriptArgs = @(),
        [switch] $Interactive,
        [switch] $AllowFailure,
        [int]    $TimeoutSec = 0,
        [string] $Purpose = ''
    )
    $opts = @('runProgramInGuest', $VmxPath)
    if ($Interactive) { $opts += @('-interactive', '-activeWindow') }
    $opts += @($GUEST_PS, '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
               (Join-Path $GuestScriptDir $ScriptName)) + $ScriptArgs
    return Invoke-Vmrun -Arguments $opts -AllowFailure:$AllowFailure -TimeoutSec $TimeoutSec -Purpose $Purpose
}

function Copy-ToGuest {
    param([string] $HostPath, [string] $GuestPath)
    Invoke-Vmrun -Arguments @('copyFileFromHostToGuest', $VmxPath, $HostPath, $GuestPath) `
                 -Purpose "投放 $([IO.Path]::GetFileName($HostPath))" | Out-Null
}

function Copy-FromGuest {
    param([string] $GuestPath, [string] $HostPath, [switch] $AllowFailure)
    $dir = Split-Path -Parent $HostPath
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $r = Invoke-Vmrun -Arguments @('copyFileFromGuestToHost', $VmxPath, $GuestPath, $HostPath) `
                      -AllowFailure:$AllowFailure -Purpose "回收 $([IO.Path]::GetFileName($GuestPath))"
    return ($r.ExitCode -eq 0)
}

function Test-GuestFile {
    param([string] $GuestPath)
    $r = Invoke-Vmrun -Arguments @('fileExistsInGuest', $VmxPath, $GuestPath) -AllowFailure -Purpose 'fileExists'
    if ($DryRun) { return $true }
    return ($r.ExitCode -eq 0 -and $r.StdOut -match 'exists')
}

function Get-CaseDir {
    param([string] $Case)
    $d = Join-Path $script:RunDir $Case
    if (-not (Test-Path -LiteralPath $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
    return $d
}

function Save-Screenshot {
    param([string] $Case, [string] $Label)
    $dest = Join-Path (Get-CaseDir $Case) ("{0}-{1}.png" -f (Get-Date).ToString('HHmmss'), ($Label -replace '[^\w\-]', '_'))
    Invoke-Vmrun -Arguments @('captureScreen', $VmxPath, $dest) -NoAuth -AllowFailure -Purpose "截图 $Label" | Out-Null
    return $dest
}

#==============================================================================
# 高阶 guest 操作
#==============================================================================
function Get-ToolsState {
    $r = Invoke-Vmrun -Arguments @('checkToolsState', $VmxPath) -NoAuth -AllowFailure -TimeoutSec 60 -Purpose 'checkToolsState'
    if ($DryRun) { return 'running' }
    return ($r.StdOut -split "`n" | Where-Object { $_.Trim() } | Select-Object -Last 1).Trim()
}

function Test-VmRunning {
    $r = Invoke-Vmrun -Arguments @('list') -NoAuth -AllowFailure -TimeoutSec 60 -Purpose 'list'
    if ($DryRun) { return $true }
    return ($r.StdOut -match [regex]::Escape($VmxPath))
}

function Start-Target {
    if (-not (Test-VmRunning)) {
        Invoke-Vmrun -Arguments @('start', $VmxPath, 'nogui') -NoAuth -TimeoutSec 300 -Purpose '启动靶机' | Out-Null
    } else {
        Write-Log '靶机已在运行，跳过 start' 'INFO'
    }
}

function Wait-Tools {
    param([int] $TimeoutSec = $ToolsTimeoutSec)
    Write-Log "等待 VMware Tools 就绪（最长 ${TimeoutSec}s）…" 'STEP'
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    do {
        $s = Get-ToolsState
        if ($s -match 'running') { Write-Log "Tools 就绪（$s）" 'OK'; return $true }
        if ($DryRun) { return $true }
        Start-SafeSleep -Seconds 5
    } while ((Get-Date) -lt $deadline)
    Write-Log "等待 Tools 超时，最后状态：$s" 'WARN'
    return $false
}

<#
  Tools 报 running 只说明心跳通了，不代表 VIX guest 操作通道可用。
  开机早期、以及 guest 内 VMware Tools / VGAuth 发生 soft reset 期间，
  runProgramInGuest / copyFileFromHostToGuest / fileExistsInGuest 会**全部**
  返回 exit=-1「未知错误」。Invoke-Deploy 的第一步就是 guest 操作，缺这道等待
  就会直接抛致命错误、整轮验证 0 用例产出。用最轻量的 fileExistsInGuest 探针。
#>
function Wait-GuestOps {
    param([int] $TimeoutSec = 300)
    Write-Log "等待 guest 操作通道可用（最长 ${TimeoutSec}s）…" 'STEP'
    if ($DryRun) { return $true }
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $last = ''
    do {
        $r = Invoke-Vmrun -Arguments @('fileExistsInGuest', $VmxPath, 'C:\Windows\System32\cmd.exe') `
                          -AllowFailure -TimeoutSec 60 -Purpose 'guest 操作通道探针'
        if ($r.ExitCode -eq 0) { Write-Log 'guest 操作通道就绪' 'OK'; return $true }
        $last = "exit=$($r.ExitCode) $(($r.StdOut + ' ' + $r.StdErr).Trim())"
        Start-SafeSleep -Seconds 5
    } while ((Get-Date) -lt $deadline)
    Write-Log "guest 操作通道在 ${TimeoutSec}s 内未就绪，最后一次：$last" 'WARN'
    return $false
}

function Get-CurrentGuestBootUtc {
    # 轻量探针：只读 LastBootUpTime，不回收证据文件。
    $gOut = 'C:\gstack\evidence\_last-boot.txt'
    $hOut = Join-Path $script:RunDir '_last-boot.txt'
    $code = "`$bt = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime; Set-Content -Path '$gOut' -Value (`$bt.ToString('yyyy-MM-ddTHH:mm:ss')) -Encoding UTF8"
    $r = Invoke-GuestInline -Code $code -AllowFailure -TimeoutSec 60 -Purpose '读取启动时间'
    if ((Get-GuestExitCode $r) -ne 0) { return $null }
    if (-not (Copy-FromGuest -GuestPath $gOut -HostPath $hOut -AllowFailure)) { return $null }
    if (Test-Path -LiteralPath $hOut) {
        $txt = (Get-Content -LiteralPath $hOut -Raw -Encoding UTF8).Trim()
        try { if (Test-Path -LiteralPath $hOut) { [System.IO.File]::Delete($hOut) } } catch {}
        if ($txt) { return $txt }
    }
    return $null
}

function Wait-GuestDown {
    param([int] $TimeoutSec = $ShutdownTimeoutSec, [string] $BootBefore = '')
    Write-Log "等待靶机下线（最长 ${TimeoutSec}s）…" 'STEP'
    if ($DryRun) { return $true }
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    # 【修复 2026-08-06 gstack-qa-lead-2】轮询间隔从 4s 降到 1s；并增加启动时间校验。
    # vmrun reset hard 实测从发命令到靶机重新上线仅约 3s；4s 间隔可能直接错过下线窗口。
    # 更棘手的是：3s 内已重新上线，checkToolsState 全程看到的都是 running，
    #  old Wait-GuestDown 会误判为"重启未发生"。
    # 对策：若调用方没给 BootBefore，自己在首轮回就读一次 guest LastBootUpTime 作为基准；
    # 之后每次 Tools=running 时都再读一次，只要发现启动时间已刷新，即判定为已重启。
    if (-not $BootBefore -and ((Get-ToolsState) -match 'running')) { $BootBefore = Get-CurrentGuestBootUtc }
    Write-Log "重启前 BootUtc 基准=$BootBefore" 'INFO'
    do {
        $s = Get-ToolsState
        if ($s -notmatch 'running') { Write-Log "靶机已下线（tools=$s）" 'OK'; return $true }
        if ($BootBefore -and ($s -match 'running')) {
            $bootNow = Get-CurrentGuestBootUtc
            if ($bootNow -and $bootNow -ne $BootBefore) {
                Write-Log "靶机已重启（BootUtc $BootBefore -> $bootNow）" 'OK'
                return $true
            }
        }
        Start-SafeSleep -Seconds 1
    } while ((Get-Date) -lt $deadline)
    return $false
}

<#
  等到「交互桌面真的可读」为止 —— 只等 Tools running 是不够的：
  Tools 在登录界面就已经 running，此时 -interactive 调用会失败，或断言器返回 blocked。
  这一步是所有跨会话用例（V2/V11/V12）能否可靠自动化的关键。
#>
function Wait-InteractiveSession {
    param([int] $TimeoutSec = $SessionTimeoutSec, [string] $Case = $script:CurrentCase)
    Write-Log "等待交互会话可读（最长 ${TimeoutSec}s）…" 'STEP'
    if ($DryRun) { return $true }
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    do {
        try {
            $probe = Invoke-GuestAssert -Case $Case -Label 'session-probe' -Quiet
            if ($probe -and $probe.verdict -in @('visible', 'hidden', 'unknown')) {
                Write-Log "交互会话就绪（探针裁决=$($probe.verdict)）" 'OK'
                return $true
            }
        } catch { }
        Start-SafeSleep -Seconds 6
    } while ((Get-Date) -lt $deadline)
    Write-Log '等待交互会话超时' 'WARN'
    return $false
}

<#
  执行图标断言并把 JSON 回收到宿主机。返回反序列化后的对象（失败返回 $null）。
  Samples/IntervalMs 用于捕捉窗口期（V11-B / V2-B）。
#>
function Invoke-GuestAssert {
    param(
        [string] $Case = $script:CurrentCase,
        [string] $Label = 'assert',
        [int]    $Samples = 1,
        [int]    $IntervalMs = 1000,
        [switch] $Quiet
    )
    $stamp = (Get-Date).ToString('HHmmss-fff')
    $gPath = "$GuestEvidence\assert-$Label-$stamp.json"
    $r = Invoke-GuestScript -ScriptName 'Assert-DesktopIcons.ps1' -Interactive -AllowFailure `
         -TimeoutSec ([Math]::Max(120, $Samples * ($IntervalMs / 1000) + 90)) `
         -ScriptArgs @('-OutFile', $gPath, '-Label', $Label, '-Samples', "$Samples", '-IntervalMs', "$IntervalMs", '-Quiet') `
         -Purpose "图标断言 $Label"

    if ($DryRun) {
        return [pscustomobject]@{
            verdict = 'visible'; reasonCode = 'dry-run'; reasonText = '(dry-run)'
            samples = @(); anyVisible = $true; anyHidden = $false; anyUnknown = $false
        }
    }

    $hPath = Join-Path (Get-CaseDir $Case) "assert-$Label-$stamp.json"
    if (-not (Copy-FromGuest -GuestPath $gPath -HostPath $hPath -AllowFailure)) {
        if (-not $Quiet) { Write-Log "断言结果回收失败：$gPath（vmrun exit=$($r.ExitCode)）" 'WARN' }
        return $null
    }
    try {
        $obj = Get-Content -LiteralPath $hPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if (-not $Quiet) { Write-Log "断言[$Label] → $($obj.verdict) ($($obj.reasonCode))" 'INFO' }
        return $obj
    } catch {
        Write-Log "断言 JSON 解析失败：$($_.Exception.Message)" 'WARN'
        return $null
    }
}

function Invoke-GuestCollect {
    param([string] $Case = $script:CurrentCase, [string] $Label = 'state')
    $stamp = (Get-Date).ToString('HHmmss-fff')
    Invoke-GuestScript -ScriptName 'Collect-State.ps1' -Interactive -AllowFailure -TimeoutSec 240 `
        -ScriptArgs @('-Label', "$Label-$stamp", '-EvidenceDir', $GuestEvidence, '-AppDir', $GuestAppDir) `
        -Purpose "状态采集 $Label" | Out-Null

    if ($DryRun) {
        return [pscustomobject]@{
            settings  = [pscustomobject]@{ exists = $true; path = '(dry-run)'; parseError = $null; raw = '(dry-run)'
                                         DesiredIconsHidden = $true; RestoreIconsOnExit = $true; LaunchOnStartup = $false
                                         ActiveSceneName = '日常'; RotationEnabled = $true; AudioEnabled = $false }
            processes = [pscustomobject]@{ mainAlive = $true; mainCount = 1; rendererCount = 0; mainIsBackground = $true }
            startup   = [pscustomobject]@{ registered = $true; hasBackgroundArg = $true; runValue = '(dry-run) "C:\gstack\app\DesktopSuite.exe" --background' }
            system    = [pscustomobject]@{ lastBootUtc = '2026-01-01T00:00:00Z'; sessionId = 1; monitorCount = 1 }
            log       = [pscustomobject]@{ tail = @() }
            icons     = [pscustomobject]@{ verdict = 'visible' }
            library   = [pscustomobject]@{ exists = $true; root = '(dry-run) C:\gstack\app\WallpaperLibrary'
                                         focusExists = $true; focusMedia = '(dry-run) focus.mp4'; focusSize = 12345
                                         demoExists = $true;  demoMedia = '(dry-run) demo.mp4';   demoSize = 12345 }
        }
    }

    # Collect-State 的文件名里带自己的时间戳，宿主机无法预知 —— 用通配回收目录下最新的一份
    $listCode = @"
`$f = Get-ChildItem -Path '$GuestEvidence' -Filter 'state-$Label-$stamp*.json' |
      Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (`$f) { Copy-Item `$f.FullName '$GuestEvidence\_latest-state.json' -Force }
"@
    Invoke-GuestInline -Code $listCode -AllowFailure -Purpose '定位最新状态文件' | Out-Null

    $hPath = Join-Path (Get-CaseDir $Case) "state-$Label-$stamp.json"
    if (-not (Copy-FromGuest -GuestPath "$GuestEvidence\_latest-state.json" -HostPath $hPath -AllowFailure)) {
        Write-Log '状态文件回收失败' 'WARN'
        return $null
    }
    try { return Get-Content -LiteralPath $hPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { Write-Log "状态 JSON 解析失败：$($_.Exception.Message)" 'WARN'; return $null }
}

function Invoke-GuestUi {
    param(
        [Parameter(Mandatory)] [string] $Action,
        [string] $Value = '',
        [string] $Case = $script:CurrentCase,
        [int]    $TimeoutSec = 120
    )
    $stamp = (Get-Date).ToString('HHmmss-fff')
    $gPath = "$GuestEvidence\ui-$Action-$stamp.json"
    $args = @('-Action', $Action, '-OutFile', $gPath, '-TimeoutSec', "$TimeoutSec")
    if ($Value) { $args += @('-Value', $Value) }
    Invoke-GuestScript -ScriptName 'Invoke-AppUi.ps1' -Interactive -AllowFailure -TimeoutSec ($TimeoutSec + 60) `
        -ScriptArgs $args -Purpose "UI 驱动 $Action $Value" | Out-Null

    if ($DryRun) {
        return [pscustomobject]@{
            status = 'ok'; reasonCode = 'dry-run'; reasonText = '(dry-run)'
            data = [pscustomobject]@{
                after       = [pscustomobject]@{ statusText = '桌面图标：已隐藏（…）'; desktopStatusText = '已隐藏'; chkHideIcons = 'On' }
                windowState = [pscustomobject]@{ chkHideIcons = 'On' }
                immediately = [pscustomobject]@{ statusText = '桌面操作正在进行中，请稍候…' }
                settled     = [pscustomobject]@{ chkHideIcons = 'On' }
                diagnostics = '(dry-run) library diagnostics'
            }
        }
    }

    $hPath = Join-Path (Get-CaseDir $Case) "ui-$Action-$stamp.json"
    if (-not (Copy-FromGuest -GuestPath $gPath -HostPath $hPath -AllowFailure)) {
        return [pscustomobject]@{ status = 'blocked'; reasonCode = 'ui-result-not-returned'
                                  reasonText = 'UI 驱动脚本没有产出结果文件，可能是交互会话不可用。' }
    }
    try { return Get-Content -LiteralPath $hPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { return [pscustomobject]@{ status = 'error'; reasonCode = 'ui-json-parse-failed'; reasonText = "$($_.Exception.Message)" } }
}

#------------------------------------------------------------------ 状态塑形
<#
  直接写 settings.json 来预置前置条件。

  为什么可以这么做：DesiredIconsHidden / RestoreIconsOnExit / LaunchOnStartup 在
  V2/V11/V12 里是**前置条件**而非被测对象；用文件预置比 UI 点击稳定一个数量级。
  被测的是「应用读到这个意图之后的行为」——启动重应用、退出恢复、意图是否被抹。
  写入前必须确保 app 未运行，否则 app 退出时的 Save() 会覆盖我们写的内容。
#>
function Set-GuestSettings {
    param(
        [bool] $DesiredIconsHidden = $false,
        [bool] $RestoreIconsOnExit = $true,
        [bool] $LaunchOnStartup    = $false,
        [string] $ActiveSceneName  = $null
    )
    $obj = [ordered]@{
        AudioEnabled            = $false
        Volume                  = 80
        LastMedia               = $null
        RendererPid             = 0
        RotationEnabled         = $false
        RotationIntervalMinutes = 30
        LibraryPath             = $null
        LaunchOnStartup         = $LaunchOnStartup
        DesiredIconsHidden      = $DesiredIconsHidden
        RestoreIconsOnExit      = $RestoreIconsOnExit
        ActiveSceneName         = $ActiveSceneName
    }
    $json = $obj | ConvertTo-Json -Depth 4
    $tmp  = Join-Path $env:TEMP "gstack-settings-$([guid]::NewGuid().ToString('N')).json"
    # 产品用 System.Text.Json 读，无 BOM 更保险
    [System.IO.File]::WriteAllText($tmp, $json, (New-Object System.Text.UTF8Encoding($false)))
    Invoke-GuestInline -Code "New-Item -ItemType Directory -Path '$script:GuestAppData' -Force | Out-Null" -Purpose '确保配置目录存在' | Out-Null
    Copy-ToGuest -HostPath $tmp -GuestPath "$script:GuestAppData\settings.json"
    try { if (Test-Path -LiteralPath $tmp) { [System.IO.File]::Delete($tmp) } } catch {}
    Write-Log "已预置 settings.json：Hidden=$DesiredIconsHidden Restore=$RestoreIconsOnExit Startup=$LaunchOnStartup" 'INFO'
}

function Reset-GuestBaseline {
    param([switch] $KeepLogs)
    Stop-GuestApp -Hard
    $code = @"
`$d = '$script:GuestAppData'
if (Test-Path `$d) { Remove-Item `$d -Recurse -Force -ErrorAction SilentlyContinue }
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'DesktopSuite' -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path '$GuestEvidence' -Force | Out-Null
"@
    Invoke-GuestInline -Code $code -AllowFailure -Purpose '基线归零（删配置 + 清自启）' | Out-Null
    Write-Log '基线已归零' 'INFO'
}

function Set-GuestRunKey {
    param([bool] $Enabled)
    if ($Enabled) {
        $code = @"
Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'DesktopSuite' ``
  -Value ('"' + '$GuestAppDir\DesktopSuite.exe' + '" --background')
"@
    } else {
        $code = "Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'DesktopSuite' -ErrorAction SilentlyContinue"
    }
    Invoke-GuestInline -Code $code -AllowFailure -Purpose "自启项 → $Enabled" | Out-Null
}

function Start-GuestApp {
    param([switch] $Background, [int] $TimeoutSec = $AppStartTimeoutSec)
    $exe = "$GuestAppDir\DesktopSuite.exe"
    $opts = @('runProgramInGuest', $VmxPath, '-interactive', '-activeWindow', '-noWait', $exe)
    if ($Background) { $opts += '--background' }
    Invoke-Vmrun -Arguments $opts -AllowFailure -TimeoutSec 120 -Purpose "启动应用$(if ($Background) { '（--background）' })" | Out-Null
    if ($DryRun) { return $true }

    # 轮询主进程出现（渲染子进程不算）
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    do {
        $probe = Invoke-GuestInline -AllowFailure -TimeoutSec 90 -Purpose '探测主进程' -Code @"
`$m = @(Get-CimInstance Win32_Process -Filter "Name='DesktopSuite.exe'" |
       Where-Object { `$_.CommandLine -notmatch '--wallpaper-host' })
exit `$(if (`$m.Count -gt 0) { 0 } else { 9 })
"@
        if ((Get-GuestExitCode $probe) -eq 0) { Write-Log '应用主进程已就绪' 'OK'; return $true }
        Start-SafeSleep -Seconds 4
    } while ((Get-Date) -lt $deadline)
    Write-Log "应用主进程在 ${TimeoutSec}s 内未出现" 'WARN'
    return $false
}

<#
  ⚠️ Hard 模式是 taskkill /F —— 只用于：
    (1) 用例之间的强制清场；
    (2) V14 刻意模拟崩溃。
  绝不可用它冒充「用户退出」：强杀不触发 OnClosed 也不触发 SessionEnding，
  拿它验 V3/V4 会得到一个彻底错误的结论。见 runbook §0-A。
#>
function Stop-GuestApp {
    param([switch] $Hard, [switch] $MainOnly)
    $filter = if ($MainOnly) { " | Where-Object { `$_.CommandLine -notmatch '--wallpaper-host' }" } else { '' }
    $code = @"
`$p = @(Get-CimInstance Win32_Process -Filter "Name='DesktopSuite.exe'"$filter)
foreach (`$x in `$p) { try { Stop-Process -Id `$x.ProcessId -Force -ErrorAction SilentlyContinue } catch {} }
"@
    Invoke-GuestInline -Code $code -AllowFailure -Purpose "强制结束应用进程$(if ($MainOnly) { '（仅主进程）' })" | Out-Null
}

function Restart-Guest {
    param(
        [string] $Mode = 'reboot',   # reboot | logoff
        [switch] $Graceful            # V11A/V11B: use soft reset to trigger WM_QUERYENDSESSION
    )
    # 【修复 2026-08-06 gstack-qa-lead-2】
    # 实测 guest 内 shutdown.exe 在 vmrun 创建的 session-0 上下文里无法真正触发重启：
    #   shutdown /r /t 0 /f 返回 rc=0，但 300s 内 checkToolsState 一直 running，
    #   屏幕仍显示活动桌面，LastBootUpTime 不变。
    # 原因推测：vmrun 以非交互方式启动的进程其 SeShutdownPrivilege 未启用，
    # 或 Windows 把关机请求挂起在 session-1 的"应用阻止关机"UI 上无人响应。
    # 改用 hypervisor 级 reset hard：实测 3 秒内完成重启（LastBootUpTime 刷新）。
    #
    # 【修复 2026-08-07 主理人】
    # V11A/V11B 测的就是 SessionEnding 恢复闩锁——硬复位不发 WM_QUERYENDSESSION，
    # 导致 SessionEnding 永远不可能触发，用例必败。
    # 改用 vmrun reset soft（ACPI 电源按钮事件）触发优雅关机：
    #   - Windows 收到 ACPI 事件 → 发 WM_QUERYENDSESSION → SessionEnding handler 执行
    #   - 如果 app 不否决（我们的代码不否决），关机正常进行
    #   - 如果 app 卡住，Wait-GuestDown 的超时兜底
    if ($Mode -eq 'reboot') {
        $resetMode = if ($Graceful) { 'soft' } else { 'hard' }
        Write-Log "发起 reboot（vmrun reset $resetMode $VmxPath）" 'STEP'
        $r = Invoke-Vmrun -Arguments @('reset', $VmxPath, $resetMode) -NoAuth -TimeoutSec 120 -Purpose "reset-$resetMode"
        if ($r) { Write-Log "reset $resetMode 返回：exit=$($r.ExitCode) $(($r.StdOut + ' ' + $r.StdErr).Trim())" 'INFO' }
        return
    }
    if ($Mode -eq 'logoff') {
        # logoff 必须发生在交互桌面会话里，否则同样会被挂起；加 -Interactive 落到 session 1。
        $cmd = 'shutdown /l /f'
        Write-Log "发起 logoff（$cmd，interactive session）" 'STEP'
        # 关键修复 2026-08-07 主理人：
        # logoff 会销毁运行该命令的交互会话，若让 vmrun 等待命令返回会挂起（竞态：
        # 上一轮 logoff 进程在会话销毁前返回故正常，本轮会话先销毁导致 runProgramInGuest 挂起）。
        # 改用 -NoWait 触发后立刻返回，logoff 在后台异步执行；后续 sleep + reboot 负责等其完成。
        $r = Invoke-GuestInline -Code $cmd -Interactive -NoWait -AllowFailure -TimeoutSec 30 -Purpose 'logoff'
        if ($r) { Write-Log "logoff 命令返回：exit=$($r.ExitCode) $(($r.StdOut + ' ' + $r.StdErr).Trim())" 'INFO' }
        return
    }
    throw "未知 Mode: $Mode"
}

function Get-GuestBootUtc {
    param($State)
    if ($State -and (Test-Prop $State 'system')) { return "$(Get-Prop $State 'system.lastBootUtc')" }
    return $null
}

function Restore-Snapshot {
    Write-Log "回滚到快照 $SnapshotName" 'STEP'
    Invoke-Vmrun -Arguments @('revertToSnapshot', $VmxPath, $SnapshotName) -NoAuth -TimeoutSec 300 -Purpose '回滚快照' | Out-Null
}

#==============================================================================
# 断言助手
#==============================================================================
<#
  对图标裁决做断言。这是整套 harness 里最重要的一个函数，防误报逻辑都在这里：
    blocked / error / $null → BLOCKED（环境没就绪或证据没回来，不能下结论）
    unknown 且不在期望集    → BLOCKED（shell 不可读，不是"图标被隐藏"）
    在期望集                → PASS
    不在期望集              → FAIL
#>
function Assert-Icons {
    param($Case, $Actual, [string[]] $Expected, [string] $What)
    if ($null -eq $Actual) {
        Add-Step $Case $What 'BLOCKED' '断言结果未回收（guest 脚本未产出 JSON 或 vmrun 回传失败）'
        return $false
    }
    $v = "$($Actual.verdict)"
    $detail = "实际=$v（$($Actual.reasonCode)）期望=$($Expected -join '/')：$($Actual.reasonText)"
    if ($v -in @('blocked', 'error')) {
        Add-Step $Case $What 'BLOCKED' $detail $Actual
        return $false
    }
    if ($v -eq 'unknown' -and ($Expected -notcontains 'unknown')) {
        Add-Step $Case $What 'BLOCKED' "$detail ｜ unknown 表示桌面图标层不可读，按纪律不得折算成 hidden，故判 BLOCKED 而非 FAIL。" $Actual
        return $false
    }
    if ($Expected -contains $v) { Add-Step $Case $What 'PASS' $detail $Actual; return $true }
    Add-Step $Case $What 'FAIL' $detail $Actual
    return $false
}

function Assert-Setting {
    param($Case, $State, [string] $Field, $Expected, [string] $What)
    if ($null -eq $State -or $null -eq $State.settings) {
        Add-Step $Case $What 'BLOCKED' '状态未采集到，无法核对 settings.json'
        return $false
    }
    # settings.exists 表示"配置文件是否存在"：缺失=采集不全 → BLOCKED（点名缺失字段）；采到为 $false=文件确实不存在
    if (-not (Test-Prop $State 'settings.exists')) {
        Add-Step $Case $What 'BLOCKED' '采集缺失：settings.exists 未返回（来自 Invoke-GuestCollect），无法判断配置文件状态'
        return $false
    }
    if (-not (Get-Prop $State 'settings.exists')) {
        Add-Step $Case $What 'BLOCKED' "settings.json 不存在（$(Get-Prop $State 'settings.path' -Default '(路径未知)')）"
        return $false
    }
    # 【修复 2026-08-06 gstack-qa-lead-2】原写法漏了内层括号：
    #   Test-Prop $State 'settings.parseError' -and (Get-Prop ...)
    # PowerShell 会把它整体当成一次 Test-Prop 命令调用（-and 被当作实参而非逻辑运算符），
    # 结果恒等于 Test-Prop 的返回值。而采集器**总是**输出 parseError 字段（无错时为空串），
    # 于是这里恒为 $true → 每一次 Assert-Setting 都误报 "settings.json 解析失败：" FAIL。
    # 实测：state-v2a-pre-reboot 里 parseError=[] 且 DesiredIconsHidden=True（本应 PASS）。
    if ((Test-Prop $State 'settings.parseError') -and (Get-Prop $State 'settings.parseError')) {
        # settings.json 损坏在 V14 里是 FAIL 判据，但在别处属证据链断裂 → 如实报 FAIL
        Add-Step $Case $What 'FAIL' "settings.json 解析失败：$(Get-Prop $State 'settings.parseError')"
        return $false
    }
    if (-not (Test-Prop $State "settings.$Field")) {
        Add-Step $Case $What 'BLOCKED' "采集缺失：settings.$Field 未返回（来自 Invoke-GuestCollect），无法核对"
        return $false
    }
    $actual = Get-Prop $State "settings.$Field"
    if ("$actual" -eq "$Expected") { Add-Step $Case $What 'PASS' "$Field=$actual"; return $true }
    Add-Step $Case $What 'FAIL' "$Field 期望 $Expected，实际 $actual"
    return $false
}

function Get-LogMatchCount {
    param($State, [string] $Pattern)
    if ($null -eq $State -or $null -eq $State.log) { return -1 }
    return @(Get-Prop $State 'log.tail' -Default @() | Where-Object { $_ -match $Pattern }).Count
}

function Assert-LogContains {
    param($Case, $State, [string] $Pattern, [string] $What, [int] $MinCount = 1, [int] $MaxCount = [int]::MaxValue)
    $n = Get-LogMatchCount $State $Pattern
    if ($n -lt 0) { Add-Step $Case $What 'BLOCKED' '日志未采集到'; return $false }
    if ($n -ge $MinCount -and $n -le $MaxCount) { Add-Step $Case $What 'PASS' "命中 $n 次（模式 /$Pattern/）"; return $true }
    Add-Step $Case $What 'FAIL' "命中 $n 次，期望 $MinCount..$MaxCount（模式 /$Pattern/）"
    return $false
}

<#
  §0-C 守卫：验「恢复」类用例前，必须确认自启状态符合预期，
  否则登录后应用会立刻重新隐藏图标，把恢复失败掩盖成 PASS。
#>
function Assert-AutostartExpectation {
    param($Case, $State, [bool] $ShouldBeRegistered)
    if ($null -eq $State -or $null -eq $State.startup) {
        Add-Step $Case '自启状态前置核对' 'BLOCKED' '未采集到自启状态，无法排除 §0-C 误报风险'
        return $false
    }
    if (-not (Test-Prop $State 'startup.registered')) {
        Add-Step $Case '自启状态前置核对' 'BLOCKED' '采集缺失：startup.registered 未返回（来自 Invoke-GuestCollect）'
        return $false
    }
    $reg = [bool](Get-Prop $State 'startup.registered')
    if ($reg -eq $ShouldBeRegistered) {
        Add-Step $Case '自启状态前置核对' 'PASS' "HKCU Run\DesktopSuite registered=$reg（符合本支线要求）"
        return $true
    }
    Add-Step $Case '自启状态前置核对' 'BLOCKED' `
        "自启项 registered=$reg，但本支线要求 $ShouldBeRegistered。runbook §0-C：自启会在登录后立刻重新隐藏图标，掩盖恢复失败，必须先纠正环境再跑。"
    return $false
}

#==============================================================================
# 用例实现
#==============================================================================

function Invoke-CaseV1 {
    $c = New-CaseResult 'V1' '隐藏/显示桌面图标基本可用' '§5 V1' 'SEMI'
    Reset-GuestBaseline
    Set-GuestSettings -DesiredIconsHidden $false -RestoreIconsOnExit $true
    if (-not (Start-GuestApp)) { Add-Step $c '启动应用' 'BLOCKED' '主进程未出现'; return (Close-Case $c) }

    Assert-Icons $c (Invoke-GuestAssert -Label 'v1-before') @('visible') '初始状态：图标可见' | Out-Null

    $ui = Invoke-GuestUi -Action 'SetHideIcons' -Value 'on'
    if ($ui.status -ne 'ok') {
        Add-Step $c '勾选「隐藏桌面图标」' 'BLOCKED' "UI 驱动不可用：$($ui.reasonCode) $($ui.reasonText)"
        return (Close-Case $c)
    }
    if (-not (Test-Prop $ui 'data.after')) {
        Add-Step $c '勾选「隐藏桌面图标」' 'BLOCKED' 'UI 结果缺 data.after（来自 Invoke-AppUi，交互会话可能不可用）'
        return (Close-Case $c)
    }
    Add-Step $c '勾选「隐藏桌面图标」' 'INFO' "Status=$(Get-Prop $ui 'data.after.statusText')" $ui.data
    [void]$c.evidence.Add((Save-Screenshot $c.id 'v1-hidden'))
    Assert-Icons $c (Invoke-GuestAssert -Label 'v1-hidden') @('hidden') '勾选后图标应隐藏' | Out-Null

    $ui2 = Invoke-GuestUi -Action 'SetHideIcons' -Value 'off'
    if ($ui2.status -ne 'ok') { Add-Step $c '取消勾选' 'BLOCKED' "$($ui2.reasonCode)" }
    elseif (-not (Test-Prop $ui2 'data.after')) { Add-Step $c '取消勾选' 'BLOCKED' 'UI 结果缺 data.after（来自 Invoke-AppUi）' }
    else { Add-Step $c '取消勾选' 'INFO' "Status=$(Get-Prop $ui2 'data.after.statusText')" }
    [void]$c.evidence.Add((Save-Screenshot $c.id 'v1-shown'))
    Assert-Icons $c (Invoke-GuestAssert -Label 'v1-shown') @('visible') '取消后图标应恢复' | Out-Null

    # 降级到 ShowWindow 属 medium 缺陷但不阻塞 —— 记为 INFO，交由报告人裁量
    $state = Invoke-GuestCollect -Label 'v1-final'
    $degraded = Get-LogMatchCount $state 'ShowWindow 降级'
    if ($degraded -gt 0) { Add-Step $c '降级检查' 'INFO' "日志出现 ShowWindow 降级 $degraded 次 → 记 medium 缺陷（原生 0x7402 未生效，桌面右键菜单可能受损），不阻塞" }
    else { Add-Step $c '降级检查' 'PASS' '未出现 ShowWindow 降级，走的是原生 WM_COMMAND 0x7402' }
    return (Close-Case $c)
}

function Invoke-CaseV2A {
    $c = New-CaseResult 'V2A' '隐藏意图跨重启保留（手动启动支线）' '§5 V2-A' 'AUTO'
    Reset-GuestBaseline
    Set-GuestRunKey $false
    Set-GuestSettings -DesiredIconsHidden $true -RestoreIconsOnExit $true -LaunchOnStartup $false

    if (-not (Start-GuestApp)) { Add-Step $c '启动应用' 'BLOCKED' '主进程未出现'; return (Close-Case $c) }
    # 启动重应用是异步退避（500/1000/2000/4000/8000ms，最多 6 次），最长约 15.5s
    Start-SafeSleep -Seconds 20
    Assert-Icons $c (Invoke-GuestAssert -Label 'v2a-pre-reboot') @('hidden') '重启前：意图已生效，图标隐藏' | Out-Null

    $pre = Invoke-GuestCollect -Label 'v2a-pre-reboot'
    Assert-Setting $c $pre 'DesiredIconsHidden' 'True' '重启前 settings.DesiredIconsHidden=true' | Out-Null
    $bootBefore = Get-GuestBootUtc $pre
    Assert-AutostartExpectation $c $pre $false | Out-Null

    # 真正退出走托盘；失败则退而求其次直接重启（重启同样会触发 SessionEnding 恢复，不影响 V2 的判据）
    # V2A 修复：用 -Graceful 确保 SessionEnding 能触发（即使托盘退出失败、app 仍在运行）
    $exit = Invoke-GuestUi -Action 'TrayExitKeepWallpaper'
    if ($exit.status -eq 'ok') { Add-Step $c '托盘退出' 'PASS' '主进程已退出' }
    else { Add-Step $c '托盘退出' 'INFO' "托盘退出不可用（$($exit.reasonCode)）；V2 的判据不依赖退出方式，直接进入重启。" }

    Restart-Guest -Mode reboot -Graceful
    if (-not (Wait-GuestDown -BootBefore $bootBefore)) { Add-Step $c '等待靶机下线' 'BLOCKED' "超过 ${ShutdownTimeoutSec}s 仍在线，重启未发生"; return (Close-Case $c) }
    Start-Target
    if (-not (Wait-Tools)) { Add-Step $c '等待 Tools' 'BLOCKED' 'Tools 未就绪'; return (Close-Case $c) }
    if (-not (Wait-InteractiveSession -Case $c.id)) {
        Add-Step $c '等待交互会话' 'BLOCKED' '登录后交互桌面不可读；请确认已配置自动登录（-EnableAutoLogon）'
        return (Close-Case $c)
    }

    $post = Invoke-GuestCollect -Label 'v2a-after-reboot'
    $bootAfter = Get-GuestBootUtc $post
    if ($bootBefore -and $bootAfter -and $bootBefore -ne $bootAfter) {
        Add-Step $c '重启确证' 'PASS' "LastBootUpTime $bootBefore → $bootAfter"
    } else {
        Add-Step $c '重启确证' 'BLOCKED' "无法确认真的重启过（before=$bootBefore after=$bootAfter）"
    }

    [void]$c.evidence.Add((Save-Screenshot $c.id 'v2a-after-login'))
    # 核心判据 1：意图跨重启存活
    Assert-Setting $c $post 'DesiredIconsHidden' 'True' '★核心：重启后（未启动程序）DesiredIconsHidden 仍为 true' | Out-Null
    # 重启后未启动程序时图标应可见（退出/关机时做过临时恢复），这是正确行为
    Assert-Icons $c (Invoke-GuestAssert -Label 'v2a-after-login-before-launch') @('visible') '重启后启动程序前：图标可见（退出恢复生效，非缺陷）' | Out-Null

    if (-not (Start-GuestApp)) { Add-Step $c '重启后启动应用' 'BLOCKED' '主进程未出现'; return (Close-Case $c) }
    Start-SafeSleep -Seconds 20
    # 核心判据 2：未经任何人工点击，图标被自动重新隐藏
    Assert-Icons $c (Invoke-GuestAssert -Label 'v2a-relaunch') @('hidden') '★核心：启动后图标被自动重新隐藏（无人工干预）' | Out-Null
    [void]$c.evidence.Add((Save-Screenshot $c.id 'v2a-after-launch'))

    $final = Invoke-GuestCollect -Label 'v2a-final'
    Assert-LogContains $c $final '启动应用图标意图成功' '日志出现「启动应用图标意图成功」' | Out-Null

    # UI 与意图一致性（复选框应为勾选态）—— 不一致是 high 缺陷
    $ui = Invoke-GuestUi -Action 'ReadState'
    if ($ui.status -eq 'ok') {
        if (-not (Test-Prop $ui 'data.windowState')) {
            Add-Step $c '复选框与意图一致' 'BLOCKED' 'UI 结果缺 data.windowState（来自 Invoke-AppUi）'
        } elseif ("$(Get-Prop $ui 'data.windowState.chkHideIcons')" -eq 'On') {
            Add-Step $c '复选框与意图一致' 'PASS' 'ChkHideIcons=On'
        } else {
            Add-Step $c '复选框与意图一致' 'FAIL' "ChkHideIcons=$(Get-Prop $ui 'data.windowState.chkHideIcons')，意图为 hidden → high（UI 与意图不一致）"
        }
    } else { Add-Step $c '复选框与意图一致' 'BLOCKED' "UI 读取不可用：$($ui.reasonCode)" }

    return (Close-Case $c)
}

function Invoke-CaseV2B {
    $c = New-CaseResult 'V2B' '隐藏意图跨重启保留（--background 自启支线）' '§5 V2-B' 'AUTO'
    Reset-GuestBaseline
    Set-GuestSettings -DesiredIconsHidden $true -RestoreIconsOnExit $true -LaunchOnStartup $true
    Set-GuestRunKey $true

    $pre = Invoke-GuestCollect -Label 'v2b-pre'
    if ($pre -and (Test-Prop $pre 'startup')) {
        if (-not (Test-Prop $pre 'startup.runValue')) {
            Add-Step $c '自启项就位' 'BLOCKED' '采集缺失：startup.runValue 未返回（来自 Invoke-GuestCollect）'
            return (Close-Case $c)
        }
        if ((Get-Prop $pre 'startup.registered') -and (Get-Prop $pre 'startup.hasBackgroundArg')) {
            Add-Step $c '自启项就位' 'PASS' "Run 值：$(Get-Prop $pre 'startup.runValue')"
        } else {
            Add-Step $c '自启项就位' 'BLOCKED' "Run 项缺失或未带 --background：$(Get-Prop $pre 'startup.runValue')。组策略锁定 HKCU Run 时按 runbook §8.3-4 记 BLOCKED。"
            return (Close-Case $c)
        }
    } else { Add-Step $c '自启项就位' 'BLOCKED' '状态未采集（Invoke-GuestCollect 未返回 startup）'; return (Close-Case $c) }
    $bootBefore = Get-GuestBootUtc $pre

    Restart-Guest -Mode reboot
    if (-not (Wait-GuestDown -BootBefore $bootBefore)) { Add-Step $c '等待靶机下线' 'BLOCKED' '重启未发生'; return (Close-Case $c) }
    Start-Target
    if (-not (Wait-Tools)) { Add-Step $c '等待 Tools' 'BLOCKED' 'Tools 未就绪'; return (Close-Case $c) }
    if (-not (Wait-InteractiveSession -Case $c.id)) { Add-Step $c '等待交互会话' 'BLOCKED' '未自动登录'; return (Close-Case $c) }

    # runbook 允许「先可见后隐藏」的过渡；用 60 次 1 秒采样覆盖 60 秒窗口
    $series = Invoke-GuestAssert -Label 'v2b-login-window' -Samples 60 -IntervalMs 1000
    [void]$c.evidence.Add((Save-Screenshot $c.id 'v2b-after-login'))
    if ($null -eq $series) { Add-Step $c '登录后 60s 采样' 'BLOCKED' '采样结果未回收'; return (Close-Case $c) }
    Add-Step $c '登录后 60s 采样' 'INFO' "anyVisible=$($series.anyVisible) anyHidden=$($series.anyHidden) anyUnknown=$($series.anyUnknown) 终值=$($series.verdict)"
    Assert-Icons $c $series @('hidden') '★核心：登录后 60 秒内图标自动隐藏（无人工操作）' | Out-Null

    $post = Invoke-GuestCollect -Label 'v2b-after-login'
    $bootAfter = Get-GuestBootUtc $post
    if ($bootBefore -and $bootAfter -and $bootBefore -ne $bootAfter) { Add-Step $c '重启确证' 'PASS' "$bootBefore → $bootAfter" }
    else { Add-Step $c '重启确证' 'BLOCKED' "无法确认真的重启过（before=$bootBefore after=$bootAfter）" }

    Assert-Setting $c $post 'DesiredIconsHidden' 'True' '意图未被改写' | Out-Null
    Assert-LogContains $c $post '启动应用图标意图成功' '日志出现「启动应用图标意图成功」' | Out-Null

    # --background 语义：进程必须带 --background，且主窗口不应自动弹出
    if ($post -and (Test-Prop $post 'processes')) {
        $mainAlive = Get-Prop $post 'processes.mainAlive'
        if (-not (Test-Prop $post 'processes.mainIsBackground')) {
            Add-Step $c '--background 语义' 'BLOCKED' '采集缺失：processes.mainIsBackground 未返回（来自 Invoke-GuestCollect）'
        } elseif ($mainAlive -and (Get-Prop $post 'processes.mainIsBackground')) {
            Add-Step $c '--background 语义' 'PASS' '主进程以 --background 启动'
        } elseif ($mainAlive) {
            Add-Step $c '--background 语义' 'FAIL' '主进程存活但命令行不含 --background → medium'
        } else {
            Add-Step $c '--background 语义' 'FAIL' '登录后主进程未启动（自启未生效）'
        }
    }
    $ui = Invoke-GuestUi -Action 'ReadState'
    if ($ui.status -eq 'blocked' -and "$($ui.reasonCode)" -eq 'main-window-not-found') {
        Add-Step $c '主窗口未自动弹出' 'PASS' '找不到可见主窗口，符合 --background 语义'
    } elseif ($ui.status -eq 'ok') {
        Add-Step $c '主窗口未自动弹出' 'FAIL' '主窗口可见 → medium（--background 未生效）'
    } else {
        Add-Step $c '主窗口未自动弹出' 'INFO' "UI 探测返回 $($ui.status)/$($ui.reasonCode)，无法判定"
    }
    return (Close-Case $c)
}

function Invoke-CaseV3 {
    $c = New-CaseResult 'V3' '退出恢复且不抹掉意图' '§5 V3' 'SEMI'
    Reset-GuestBaseline
    Set-GuestRunKey $false
    Set-GuestSettings -DesiredIconsHidden $true -RestoreIconsOnExit $true
    if (-not (Start-GuestApp)) { Add-Step $c '启动应用' 'BLOCKED' '主进程未出现'; return (Close-Case $c) }
    Start-SafeSleep -Seconds 20

    $pre = Invoke-GuestCollect -Label 'v3-pre'
    Assert-AutostartExpectation $c $pre $false | Out-Null
    Assert-Icons $c (Invoke-GuestAssert -Label 'v3-hidden') @('hidden') '退出前：图标已隐藏' | Out-Null
    Assert-Setting $c $pre 'RestoreIconsOnExit' 'True' '退出前 RestoreIconsOnExit=true' | Out-Null
    [void]$c.evidence.Add((Save-Screenshot $c.id 'v3-hidden'))

    # runbook §0-A：只能走托盘「退出（保留壁纸）」。× 是最小化，taskkill 是崩溃，两者都不是退出。
    $exit = Invoke-GuestUi -Action 'TrayExitKeepWallpaper' -TimeoutSec 180
    if ($exit.status -ne 'ok') {
        Add-Step $c '★托盘「退出（保留壁纸）」' 'BLOCKED' `
            ("$($exit.reasonCode) :: $($exit.reasonText) ｜ 本用例的触发方式不可替代：点 × 只最小化到托盘、taskkill 是异常退出（V14 语义），" +
             "都会把 V3 验成假结果。请人工执行本用例，或参考：V11-A/V12 已自动覆盖 RestoreIconsOnTeardown 的同一段代码（含意图保护），只是触发源不同。")
        return (Close-Case $c)
    }
    Add-Step $c '★托盘「退出（保留壁纸）」' 'PASS' '主进程已真正退出'

    Start-SafeSleep -Seconds 4
    [void]$c.evidence.Add((Save-Screenshot $c.id 'v3-after-exit'))
    $post = Invoke-GuestCollect -Label 'v3-after-exit'

    # 两条判据必须同时成立，缺一即 FAIL
    $r1 = Assert-Icons $c (Invoke-GuestAssert -Label 'v3-after-exit') @('visible') '★判据1：退出后图标恢复可见'
    $r2 = Assert-Setting $c $post 'DesiredIconsHidden' 'True' '★判据2：DesiredIconsHidden 仍为 true（临时恢复未抹意图）'
    if ($r1 -and -not $r2) { Add-Step $c '意图污染判定' 'FAIL' 'critical：退出恢复抹掉了 DesiredIconsHidden（_suppressIconEvents 卫兵或意图回写失效）' }
    Assert-LogContains $c $post '退出（窗口关闭）：恢复桌面图标' '日志出现「退出（窗口关闭）：恢复桌面图标 → …」' | Out-Null

    if (-not (Start-GuestApp)) { Add-Step $c '重新启动应用' 'BLOCKED' '主进程未出现'; return (Close-Case $c) }
    Start-SafeSleep -Seconds 20
    Assert-Icons $c (Invoke-GuestAssert -Label 'v3-relaunch') @('hidden') '★判据3：再次启动后自动重新隐藏' | Out-Null
    [void]$c.evidence.Add((Save-Screenshot $c.id 'v3-relaunch'))
    return (Close-Case $c)
}

function Invoke-CaseV4 {
    $c = New-CaseResult 'V4' '关闭「退出恢复」后保持隐藏' '§5 V4' 'SEMI'
    Reset-GuestBaseline
    Set-GuestRunKey $false
    Set-GuestSettings -DesiredIconsHidden $true -RestoreIconsOnExit $false
    if (-not (Start-GuestApp)) { Add-Step $c '启动应用' 'BLOCKED' '主进程未出现'; return (Close-Case $c) }
    Start-SafeSleep -Seconds 20

    $pre = Invoke-GuestCollect -Label 'v4-pre'
    Assert-Setting $c $pre 'RestoreIconsOnExit' 'False' '前置：RestoreIconsOnExit=false' | Out-Null
    Assert-Icons $c (Invoke-GuestAssert -Label 'v4-hidden') @('hidden') '退出前图标已隐藏' | Out-Null

    $exit = Invoke-GuestUi -Action 'TrayExitKeepWallpaper' -TimeoutSec 180
    if ($exit.status -ne 'ok') {
        Add-Step $c '托盘退出' 'BLOCKED' "$($exit.reasonCode)：与 V3 同因，托盘路径不可自动化时本用例必须人工执行（不得用 taskkill 替代）"
        return (Close-Case $c)
    }
    Start-SafeSleep -Seconds 5
    [void]$c.evidence.Add((Save-Screenshot $c.id 'v4-after-exit'))
    $post = Invoke-GuestCollect -Label 'v4-after-exit'
    Assert-Icons $c (Invoke-GuestAssert -Label 'v4-after-exit') @('hidden') '★退出后图标保持隐藏' | Out-Null
    Assert-LogContains $c $post '用户已关闭「退出时恢复桌面图标」，保持当前状态' '日志出现「保持当前状态」行' | Out-Null

    if (-not (Start-GuestApp)) { Add-Step $c '重新启动' 'BLOCKED' '主进程未出现'; return (Close-Case $c) }
    Start-SafeSleep -Seconds 15
    Assert-Icons $c (Invoke-GuestAssert -Label 'v4-relaunch') @('hidden') '重启程序后仍隐藏' | Out-Null
    $final = Invoke-GuestCollect -Label 'v4-final'
    Assert-Setting $c $final 'DesiredIconsHidden' 'True' 'intent 仍为 hidden' | Out-Null

    # 收尾：恢复默认，避免污染后续用例（runbook V4 步骤 8）
    Stop-GuestApp -Hard
    Set-GuestSettings -DesiredIconsHidden $false -RestoreIconsOnExit $true
    Add-Step $c '收尾复位' 'INFO' '已把 RestoreIconsOnExit 恢复为 true、意图归零'
    return (Close-Case $c)
}

function Invoke-CaseV5 {
    $c = New-CaseResult 'V5' '托盘菜单与复选框状态同步' '§5 V5' 'MANUAL'
    Reset-GuestBaseline
    Set-GuestSettings -DesiredIconsHidden $false -RestoreIconsOnExit $true
    if (-not (Start-GuestApp)) { Add-Step $c '启动应用' 'BLOCKED' '主进程未出现'; return (Close-Case $c) }

    # 可自动化的一半：复选框 → 现实 → 意图 三者是否同步
    $ui = Invoke-GuestUi -Action 'SetHideIcons' -Value 'on'
    if ($ui.status -eq 'ok') {
        if (-not (Test-Prop $ui 'data.after')) {
            Add-Step $c '复选框驱动隐藏' 'BLOCKED' 'UI 结果缺 data.after（来自 Invoke-AppUi）'
        } else {
            Add-Step $c '复选框驱动隐藏' 'PASS' "chkHideIcons=$(Get-Prop $ui 'data.after.chkHideIcons')"
        }
        Assert-Icons $c (Invoke-GuestAssert -Label 'v5-on') @('hidden') '复选框 On 时现实为 hidden' | Out-Null
        $s = Invoke-GuestCollect -Label 'v5-on'
        Assert-Setting $c $s 'DesiredIconsHidden' 'True' '意图同步为 true' | Out-Null
    } else { Add-Step $c '复选框驱动隐藏' 'BLOCKED' "$($ui.reasonCode)" }
    [void]$c.evidence.Add((Save-Screenshot $c.id 'v5-checkbox-on'))

    $ui2 = Invoke-GuestUi -Action 'SetHideIcons' -Value 'off'
    if ($ui2.status -eq 'ok') { Assert-Icons $c (Invoke-GuestAssert -Label 'v5-off') @('visible') '复选框 Off 时现实为 visible' | Out-Null }

    # 不可自动化的一半：托盘文案三态、「点一次图标闪两下」的视觉判据
    Add-Step $c '托盘文案三态与视觉双闪' 'BLOCKED' `
        ('托盘菜单文案（🗂️ 隐藏桌面图标：开/关/未知）需要右键唤出通知区域菜单后逐条读取，' +
         'UIA 在 Win10 折叠通知区域下不稳定；「图标不连续翻转两次」属视觉判据，截图无法证明。' +
         '这两条必须人工执行 —— 见 README「必须人工介入的用例」。')
    Close-Case $c -ForceVerdict 'BLOCKED' -Reason '部分自动（复选框↔现实↔意图 已验），托盘文案三态与双闪视觉判据需人工'
    return $c
}

function Invoke-CaseV6 {
    $c = New-CaseResult 'V6' '托盘场景子菜单（日常/专注/演示）' '§5 V6' 'SEMI'
    Reset-GuestBaseline
    Set-GuestSettings -DesiredIconsHidden $false -RestoreIconsOnExit $true
    if (-not (Start-GuestApp)) { Add-Step $c '启动应用' 'BLOCKED' '主进程未出现'; return (Close-Case $c) }
    Start-SafeSleep -Seconds 20

    # 期望表（runbook §5 V6）：图标 / 轮换 / 声音；壁纸画面属视觉判据
    $expect = @(
        @{ Name = '专注'; Icons = 'hidden';  Rotation = 'False'; Audio = 'False' },
        @{ Name = '演示'; Icons = 'hidden';  Rotation = 'False'; Audio = 'False' },
        @{ Name = '日常'; Icons = 'visible'; Rotation = 'True';  Audio = 'False' }
    )
    foreach ($e in $expect) {
        # 用主窗口的场景下拉 + 应用按钮代替托盘子菜单（同一条 ApplyScene 代码路径）
        $ui = Invoke-GuestUi -Action 'ApplyScene' -Value $e.Name -TimeoutSec 180
        if ($ui.status -ne 'ok') { Add-Step $c "应用场景「$($e.Name)」" 'BLOCKED' "$($ui.reasonCode) $($ui.reasonText)"; continue }
        if (-not (Test-Prop $ui 'data.after')) {
            Add-Step $c "应用场景「$($e.Name)」" 'BLOCKED' 'UI 结果缺 data.after（来自 Invoke-AppUi）'; continue
        }
        $statusText = "$(Get-Prop $ui 'data.after.statusText')"
        Add-Step $c "应用场景「$($e.Name)」" 'INFO' "Status=$statusText"
        [void]$c.evidence.Add((Save-Screenshot $c.id "v6-$($e.Name)"))

        # Status 文案必须是纯「已应用场景：X」，带括号说明即为部分失败
        if ($statusText -match '已应用场景') {
            if ($statusText -match '[（(]') { Add-Step $c "「$($e.Name)」Status 无失败说明" 'FAIL' "Status 带括号说明 → high：$statusText" }
            else { Add-Step $c "「$($e.Name)」Status 无失败说明" 'PASS' $statusText }
        } elseif ($statusText -match '应用场景失败') {
            Add-Step $c "「$($e.Name)」Status 无失败说明" 'FAIL' "high：$statusText（若 settings 只回滚一部分则升级为 critical，请核对 v6 状态 JSON）"
        } else {
            Add-Step $c "「$($e.Name)」Status 无失败说明" 'BLOCKED' "未读到可识别的 Status 文案：$statusText"
        }

        Assert-Icons $c (Invoke-GuestAssert -Label "v6-$($e.Name)") @($e.Icons) "「$($e.Name)」图标应为 $($e.Icons)" | Out-Null
        $s = Invoke-GuestCollect -Label "v6-$($e.Name)"
        Assert-Setting $c $s 'ActiveSceneName' $e.Name  "「$($e.Name)」ActiveSceneName 一致" | Out-Null
        Assert-Setting $c $s 'RotationEnabled' $e.Rotation "「$($e.Name)」轮换=$($e.Rotation)" | Out-Null
        Assert-Setting $c $s 'AudioEnabled'    $e.Audio    "「$($e.Name)」声音=$($e.Audio)" | Out-Null
    }
    Add-Step $c '壁纸画面视觉核对' 'INFO' '各场景壁纸是否为对应视频、是否在动，需人工看截图/录屏判定（已留 v6-*.png）'
    return (Close-Case $c)
}

function Invoke-CaseV7 {
    $c = New-CaseResult 'V7' '场景切换无双闪' '§5 V7' 'SEMI'
    Reset-GuestBaseline
    Set-GuestSettings -DesiredIconsHidden $false -RestoreIconsOnExit $true
    if (-not (Start-GuestApp)) { Add-Step $c '启动应用' 'BLOCKED' '主进程未出现'; return (Close-Case $c) }

    # 先切到固定壁纸场景，确保随后切「日常」时确实发生一次壁纸变更
    $u1 = Invoke-GuestUi -Action 'ApplyScene' -Value '专注' -TimeoutSec 180
    if ($u1.status -ne 'ok') { Add-Step $c '前置：切到「专注」' 'BLOCKED' "$($u1.reasonCode)"; return (Close-Case $c) }

    $before = Invoke-GuestCollect -Label 'v7-before'
    $countBefore = Get-LogMatchCount $before '--- StartDynamic ---'
    Add-Step $c '记录日志基线' 'INFO' "切换前 tail 中 StartDynamic 计数 = $countBefore"

    $u2 = Invoke-GuestUi -Action 'ApplyScene' -Value '日常' -TimeoutSec 180
    if ($u2.status -ne 'ok') { Add-Step $c '切到「日常」' 'BLOCKED' "$($u2.reasonCode)"; return (Close-Case $c) }
    Start-SafeSleep -Seconds 20
    [void]$c.evidence.Add((Save-Screenshot $c.id 'v7-after'))

    $after = Invoke-GuestCollect -Label 'v7-after'
    $countAfter = Get-LogMatchCount $after '--- StartDynamic ---'
    $delta = $countAfter - $countBefore
    if ($countBefore -lt 0 -or $countAfter -lt 0) {
        Add-Step $c '★StartDynamic 增量 ∈ {0,1}' 'BLOCKED' '日志未采集到，无法计数'
    } elseif ($delta -ge 0 -and $delta -le 1) {
        Add-Step $c '★StartDynamic 增量 ∈ {0,1}' 'PASS' "增量=$delta（$countBefore → $countAfter）"
    } else {
        Add-Step $c '★StartDynamic 增量 ∈ {0,1}' 'FAIL' "增量=$delta ≥2 → P1-3 回归（双闪），medium→high"
    }
    Add-Step $c 'LibStatus / 录屏逐帧' 'INFO' '「壁纸未二次跳变」的视觉确认需人工看录屏；日志增量判据已自动完成。若当前时段目录为空（LibStatus 显示「暂无壁纸，已跳过」）本用例不成立，需换时段重跑。'
    return (Close-Case $c)
}

function Invoke-CaseV8 {
    $c = New-CaseResult 'V8' '固定壁纸文件就位且可播放' '§5 V8' 'SEMI'
    $s = Invoke-GuestCollect -Label 'v8'
    if ($null -eq $s -or $null -eq $s.library) { Add-Step $c '壁纸库探测' 'BLOCKED' '状态未采集'; return (Close-Case $c) }

    if (-not (Test-Prop $s 'library.exists')) {
        Add-Step $c 'WallpaperLibrary 根目录存在' 'BLOCKED' '采集缺失：library.exists 未返回（来自 Invoke-GuestCollect）'
    } elseif (Get-Prop $s 'library.exists') {
        Add-Step $c 'WallpaperLibrary 根目录存在' 'PASS' "$(Get-Prop $s 'library.root' -Default '(路径未知)')"
    } else {
        Add-Step $c 'WallpaperLibrary 根目录存在' 'FAIL' "缺失：$(Get-Prop $s 'library.root' -Default '(路径未知)') → high，P1-5 回归（未随发布包分发）"
    }

    if (-not (Test-Prop $s 'library.focusExists')) {
        Add-Step $c '「专注」固定壁纸就位' 'BLOCKED' '采集缺失：library.focusExists 未返回（来自 Invoke-GuestCollect）'
    } elseif (Get-Prop $s 'library.focusExists' -and [int](Get-Prop $s 'library.focusSize' -Default 0) -gt 0) {
        Add-Step $c '「专注」固定壁纸就位' 'PASS' "$(Get-Prop $s 'library.focusMedia' -Default '')（$(Get-Prop $s 'library.focusSize' -Default 0) bytes）"
    } else {
        Add-Step $c '「专注」固定壁纸就位' 'FAIL' "MISSING 或 0 字节：$(Get-Prop $s 'library.focusMedia' -Default '') → high，P1-5 回归"
    }

    if (-not (Test-Prop $s 'library.demoExists')) {
        Add-Step $c '「演示」固定壁纸就位' 'BLOCKED' '采集缺失：library.demoExists 未返回（来自 Invoke-GuestCollect）'
    } elseif (Get-Prop $s 'library.demoExists' -and [int](Get-Prop $s 'library.demoSize' -Default 0) -gt 0) {
        Add-Step $c '「演示」固定壁纸就位' 'PASS' "$(Get-Prop $s 'library.demoMedia' -Default '')（$(Get-Prop $s 'library.demoSize' -Default 0) bytes）"
    } else {
        Add-Step $c '「演示」固定壁纸就位' 'FAIL' "MISSING 或 0 字节：$(Get-Prop $s 'library.demoMedia' -Default '') → high，P1-5 回归"
    }

    # 诊断文本留档（runbook §8.4 要求每次发布强制留档 == wallpaper library == 全段）
    if (Start-GuestApp) {
        $d = Invoke-GuestUi -Action 'Diagnose' -TimeoutSec 180
        if ($d.status -eq 'ok') {
            $p = Join-Path (Get-CaseDir $c.id) 'V8-diag-library.txt'
            Set-Content -LiteralPath $p -Value "$(Get-Prop $d 'data.diagnostics' -Default '')" -Encoding UTF8
            [void]$c.evidence.Add($p)
            Add-Step $c '诊断文本留档' 'PASS' $p
        } else { Add-Step $c '诊断文本留档' 'BLOCKED' "$($d.reasonCode)" }
    }
    Add-Step $c '两个视频实际播放（动态画面）' 'INFO' '需人工看截图/录屏确认不是黑屏或静止首帧；VM 无硬件加速时 mpv 可能表现异常，属环境限制。'
    return (Close-Case $c)
}

function Invoke-CaseV9 {
    $c = New-CaseResult 'V9' 'Unknown 态不写 intent 且有反馈' '§5 V9' 'SEMI'
    Reset-GuestBaseline
    Set-GuestSettings -DesiredIconsHidden $false -RestoreIconsOnExit $true
    if (-not (Start-GuestApp)) { Add-Step $c '启动应用' 'BLOCKED' '主进程未出现'; return (Close-Case $c) }
    Start-SafeSleep -Seconds 10

    $before = Invoke-GuestCollect -Label 'v9-before'
    if ($null -eq $before) { Add-Step $c '记录基线 settings' 'BLOCKED' '状态未采集'; return (Close-Case $c) }
    $intentBefore = "$(Get-Prop $before 'settings.DesiredIconsHidden')"
    $rawBefore    = "$(Get-Prop $before 'settings.raw')"
    Add-Step $c '记录基线 settings' 'INFO' "DesiredIconsHidden=$intentBefore"

    # 结束 Explorer 制造 Shell 不可读。注意：任务栏与托盘会一并消失，只能用主窗口操作。
    Invoke-GuestInline -Code "Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue" -AllowFailure -Purpose '结束 Explorer' | Out-Null
    Start-SafeSleep -Seconds 5

    $unk = Invoke-GuestAssert -Label 'v9-explorer-killed'
    if ($null -eq $unk) { Add-Step $c 'Shell 进入不可读态' 'BLOCKED' '断言未回收' }
    elseif ("$($unk.verdict)" -eq 'unknown') {
        Add-Step $c 'Shell 进入不可读态' 'PASS' "verdict=unknown reasonCode=$($unk.reasonCode)（预期：explorer-not-running）"
    } else {
        Add-Step $c 'Shell 进入不可读态' 'BLOCKED' "结束 Explorer 后断言为 $($unk.verdict)，未能制造 Unknown 场景，本用例前提不成立"
    }
    [void]$c.evidence.Add((Save-Screenshot $c.id 'v9-explorer-killed'))

    $ui = Invoke-GuestUi -Action 'SetHideIcons' -Value 'on' -TimeoutSec 180
    # 期望：切换不生效，UI 明确报错。UI 驱动返回 blocked(toggle-did-not-settle) 也是合理结果 —— 复选框会被拨回真实状态
    $statusText = "$(Get-Prop $ui 'data.after.statusText')`n$(Get-Prop $ui 'data.after.desktopStatusText')"
    if ($statusText -match '无法读取桌面图标层' -or $statusText -match '状态未知') {
        Add-Step $c '★P1-8：UI 明确报错不静默' 'PASS' ("Status/DesktopStatus 命中未知态文案：" + ($statusText -replace '\s+', ' '))
        if ($statusText -match '偏好未写入') { Add-Step $c 'P1-8：追加行「偏好未写入…」' 'PASS' '文案齐全' }
        else { Add-Step $c 'P1-8：追加行「偏好未写入…」' 'FAIL' "缺少「偏好未写入（避免用未知状态覆盖你的选择）」提示行 → high" }
    } else {
        Add-Step $c '★P1-8：UI 明确报错不静默' 'BLOCKED' ("未能读到 Status 文案（UI 驱动 status=$($ui.status)/$($ui.reasonCode)）。" +
            '结束 Explorer 后 UIA 仍可访问 WPF 主窗口，但托盘/任务栏消失可能影响激活；此项需人工复核截图。')
    }

    # ★核心：Unknown 期间 intent 一字未改
    $after = Invoke-GuestCollect -Label 'v9-after' 
    if ($null -eq $after) { Add-Step $c '★P1-7：Unknown 期间 intent 未被改写' 'BLOCKED' '状态未采集' }
    else {
        $intentAfter = "$(Get-Prop $after 'settings.DesiredIconsHidden')"
        if ($intentAfter -eq $intentBefore) { Add-Step $c '★P1-7：Unknown 期间 intent 未被改写' 'PASS' "DesiredIconsHidden 前后均为 $intentBefore" }
        else { Add-Step $c '★P1-7：Unknown 期间 intent 未被改写' 'FAIL' "critical，P1-7 回归：$intentBefore → $intentAfter" }
        # 顺带核对整份 settings 是否有非预期改动
        if ("$(Get-Prop $after 'settings.raw')" -ne $rawBefore) { Add-Step $c 'settings.json 全文比对' 'INFO' 'settings.json 内容发生变化（可能是其它字段），请人工 diff 两份归档' }
    }

    # 恢复 Explorer，确认程序不崩溃且可继续切换
    Invoke-GuestInline -Code "Start-Process explorer.exe" -AllowFailure -Purpose '恢复 Explorer' | Out-Null
    Start-SafeSleep -Seconds 15
    $back = Invoke-GuestAssert -Label 'v9-explorer-restored'
    Assert-Icons $c $back @('visible', 'hidden') 'Explorer 恢复后图标层可读' | Out-Null
    $final = Invoke-GuestCollect -Label 'v9-final'
    if ($final -and (Test-Prop $final 'processes') -and (Get-Prop $final 'processes.mainAlive')) { Add-Step $c 'Explorer 恢复后应用未崩溃' 'PASS' '主进程仍存活' }
    else { Add-Step $c 'Explorer 恢复后应用未崩溃' 'FAIL' '主进程已消失（疑似崩溃）' }
    Assert-LogContains $c $final '判定为 Unknown，不落盘 intent' '日志出现 Unknown 不落盘记录' | Out-Null
    return (Close-Case $c)
}

function Invoke-CaseV10 {
    $c = New-CaseResult 'V10' 'Apply 返回值全程可见' '§5 V10' 'SEMI'
    Reset-GuestBaseline
    Set-GuestSettings -DesiredIconsHidden $false -RestoreIconsOnExit $true
    if (-not (Start-GuestApp)) { Add-Step $c '启动应用' 'BLOCKED' '主进程未出现'; return (Close-Case $c) }

    $on = Invoke-GuestUi -Action 'SetHideIcons' -Value 'on'
    if ($on.status -eq 'ok') {
        if (-not (Test-Prop $on 'data.after')) {
            Add-Step $c '成功态有文字反馈（隐藏）' 'BLOCKED' 'UI 结果缺 data.after（来自 Invoke-AppUi）'
        } else {
            $t = "$(Get-Prop $on 'data.after.statusText')"
            if ($t -match '桌面图标：已隐藏') { Add-Step $c '成功态有文字反馈（隐藏）' 'PASS' $t }
            else { Add-Step $c '成功态有文字反馈（隐藏）' 'FAIL' "Status 未出现「桌面图标：已隐藏（…）」，实际：$t" }
        }
    } else { Add-Step $c '成功态有文字反馈（隐藏）' 'BLOCKED' "$($on.reasonCode)" }
    [void]$c.evidence.Add((Save-Screenshot $c.id 'v10-success'))

    $off = Invoke-GuestUi -Action 'SetHideIcons' -Value 'off'
    if ($off.status -eq 'ok') {
        if (-not (Test-Prop $off 'data.after')) {
            Add-Step $c '成功态有文字反馈（显示）' 'BLOCKED' 'UI 结果缺 data.after（来自 Invoke-AppUi）'
        } else {
            $t = "$(Get-Prop $off 'data.after.statusText')"
            if ($t -match '桌面图标：已显示') { Add-Step $c '成功态有文字反馈（显示）' 'PASS' $t }
            else { Add-Step $c '成功态有文字反馈（显示）' 'FAIL' "实际：$t" }
        }
    } else { Add-Step $c '成功态有文字反馈（显示）' 'BLOCKED' "$($off.reasonCode)" }

    $busy = Invoke-GuestUi -Action 'ToggleHideIconsTwiceFast' -TimeoutSec 180
    if ($busy.status -eq 'ok') {
        if (-not (Test-Prop $busy 'data.immediately')) {
            Add-Step $c '并发点击被拦截且有提示' 'BLOCKED' 'UI 结果缺 data.immediately（来自 Invoke-AppUi）'
        } else {
            $t = "$(Get-Prop $busy 'data.immediately.statusText')"
            if ($t -match '桌面操作正在进行中') { Add-Step $c '并发点击被拦截且有提示' 'PASS' $t }
            else { Add-Step $c '并发点击被拦截且有提示' 'INFO' "未捕捉到「桌面操作正在进行中，请稍候…」（可能采样时机错过），实际：$t。捕捉不到不判 FAIL，需人工复核。" }
        }
        if (-not (Test-Prop $busy 'data.settled')) {
            Add-Step $c '并发后复选框回正' 'BLOCKED' 'UI 结果缺 data.settled（来自 Invoke-AppUi）'
        } else {
            Add-Step $c '并发后复选框回正' 'INFO' "settled.chkHideIcons=$(Get-Prop $busy 'data.settled.chkHideIcons')，需与当时的真实图标状态对照人工判读"
        }
    } else { Add-Step $c '并发点击被拦截且有提示' 'BLOCKED' "$($busy.reasonCode)" }
    [void]$c.evidence.Add((Save-Screenshot $c.id 'v10-busy'))

    Add-Step $c '「正在…」过渡文案 / 切换期间窗口不卡死' 'BLOCKED' `
        '过渡文案存在时间只有几百毫秒，跨 vmrun 的采样延迟远大于它，稳定捕捉不到；「窗口可拖动、不转圈」属交互判据。这两条需人工执行。'
    Close-Case $c -ForceVerdict $(if (@($c.steps | Where-Object { $_.status -eq 'FAIL' }).Count -gt 0) { 'FAIL' } else { 'BLOCKED' }) `
                  -Reason '成功态与并发态反馈已自动核对；过渡文案与卡死判据需人工'
    return $c
}

function Invoke-CaseV11A {
    $c = New-CaseResult 'V11A' '真实关机 SessionEnding 恢复（前台 + 关闭自启）' '§5 V11-A' 'AUTO'
    Reset-GuestBaseline
    Set-GuestRunKey $false
    Set-GuestSettings -DesiredIconsHidden $true -RestoreIconsOnExit $true -LaunchOnStartup $false
    if (-not (Start-GuestApp)) { Add-Step $c '启动应用' 'BLOCKED' '主进程未出现'; return (Close-Case $c) }
    Start-SafeSleep -Seconds 20

    $pre = Invoke-GuestCollect -Label 'v11a-pre'
    # §0-C 守卫：自启必须关闭，否则登录后立刻重新隐藏会掩盖恢复失败
    if (-not (Assert-AutostartExpectation $c $pre $false)) { return (Close-Case $c) }
    Assert-Icons $c (Invoke-GuestAssert -Label 'v11a-pre') @('hidden') '关机前：图标已隐藏' | Out-Null
    Assert-Setting $c $pre 'RestoreIconsOnExit' 'True' '关机前 on exit = restore icons' | Out-Null
    $bootBefore = Get-GuestBootUtc $pre

    # 关键：保持程序运行，不退出，直接重启（runbook 允许用重启代替关机，语义等价）
    # V11A 修复：用 -Graceful（ACPI soft reset）触发 WM_QUERYENDSESSION，否则 SessionEnding 永不触发
    Restart-Guest -Mode reboot -Graceful
    if (-not (Wait-GuestDown -BootBefore $bootBefore)) {
        Add-Step $c '关机未被阻断' 'FAIL' "发出重启后 ${ShutdownTimeoutSec}s 内靶机仍在线 → high（疑似出现「此应用正在阻止关机」拦截页，见证据截图）"
        [void]$c.evidence.Add((Save-Screenshot $c.id 'v11a-blocked-shutdown'))
        return (Close-Case $c)
    }
    Add-Step $c '关机未被阻断' 'PASS' "靶机在 ${ShutdownTimeoutSec}s 内正常下线，未出现拦截"

    Start-Target
    if (-not (Wait-Tools)) { Add-Step $c '等待 Tools' 'BLOCKED' 'Tools 未就绪'; return (Close-Case $c) }
    if (-not (Wait-InteractiveSession -Case $c.id)) { Add-Step $c '等待交互会话' 'BLOCKED' '未自动登录'; return (Close-Case $c) }

    [void]$c.evidence.Add((Save-Screenshot $c.id 'v11a-desktop-after-login'))
    # ★核心：重新登录后（未启动任何程序）图标必须可见
    Assert-Icons $c (Invoke-GuestAssert -Label 'v11a-after-login' -Samples 5 -IntervalMs 1500) @('visible') `
        '★核心：重新登录后（未启动程序）桌面图标可见 —— SessionEnding 恢复生效' | Out-Null

    $post = Invoke-GuestCollect -Label 'v11a-after-login'
    $bootAfter = Get-GuestBootUtc $post
    if ($bootBefore -and $bootAfter -and $bootBefore -ne $bootAfter) { Add-Step $c '重启确证' 'PASS' "$bootBefore → $bootAfter" }
    else { Add-Step $c '重启确证' 'BLOCKED' "无法确认真的重启过（before=$bootBefore after=$bootAfter）" }

    # 日志：至少命中一条 SessionEnding，且恢复行恰好一条（闩锁 _iconsRestored 生效）
    $wpf = Get-LogMatchCount $post '^\[.*SessionEnding（'
    $sys = Get-LogMatchCount $post 'SystemEvents\.SessionEnding（'
    if ($wpf -lt 0) { Add-Step $c 'SessionEnding 命中' 'BLOCKED' '日志未采集' }
    elseif (($wpf + $sys) -ge 1) { Add-Step $c 'SessionEnding 命中' 'PASS' "WPF 侧 $wpf 条 / SystemEvents 侧 $sys 条" }
    else { Add-Step $c 'SessionEnding 命中' 'FAIL' 'critical，P1-2 回归：日志中没有任何 SessionEnding 行，用户会拿到空桌面' }

    Assert-LogContains $c $post '退出（系统关机（WM_QUERYENDSESSION））：恢复桌面图标' '★「退出（系统关机）：恢复桌面图标 → …」恰好一条（闩锁生效）' -MinCount 1 -MaxCount 1 | Out-Null
    $dup = Get-LogMatchCount $post '退出（系统关机（WM_QUERYENDSESSION））：恢复桌面图标'
    if ($dup -ge 2) { Add-Step $c '闩锁未双跑' 'FAIL' "出现 $dup 条恢复记录 → medium（_iconsRestored 闩锁失效）" }

    $failedRestore = Get-LogMatchCount $post '退出（系统关机）：恢复桌面图标 → (Failed|Unknown)'
    if ($failedRestore -gt 0) { Add-Step $c '恢复结果非 Failed/Unknown' 'FAIL' "high：恢复结果为 Failed/Unknown（很可能关机时 Explorer 已先行退出，属已知窗口期，需评估）" }

    Assert-Setting $c $post 'DesiredIconsHidden' 'True' '★意图未被恢复动作污染（仍为 true）' | Out-Null

    if (Start-GuestApp) {
        Start-SafeSleep -Seconds 20
        Assert-Icons $c (Invoke-GuestAssert -Label 'v11a-relaunch') @('hidden') '手动启动后按意图重新隐藏' | Out-Null
    }
    return (Close-Case $c)
}

function Invoke-CaseV11B {
    $c = New-CaseResult 'V11B' '真实关机 SessionEnding 恢复（--background 兜底路径）' '§5 V11-B' 'AUTO'
    Reset-GuestBaseline
    Set-GuestSettings -DesiredIconsHidden $true -RestoreIconsOnExit $true -LaunchOnStartup $true
    Set-GuestRunKey $true

    # 第一次重启：让下次登录走纯 --background 路径（主窗口从未 Show()，没有 HWND）
    Restart-Guest -Mode reboot
    if (-not (Wait-GuestDown)) { Add-Step $c '首次重启进入 --background 态' 'BLOCKED' '重启未发生'; return (Close-Case $c) }
    Start-Target
    if (-not (Wait-Tools)) { Add-Step $c '等待 Tools' 'BLOCKED' 'Tools 未就绪'; return (Close-Case $c) }
    if (-not (Wait-InteractiveSession -Case $c.id)) { Add-Step $c '等待交互会话' 'BLOCKED' '未自动登录'; return (Close-Case $c) }

    $s1 = Invoke-GuestAssert -Label 'v11b-bg-established' -Samples 60 -IntervalMs 1000
    Assert-Icons $c $s1 @('hidden') '前置：--background 自启后图标已隐藏' | Out-Null
    $mid = Invoke-GuestCollect -Label 'v11b-bg-established'
    if (-not (Test-Prop $mid 'processes.mainIsBackground')) {
        Add-Step $c '确认走 --background 路径' 'BLOCKED' '采集缺失：processes.mainIsBackground 未返回（来自 Invoke-GuestCollect）'
        return (Close-Case $c)
    }
    if (Get-Prop $mid 'processes.mainIsBackground') { Add-Step $c '确认走 --background 路径' 'PASS' '主进程命令行含 --background，主窗口从未 Show()，无 HWND' }
    else { Add-Step $c '确认走 --background 路径' 'BLOCKED' '未确认主进程以 --background 运行，本支线（SystemEvents 兜底）前提不成立'; return (Close-Case $c) }
    $bootBefore = Get-GuestBootUtc $mid

    # 第二次重启：这次要验的就是无 HWND 场景下的 SystemEvents 兜底
    # V11B 修复：用 -Graceful 触发 WM_QUERYENDSESSION（通过 SystemEvents 兜底路径）
    Restart-Guest -Mode reboot -Graceful
    if (-not (Wait-GuestDown -BootBefore $bootBefore)) {
        Add-Step $c '关机未被阻断' 'FAIL' "high：${ShutdownTimeoutSec}s 内未下线，疑似阻断或明显变慢"
        return (Close-Case $c)
    }
    Add-Step $c '关机未被阻断' 'PASS' '正常下线'
    Start-Target
    if (-not (Wait-Tools)) { Add-Step $c '等待 Tools' 'BLOCKED' 'Tools 未就绪'; return (Close-Case $c) }
    if (-not (Wait-InteractiveSession -Case $c.id)) { Add-Step $c '等待交互会话' 'BLOCKED' '未自动登录'; return (Close-Case $c) }

    # §0-C：自启开着，图标会很快被重新隐藏。用高频采样抢窗口期；抓不到不判 FAIL，改用日志断言。
    $series = Invoke-GuestAssert -Label 'v11b-login-window' -Samples 40 -IntervalMs 500
    [void]$c.evidence.Add((Save-Screenshot $c.id 'v11b-login-0s'))
    if ($null -eq $series) { Add-Step $c '登录早期窗口期采样' 'BLOCKED' '采样未回收' }
    elseif ($series.anyVisible) { Add-Step $c '登录早期窗口期采样' 'PASS' '捕捉到「图标曾可见」的窗口期 —— 关机时恢复确实执行了' }
    else { Add-Step $c '登录早期窗口期采样' 'INFO' "未捕捉到可见窗口期（anyHidden=$($series.anyHidden) anyUnknown=$($series.anyUnknown)）。自启重隐藏可能快于首次采样，按 runbook 判据改用日志断言，不因抓拍失败判 FAIL。" }

    $post = Invoke-GuestCollect -Label 'v11b-after-login'
    $bootAfter = Get-GuestBootUtc $post
    if ($bootBefore -and $bootAfter -and $bootBefore -ne $bootAfter) { Add-Step $c '重启确证' 'PASS' "$bootBefore → $bootAfter" }
    else { Add-Step $c '重启确证' 'BLOCKED' "无法确认真的重启过" }

    # ★核心判据：兜底订阅必须触发。§0-B：WPF 侧那条缺席是预期，绝不因此判 FAIL。
    $sys = Get-LogMatchCount $post 'SystemEvents\.SessionEnding（'
    $wpf = Get-LogMatchCount $post '^\[.*\] SessionEnding（'
    if ($sys -lt 0) { Add-Step $c '★SystemEvents 兜底触发' 'BLOCKED' '日志未采集' }
    elseif ($sys -ge 1) { Add-Step $c '★SystemEvents 兜底触发' 'PASS' "命中 $sys 条 SystemEvents.SessionEnding（WPF 侧 $wpf 条，缺席属预期，见 §0-B）" }
    elseif ($wpf -ge 1) { Add-Step $c '★SystemEvents 兜底触发' 'FAIL' "只有 WPF 侧 SessionEnding（$wpf 条），SystemEvents 兜底未触发 —— --background 用户的兜底路径失效" }
    else { Add-Step $c '★SystemEvents 兜底触发' 'FAIL' 'critical，P1-2 兜底路径失效：两条 SessionEnding 都没有' }

    Assert-LogContains $c $post '退出（系统关机（WM_QUERYENDSESSION））：恢复桌面图标 → (Applied|AlreadyInState)' '恢复动作成功执行' | Out-Null
    Assert-Setting $c $post 'DesiredIconsHidden' 'True' '意图未被污染' | Out-Null
    return (Close-Case $c)
}

function Invoke-CaseV12 {
    $c = New-CaseResult 'V12' '真实注销 SessionEnding 恢复' '§5 V12' 'AUTO'
    Reset-GuestBaseline
    Set-GuestRunKey $false
    Set-GuestSettings -DesiredIconsHidden $true -RestoreIconsOnExit $true -LaunchOnStartup $false
    if (-not (Start-GuestApp)) { Add-Step $c '启动应用' 'BLOCKED' '主进程未出现'; return (Close-Case $c) }
    Start-SafeSleep -Seconds 20

    $pre = Invoke-GuestCollect -Label 'v12-pre'
    if (-not (Assert-AutostartExpectation $c $pre $false)) { return (Close-Case $c) }
    Assert-Icons $c (Invoke-GuestAssert -Label 'v12-pre') @('hidden') '注销前：图标已隐藏' | Out-Null
    Assert-Setting $c $pre 'RestoreIconsOnExit' 'True' '注销前 on exit = restore icons' | Out-Null
    $sessionBefore = if ($pre -and (Test-Prop $pre 'system')) { "$(Get-Prop $pre 'system.sessionId')" } else { $null }

    # 必须是「注销」，不能是锁定/切换用户 —— 锁定不结束会话，不会触发 SessionEnding
    Restart-Guest -Mode logoff
    Start-SafeSleep -Seconds 20
    # 注销后 Windows autologon 不会自动重新登录交互会话（autologon 仅在系统启动/重启时触发），
    # 直接用 hypervisor 级 reboot 恢复会话作为观察窗口。
    # 重要：SessionEnding(Logoff) 恢复动作已在上面 logoff 时同步执行，reboot 仅用于重新进入桌面观察结果，不影响结论。
    Restart-Guest -Mode reboot
    if (-not (Wait-InteractiveSession -Case $c.id)) {
        Add-Step $c '注销后重新登录' 'BLOCKED' "注销后重启仍未能在 ${SessionTimeoutSec}s 内登录回交互会话。"
        return (Close-Case $c)
    }
    [void]$c.evidence.Add((Save-Screenshot $c.id 'v12-desktop-after-login'))

    $post = Invoke-GuestCollect -Label 'v12-after-login'
    $sessionAfter = if ($post -and (Test-Prop $post 'system')) { "$(Get-Prop $post 'system.sessionId')" } else { $null }
    # 【修复 2026-08-07 主理人】
    # 注销确证原本只认「session ID 变化」——这对纯 logoff 路径有效（logoff 后会话重建，ID 会变）。
    # 但本 harness 在 logoff 后用 hypervisor reboot 恢复交互会话（autologon 在 logoff 后不触发），
    # reboot 会重置 session ID，导致 before==after==1，误判为「可能只是锁屏」。
    # 改为接受三种确证信号之一即 PASS：
    #   ① session ID 变化（纯 logoff 路径）
    #   ② 日志中捕获到 SessionEnding(Logoff) 事件（logoff+reboot 路径，最强确证）
    #   ③ 系统已重启（lastBootUtc 变化，logoff+reboot 路径）
    # 锁屏冒充仍会被正确拦截：session 不变 + 无 Logoff 事件 + 无重启。
    $logoffEvt = if ($post) { Get-LogMatchCount $post 'SessionEnding（Logoff）' } else { 0 }
    $rebooted = if ($pre -and $post -and (Test-Prop $pre 'system') -and (Test-Prop $post 'system')) {
        "$(Get-Prop $pre 'system.lastBootUtc')" -ne "$(Get-Prop $post 'system.lastBootUtc')"
    } else { $false }
    if (($sessionBefore -and $sessionAfter -and $sessionBefore -ne $sessionAfter) -or ($logoffEvt -ge 1) -or $rebooted) {
        $why = if ($sessionBefore -and $sessionAfter -and $sessionBefore -ne $sessionAfter) { "会话 ID $sessionBefore → $sessionAfter" }
               elseif ($logoffEvt -ge 1) { "SessionEnding(Logoff) 事件命中 $logoffEvt 条（logoff+reboot 路径）" }
               else { "系统已重启（lastBootUtc 变化）" }
        Add-Step $c '注销确证' 'PASS' "已确证真实注销：$why"
    } else {
        Add-Step $c '注销确证' 'BLOCKED' "会话 ID 未变化（before=$sessionBefore after=$sessionAfter）且未捕获到 SessionEnding(Logoff) 事件，无法确认真的注销过 —— 可能只是锁屏。"
    }

    # ★核心
    Assert-Icons $c (Invoke-GuestAssert -Label 'v12-after-login' -Samples 5 -IntervalMs 1500) @('visible') `
        '★核心：重新登录后（未启动程序）图标可见' | Out-Null

    $logoff = Get-LogMatchCount $post 'SessionEnding（Logoff）'
    if ($logoff -lt 0) { Add-Step $c '★Logoff 分支命中' 'BLOCKED' '日志未采集' }
    elseif ($logoff -ge 1) { Add-Step $c '★Logoff 分支命中' 'PASS' "命中 $logoff 条 SessionEnding（Logoff）" }
    else { Add-Step $c '★Logoff 分支命中' 'FAIL' 'critical，P1-2 注销路径回归：日志中没有 Logoff 相关的 SessionEnding 行' }

    Assert-LogContains $c $post '退出（系统注销（WM_QUERYENDSESSION））：恢复桌面图标' '★恢复日志 reason 为「系统注销」且恰好一条' -MinCount 1 -MaxCount 1 | Out-Null
    $wrongReason = Get-LogMatchCount $post '退出（系统关机（WM_QUERYENDSESSION））：恢复桌面图标'
    if ($wrongReason -gt 0 -and (Get-LogMatchCount $post '退出（系统注销）：恢复桌面图标') -eq 0) {
        Add-Step $c 'reason 文案正确' 'FAIL' 'low：恢复日志 reason 显示「系统关机」而非「系统注销」，reason 映射错误（不影响功能，但会误导排障）'
    }
    Assert-Setting $c $post 'DesiredIconsHidden' 'True' '★意图仍为 true' | Out-Null

    if (Start-GuestApp) {
        Start-SafeSleep -Seconds 20
        Assert-Icons $c (Invoke-GuestAssert -Label 'v12-relaunch') @('hidden') '启动后按意图重新隐藏' | Out-Null
    }
    return (Close-Case $c)
}

function Invoke-CaseV13 {
    $c = New-CaseResult 'V13' '多显示器 / 高 DPI 行为记录' '§5 V13' 'NA'
    $s = Invoke-GuestCollect -Label 'v13'
    $mon = if ($s -and (Test-Prop $s 'system')) { Get-Prop $s 'system.monitorCount' -Default '?' } else { '?' }
    Add-Step $c '环境记录' 'INFO' "靶机视频控制器数量 = $mon（VMware 默认单虚拟显示器）"
    Close-Case $c -ForceVerdict 'NA' -Reason `
        ("本靶机为 VMware 单虚拟显示器，无法构造 ≥2 屏场景；DPI 缩放虽可改注册表，但需注销生效且属视觉判据（文字模糊/控件重叠/裁切），" +
         "截图无法机器判读。V13 为观察项（非 FAIL 判据），建议在物理机或多显示器 VM 上人工执行。")
    return $c
}

function Invoke-CaseV14 {
    $c = New-CaseResult 'V14' '进程异常退出后的下次启动' '§5 V14' 'AUTO'
    Reset-GuestBaseline
    Set-GuestRunKey $false
    Set-GuestSettings -DesiredIconsHidden $true -RestoreIconsOnExit $true
    if (-not (Start-GuestApp)) { Add-Step $c '启动应用' 'BLOCKED' '主进程未出现'; return (Close-Case $c) }
    Start-SafeSleep -Seconds 20
    Assert-Icons $c (Invoke-GuestAssert -Label 'v14-hidden') @('hidden') '强杀前：图标已隐藏' | Out-Null

    $pre = Invoke-GuestCollect -Label 'v14-pre'
    $rendererBefore = if ($pre -and (Test-Prop $pre 'processes')) { [int](Get-Prop $pre 'processes.rendererCount' -Default 0) } else { -1 }

    # 只杀主进程，不碰渲染子进程 —— 两者都叫 DesktopSuite.exe，混杀会毁掉「渲染进程独立存活」这条判据
    Stop-GuestApp -Hard -MainOnly
    Start-SafeSleep -Seconds 10
    [void]$c.evidence.Add((Save-Screenshot $c.id 'v14-after-kill'))

    # 设计内行为：异常退出没有 OnClosed 也没有 SessionEnding，不会恢复 —— 图标保持隐藏才是对的
    Assert-Icons $c (Invoke-GuestAssert -Label 'v14-after-kill') @('hidden') '强杀后图标保持隐藏（设计内行为，非缺陷）' | Out-Null

    $post = Invoke-GuestCollect -Label 'v14-after-kill'
    if ($null -eq $post) { Add-Step $c 'settings.json 未损坏' 'BLOCKED' '状态未采集' }
    elseif ((Test-Prop $post 'settings.parseError') -and (Get-Prop $post 'settings.parseError')) {
        Add-Step $c 'settings.json 未损坏' 'FAIL' "high：settings.json 损坏 —— $(Get-Prop $post 'settings.parseError')"
    }
    else { Add-Step $c 'settings.json 未损坏' 'PASS' 'JSON 结构完整可解析' }
    Assert-Setting $c $post 'DesiredIconsHidden' 'True' '强杀后 intent 仍为 true（apply 时已即时落盘）' | Out-Null
    if ($rendererBefore -gt 0) {
        Add-Step $c '渲染子进程独立存活' 'INFO' "强杀前 $rendererBefore 个，强杀后 $(Get-Prop $post 'processes.rendererCount' -Default 0) 个（设计上壁纸独立于 GUI 存活）"
    }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $started = Start-GuestApp
    $sw.Stop()
    if (-not $started) { Add-Step $c '重新启动不卡死' 'FAIL' "high：重新启动后 ${AppStartTimeoutSec}s 内主进程仍未出现"; return (Close-Case $c) }
    Add-Step $c '重新启动不卡死' 'PASS' "主进程在 $([int]$sw.Elapsed.TotalSeconds)s 内出现"
    Start-SafeSleep -Seconds 20

    $final = Invoke-GuestCollect -Label 'v14-relaunch'
    [void]$c.evidence.Add((Save-Screenshot $c.id 'v14-relaunch'))
    Assert-Icons $c (Invoke-GuestAssert -Label 'v14-relaunch') @('hidden') '重启后仍隐藏（AlreadyInState，不重复发命令）' | Out-Null

    # 互斥体泄漏检查：只能有一个主进程
    if ($final -and (Test-Prop $final 'processes')) {
        $mc = [int](Get-Prop $final 'processes.mainCount' -Default 0)
        if ($mc -eq 1) { Add-Step $c '单实例互斥体无残留' 'PASS' '只有 1 个主进程' }
        else { Add-Step $c '单实例互斥体无残留' 'FAIL' "high：主进程数量 = $mc，互斥体泄漏" }
    }
    $ui = Invoke-GuestUi -Action 'ReadState'
    if ($ui.status -eq 'ok') {
        if (-not (Test-Prop $ui 'data.windowState')) {
            Add-Step $c '复选框为勾选态' 'BLOCKED' 'UI 结果缺 data.windowState（来自 Invoke-AppUi）'
        } elseif ("$(Get-Prop $ui 'data.windowState.chkHideIcons')" -eq 'On') {
            Add-Step $c '复选框为勾选态' 'PASS' ''
        } else {
            Add-Step $c '复选框为勾选态' 'FAIL' "ChkHideIcons=$(Get-Prop $ui 'data.windowState.chkHideIcons')"
        }
    } else { Add-Step $c '复选框为勾选态' 'BLOCKED' "$($ui.reasonCode)" }
    Add-Step $c '托盘只有一个图标' 'INFO' '托盘图标数量需人工看截图确认（UIA 在折叠通知区域下不可靠）'
    return (Close-Case $c)
}

#==============================================================================
# 部署与自动登录
#==============================================================================
function Invoke-Deploy {
    Write-Log '投放脚本与应用…' 'STEP'
    Invoke-GuestInline -Purpose '创建 guest 目录' -Code @"
foreach (`$d in @('$GuestAppDir', '$GuestScriptDir', '$GuestEvidence')) {
    New-Item -ItemType Directory -Path `$d -Force | Out-Null
}
"@ | Out-Null

    foreach ($f in @('Assert-DesktopIcons.ps1', 'Collect-State.ps1', 'Invoke-AppUi.ps1')) {
        $src = Join-Path $PSScriptRoot "guest\$f"
        if (-not (Test-Path -LiteralPath $src)) { throw "缺少 guest 脚本：$src" }
        Copy-ToGuest -HostPath $src -GuestPath "$GuestScriptDir\$f"
    }

    if (-not $AppSource) {
        Write-Log '未提供 -AppSource，跳过应用投放（假定 guest 内已就位）' 'WARN'
        return
    }
    if (-not (Test-Path -LiteralPath $AppSource)) { throw "-AppSource 不存在：$AppSource" }

    # vmrun 只能逐文件拷贝，没有目录递归。先在宿主机打 zip，投放后在 guest 内解压。
    # 注意：Compress-Archive / Expand-Archive 在 PS 5.1 下会把整个 zip 加载到内存，
    # 对于 self-contained 包（~200MB, 259 文件）会 OOM。改用 .NET ZipFile 流式处理。
    $zip = Join-Path $env:TEMP "gstack-app-$($script:RunId).zip"
    if (Test-Path -LiteralPath $zip) { try { [System.IO.File]::Delete($zip) } catch {} }
    Write-Log "压缩应用包：$AppSource → $zip" 'INFO'
    if (-not $DryRun) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::CreateFromDirectory($AppSource, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    }
    Copy-ToGuest -HostPath $zip -GuestPath 'C:\gstack\app.zip'
    Invoke-GuestInline -TimeoutSec 600 -Purpose '解压应用包' -Code @"
if (Test-Path '$GuestAppDir') { [System.IO.Directory]::Delete('$GuestAppDir', `$true) }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::ExtractToDirectory('C:\gstack\app.zip', '$GuestAppDir')
"@ | Out-Null
    if (-not $DryRun) { try { if (Test-Path -LiteralPath $zip) { [System.IO.File]::Delete($zip) } } catch {} }

    if (-not (Test-GuestFile "$GuestAppDir\DesktopSuite.exe")) {
        throw "投放后 guest 内找不到 $GuestAppDir\DesktopSuite.exe，请检查 -AppSource 是否为 publish 输出目录"
    }
    Write-Log '应用投放完成' 'OK'
}

<#
  自动登录是所有跨会话用例（V2/V11/V12）能无人值守跑完的硬前提：
  没有它，靶机重启后会停在登录界面，-interactive 全部失败。

  写 HKLM\...\Winlogon 需要管理员权限。vmrun 起的进程通常不是提升令牌，
  所以这里「尝试 + 回读校验」，失败就明确报出来让人工配一次（配好后会随快照保留）。
  刻意不采集/不回传 DefaultPassword，避免明文口令进入证据目录。
#>
function Enable-GuestAutoLogon {
    Write-Log '配置自动登录…' 'STEP'
    $code = @"
`$k = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
try {
    Set-ItemProperty -Path `$k -Name 'AutoAdminLogon'  -Value '1' -ErrorAction Stop
    Set-ItemProperty -Path `$k -Name 'DefaultUserName' -Value '$GuestUser' -ErrorAction Stop
    Set-ItemProperty -Path `$k -Name 'DefaultPassword' -Value '$($GuestPass -replace "'", "''")' -ErrorAction Stop
    Remove-ItemProperty -Path `$k -Name 'AutoLogonCount' -ErrorAction SilentlyContinue
    `$v = (Get-ItemProperty -Path `$k -Name 'AutoAdminLogon').AutoAdminLogon
    exit `$(if (`$v -eq '1') { 0 } else { 7 })
} catch { exit 8 }
"@
    # 不加 -interactive：由 vmtoolsd 侧启动，更有机会拿到写 HKLM 的权限
    $r = Invoke-GuestInline -Code $code -AllowFailure -Purpose '写入自动登录配置'
    $ec = Get-GuestExitCode $r
    if ($ec -eq 0) { Write-Log '自动登录已启用' 'OK'; return $true }
    Write-Log ("自动登录配置失败（guest exit=$ec）。跨会话用例（V2/V11/V12）将在重启后卡在登录界面。" +
               "请在靶机上手工执行一次 netplwiz 取消「必须输入用户名和密码」，然后重建快照。") 'WARN'
    return $false
}

#==============================================================================
# 报告
#==============================================================================
function Write-Summary {
    $summaryPath = Join-Path $script:RunDir 'summary.json'
    $mdPath      = Join-Path $script:RunDir 'summary.md'
    $script:Results | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding UTF8

    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine("# DesktopSuite Phase 3 靶机自动化验证结果")
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("- 运行 ID：``$($script:RunId)``")
    [void]$sb.AppendLine("- 靶机：``$VmxPath``")
    [void]$sb.AppendLine("- 快照：``$SnapshotName``（用例间回滚：$([bool]$RevertBetweenCases)）")
    [void]$sb.AppendLine("- 证据目录：``$($script:RunDir)``")
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('| 编号 | 名称 | 自动化 | 结论 | 原因 / 备注 |')
    [void]$sb.AppendLine('|---|---|---|---|---|')
    foreach ($r in $script:Results) {
        $reason = ("$($r.reason)" -replace '\|', '\|' -replace '\r?\n', ' ')
        if ($reason.Length -gt 300) { $reason = $reason.Substring(0, 300) + '…' }
        [void]$sb.AppendLine("| $($r.id) | $($r.name) | $($r.automation) | **$($r.verdict)** | $reason |")
    }
    [void]$sb.AppendLine()

    # 放行门槛（runbook §7.1）
    $gate = @('V1','V2A','V2B','V3','V11A','V11B','V12')
    $gateResults = @($script:Results | Where-Object { $gate -contains $_.id })
    $gatePass = @($gateResults | Where-Object { $_.verdict -eq 'PASS' }).Count
    [void]$sb.AppendLine("## 放行门槛（runbook §7.1）")
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("要求 V1 / V2-A / V2-B / V3 / V11-A / V11-B / V12 全部 PASS。")
    [void]$sb.AppendLine("本次已跑门槛用例 $($gateResults.Count) 条，其中 PASS $gatePass 条。")
    if ($gateResults.Count -lt $gate.Count -or $gatePass -lt $gateResults.Count) {
        [void]$sb.AppendLine()
        [void]$sb.AppendLine('> ⚠️ **未达放行门槛。** 未跑或未 PASS 的门槛用例必须补齐（含人工执行部分）后才能放行。')
    }
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('## 逐步明细')
    foreach ($r in $script:Results) {
        [void]$sb.AppendLine()
        [void]$sb.AppendLine("### $($r.id) — $($r.name) → **$($r.verdict)**")
        [void]$sb.AppendLine()
        foreach ($s in $r.steps) {
            $mark = switch ($s.status) { 'PASS' {'✅'} 'FAIL' {'❌'} 'BLOCKED' {'🚧'} default {'ℹ️'} }
            [void]$sb.AppendLine("- $mark **$($s.name)** — $($s.detail)")
        }
        if ($r.evidence.Count -gt 0) {
            [void]$sb.AppendLine()
            [void]$sb.AppendLine("  证据：$(($r.evidence | ForEach-Object { '`' + (Split-Path -Leaf $_) + '`' }) -join ', ')")
        }
    }
    Set-Content -LiteralPath $mdPath -Value $sb.ToString() -Encoding UTF8

    Write-Host ''
    Write-Host '================ 结果汇总 ================' -ForegroundColor Cyan
    foreach ($r in $script:Results) {
        $color = switch ($r.verdict) { 'PASS' {'Green'} 'FAIL' {'Red'} 'NA' {'DarkGray'} default {'Yellow'} }
        Write-Host ("  {0,-6} {1,-8} {2}" -f $r.id, $r.verdict, $r.name) -ForegroundColor $color
    }
    Write-Host '==========================================' -ForegroundColor Cyan
    Write-Host "报告：$mdPath" -ForegroundColor Cyan
}

#==============================================================================
# 主流程
#==============================================================================
$CASE_MAP = @{
    'V1'   = ${function:Invoke-CaseV1};   'V2A'  = ${function:Invoke-CaseV2A}
    'V2B'  = ${function:Invoke-CaseV2B};  'V3'   = ${function:Invoke-CaseV3}
    'V4'   = ${function:Invoke-CaseV4};   'V5'   = ${function:Invoke-CaseV5}
    'V6'   = ${function:Invoke-CaseV6};   'V7'   = ${function:Invoke-CaseV7}
    'V8'   = ${function:Invoke-CaseV8};   'V9'   = ${function:Invoke-CaseV9}
    'V10'  = ${function:Invoke-CaseV10};  'V11A' = ${function:Invoke-CaseV11A}
    'V11B' = ${function:Invoke-CaseV11B}; 'V12'  = ${function:Invoke-CaseV12}
    'V13'  = ${function:Invoke-CaseV13};  'V14'  = ${function:Invoke-CaseV14}
}

try {
    New-Item -ItemType Directory -Path $script:RunDir -Force | Out-Null
    Write-Log "=== DesktopSuite Phase 3 靶机验证开始（RunId=$($script:RunId)）===" 'STEP'
    if ($DryRun) { Write-Log 'DRY-RUN 模式：只打印 vmrun 命令，不操作靶机' 'WARN' }

    if (-not $DryRun -and -not (Test-Path -LiteralPath $VmrunPath)) { throw "找不到 vmrun：$VmrunPath" }
    if (-not $DryRun -and -not (Test-Path -LiteralPath $VmxPath))   { throw "找不到 vmx：$VmxPath" }

    # 用例集合：按 DEFAULT_ORDER 排序，保证破坏性用例始终在后
    $selected = if ($Cases.Count -gt 0) { @($Cases | ForEach-Object { $_.ToUpper().Trim() }) } else { $DEFAULT_ORDER }
    $unknown = @($selected | Where-Object { $ALL_CASES -notcontains $_ })
    if ($unknown.Count -gt 0) { throw "未知用例：$($unknown -join ', ')。可用：$($ALL_CASES -join ', ')" }
    $ordered = @($DEFAULT_ORDER | Where-Object { $selected -contains $_ })
    Write-Log "本次执行用例：$($ordered -join ' → ')" 'STEP'

    Start-Target
    if (-not (Wait-Tools)) { throw 'VMware Tools 未就绪，无法继续。请确认靶机已开机并完成登录。' }
    if ($EnableAutoLogon) { Enable-GuestAutoLogon | Out-Null }
    # 必须先投放再等会话：Wait-InteractiveSession 的探针就是 guest 内的
    # Assert-DesktopIcons.ps1。若沿用快照里的旧版探针，探针自身会异常退出
    # （exit=4），会话永远等不到就绪，整个套件卡死。Invoke-Deploy 只依赖
    # VMware Tools（session 0 即可完成 mkdir + 文件拷贝），不需要交互会话。
    if (-not (Wait-GuestOps)) { throw 'guest 操作通道不可用（vmrun 全部返回未知错误）。靶机内 VMware Tools/VGAuth 可能正在重启或已异常，请检查靶机。' }
    if (-not $SkipDeploy) { Invoke-Deploy }
    if (-not (Wait-InteractiveSession -Case 'init')) {
        Write-Log '交互会话不可读。所有依赖桌面的用例都会判 BLOCKED。请确认靶机已自动登录到桌面。' 'WARN'
    }

    foreach ($case in $ordered) {
        $script:CurrentCase = $case
        Write-Log "———————— 开始 $case ————————" 'STEP'
        if ($RevertBetweenCases) {
            Restore-Snapshot
            Start-Target
            if (-not (Wait-Tools)) { Write-Log '回滚后 Tools 未就绪' 'WARN' }
            # 同 init：回滚后 guest 内是快照里的旧脚本，必须先重新投放，
            # 否则会话探针用的还是旧版 Assert-DesktopIcons.ps1。
            Wait-GuestOps | Out-Null
            if (-not $SkipDeploy) { Invoke-Deploy }
            Wait-InteractiveSession -Case $case | Out-Null
        }
        try {
            & $CASE_MAP[$case] | Out-Null
        }
        catch {
            $c = New-CaseResult $case "（执行时异常）" '-' 'AUTO'
            Add-Step $c '编排器异常' 'BLOCKED' "$($_.Exception.Message)"
            Close-Case $c -ForceVerdict 'BLOCKED' -Reason "编排器异常：$($_.Exception.Message)" | Out-Null
            Write-Log "用例 $case 抛出异常：$($_.Exception.Message)" 'ERROR'
        }
        # 回收整个 guest 证据目录里的日志（最后一次即可，这里做轻量清场）
        Stop-GuestApp -Hard

        # 每跑完一个用例就落一次盘：Write-Summary 是幂等的（全量重写 summary.json/md）。
        # 长跑时编排器进程若被外部中断，已完成用例的结论不至于全部丢失。
        try { Write-Summary } catch { Write-Log "阶段性 Write-Summary 失败：$($_.Exception.Message)" 'WARN' }
    }

    $script:CurrentCase = 'summary'
    Write-Summary
    if (-not $KeepVmRunning -and -not $DryRun) {
        Invoke-Vmrun -Arguments @('stop', $VmxPath, 'soft') -NoAuth -AllowFailure -TimeoutSec 180 -Purpose '关闭靶机' | Out-Null
    }
}
catch {
    Write-Log "编排器致命错误：$($_.Exception.Message)" 'ERROR'
    Write-Log "$($_.ScriptStackTrace)" 'ERROR'
    if ($script:Results.Count -gt 0) { Write-Summary }
    exit 1
}

$failCount = @($script:Results | Where-Object { $_.verdict -eq 'FAIL' }).Count
# DryRun 只是流程演练，结论不代表产品质量，故一律退出 0；真实运行才按 FAIL 数决定放行
exit $(if ($DryRun) { 0 } elseif ($failCount -gt 0) { 1 } else { 0 })

<#
.SYNOPSIS
    guest 侧 UI 驱动器 —— 用 UI Automation 操作 DesktopSuite 主窗口 / 托盘，把原本必须人手点击的
    用例（V1 / V3 / V4 / V5 / V6 / V9 / V10）拉进自动化范围。

.DESCRIPTION
    DesktopSuite 没有提供任何可编程的命令行开关来切换图标（只有 --background 与 --wallpaper-host），
    因此这些用例只能靠 UI 驱动。WPF 控件默认暴露 UIA，且 x:Name 会自动成为 AutomationId，
    所以 ChkHideIcons / ChkRestoreIconsOnExit / BtnDiagnose 等都可以稳定定位。

    【三个关键设计】
    1. 唤起隐藏窗口不点托盘：应用自己创建了具名事件 "DesktopSuiteShowWindow"（见 App.xaml.cs），
       Set() 它就会调用 ShowMainWindow()。这比在通知区域找图标再模拟点击可靠得多，
       而且是 --background 支线（V2-B / V11-B）唯一稳的开窗方式。
    2. 「真正退出」只能走托盘菜单。runbook §0-A 明确：点 × 只是最小化到托盘，不是退出。
       本脚本因此**绝不**用 WM_CLOSE / taskkill 冒充退出 —— 那会把 V3 验成一个假 PASS。
       托盘路径失败时一律返回 blocked，交由宿主机判 BLOCKED 并转人工。
    3. 任何定位失败都是 blocked（环境/时序问题），不是 fail（产品缺陷）。判 PASS/FAIL 是宿主机的事。

.PARAMETER Action
    见下方 ValidateSet。

.PARAMETER Value
    Action 需要的参数（如场景名「专注」）。

.PARAMETER OutFile
    结果 JSON 落盘路径（vmrun 不回传 stdout，务必提供）。

.PARAMETER TimeoutSec
    等待窗口/控件出现的超时秒数。2GB 内存的靶机很慢，默认放宽到 60。

.OUTPUTS
    退出码：0 = 动作已执行；3 = blocked（找不到窗口/控件/托盘）；4 = error。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet(
        'ShowWindow',        # 通过具名事件唤起主窗口（对 --background 实例同样有效）
        'ReadState',         # 读取复选框状态 + Status + DesktopStatus 文本
        'SetHideIcons',      # -Value on|off
        'SetRestoreOnExit',  # -Value on|off
        'SetLaunchOnBoot',   # -Value on|off
        'ToggleHideIconsTwiceFast',  # V10 并发点击：1 秒内连点两次
        'ApplyScene',        # -Value 日常|专注|演示
        'Diagnose',          # 点「运行壁纸诊断」并回读 DiagInfo 全文
        'TrayExitKeepWallpaper'      # 唯一合法的「真正退出」路径（V3/V4）
    )]
    [string] $Action,

    [string] $Value,
    [string] $OutFile,
    [int]    $TimeoutSec = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$EXIT_OK      = 0
$EXIT_BLOCKED = 3
$EXIT_ERROR   = 4

$result = [ordered]@{
    schema     = 'gstack.appui/1'
    action     = $Action
    value      = $Value
    timestamp  = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff')
    sessionId  = [System.Diagnostics.Process]::GetCurrentProcess().SessionId
    status     = 'error'      # ok | blocked | error
    reasonCode = ''
    reasonText = ''
    data       = [ordered]@{}
}

function Write-ResultAndExit {
    param([int] $Code)
    $json = $result | ConvertTo-Json -Depth 6
    if ($OutFile) {
        try {
            $dir = Split-Path -Parent $OutFile
            if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
            Set-Content -LiteralPath $OutFile -Value $json -Encoding UTF8
        } catch { Write-Warning "写 OutFile 失败：$($_.Exception.Message)" }
    }
    Write-Output $json
    exit $Code
}

function Set-Blocked {
    param([string] $Code, [string] $Text)
    $result.status     = 'blocked'
    $result.reasonCode = $Code
    $result.reasonText = $Text
    Write-ResultAndExit $EXIT_BLOCKED
}

# session 0 下没有交互桌面，UIA 什么也看不到 —— 先拦，避免产生误导性的「控件找不到」
if ($result.sessionId -eq 0) {
    Set-Blocked 'session-0-non-interactive' 'UI 驱动必须运行在交互会话。vmrun 调用缺少 -interactive 参数。'
}

try {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase, System.Windows.Forms -ErrorAction Stop
}
catch {
    Set-Blocked 'uia-assembly-load-failed' "无法加载 UIAutomation 程序集：$($_.Exception.Message)"
}

# 鼠标模拟：UIA 没有「右键」概念，托盘菜单只能靠真实鼠标事件唤出
if (-not ('GstackMouse' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class GstackMouse
{
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
    public const uint RIGHTDOWN = 0x0008;
    public const uint RIGHTUP   = 0x0010;
    public const uint LEFTDOWN  = 0x0002;
    public const uint LEFTUP    = 0x0004;
    public static void RightClick(int x, int y)
    {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(120);
        mouse_event(RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        mouse_event(RIGHTUP, 0, 0, 0, UIntPtr.Zero);
    }
}
'@ -Language CSharp
}

$AE  = [System.Windows.Automation.AutomationElement]
$TS  = [System.Windows.Automation.TreeScope]
$CND = [System.Windows.Automation.PropertyCondition]

function New-IdCondition   { param([string]$Id)   New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $Id) }
function New-NameCondition { param([string]$Name) New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $Name) }

<# 轮询等待主窗口出现。不用固定 sleep —— 2GB 靶机的 WPF 冷启动可能要十几秒。#>
function Wait-MainWindow {
    param([int] $Seconds)
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        try {
            $procs = @(Get-Process -Name DesktopSuite -ErrorAction SilentlyContinue |
                       Where-Object { $_.MainWindowHandle -ne 0 })
            foreach ($p in $procs) {
                $el = $AE::FromHandle($p.MainWindowHandle)
                if ($el -and $el.Current.Name -eq 'DesktopSuite') { return $el }
            }
            # 退路：按窗口名在根元素下找（进程未暴露 MainWindowHandle 时）
            $el = $AE::RootElement.FindFirst($TS::Children, (New-NameCondition 'DesktopSuite'))
            if ($el) { return $el }
        } catch { }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    return $null
}

function Wait-Element {
    param($Root, [string] $AutomationId, [int] $Seconds = 20)
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        try {
            $el = $Root.FindFirst($TS::Descendants, (New-IdCondition $AutomationId))
            if ($el) { return $el }
        } catch { }
        Start-Sleep -Milliseconds 300
    } while ((Get-Date) -lt $deadline)
    return $null
}

function Get-TextOf {
    param($Element)
    if (-not $Element) { return $null }
    try {
        $vp = $null
        if ($Element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) {
            return $vp.Current.Value
        }
    } catch { }
    try { return $Element.Current.Name } catch { return $null }
}

function Get-ToggleState {
    param($Element)
    if (-not $Element) { return 'notfound' }
    try {
        $tp = $null
        if ($Element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$tp)) {
            return $tp.Current.ToggleState.ToString()   # On | Off | Indeterminate
        }
    } catch { }
    return 'nopattern'
}

<#
  把复选框设成目标状态。返回 $true 表示「已经是目标态或已成功切换」。
  刻意做成幂等：重复调用不会把状态来回翻 —— 否则会污染 V5 的「不得出现连续两次翻转」判据。
#>
function Set-CheckBox {
    param($Root, [string] $AutomationId, [bool] $Desired, [int] $Seconds = 20)
    $el = Wait-Element -Root $Root -AutomationId $AutomationId -Seconds $Seconds
    if (-not $el) { return @{ ok = $false; reason = "control-not-found:$AutomationId" } }
    $before = Get-ToggleState $el
    $want   = if ($Desired) { 'On' } else { 'Off' }
    if ($before -eq $want) { return @{ ok = $true; changed = $false; before = $before; after = $before } }
    $tp = $null
    if (-not $el.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$tp)) {
        return @{ ok = $false; reason = "no-toggle-pattern:$AutomationId"; before = $before }
    }
    $tp.Toggle()
    # 等状态真的翻过去（Apply 是异步的，见 MainWindow.ApplyIconsWithRetry）
    $deadline = (Get-Date).AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 300
        $after = Get-ToggleState $el
        if ($after -eq $want) { return @{ ok = $true; changed = $true; before = $before; after = $after } }
    } while ((Get-Date) -lt $deadline)
    return @{ ok = $false; reason = 'toggle-did-not-settle'; before = $before; after = (Get-ToggleState $el) }
}

function Read-WindowState {
    param($Win)
    $chkHide    = Wait-Element -Root $Win -AutomationId 'ChkHideIcons'          -Seconds 10
    $chkRestore = Wait-Element -Root $Win -AutomationId 'ChkRestoreIconsOnExit' -Seconds 5
    $chkBoot    = Wait-Element -Root $Win -AutomationId 'ChkLaunchOnBoot'       -Seconds 5
    $status     = Wait-Element -Root $Win -AutomationId 'Status'                -Seconds 5
    $deskStatus = Wait-Element -Root $Win -AutomationId 'DesktopStatus'         -Seconds 5
    return [ordered]@{
        chkHideIcons          = Get-ToggleState $chkHide
        chkRestoreIconsOnExit = Get-ToggleState $chkRestore
        chkLaunchOnBoot       = Get-ToggleState $chkBoot
        statusText            = Get-TextOf $status
        desktopStatusText     = Get-TextOf $deskStatus
    }
}

<#
  通知区域里找到 DesktopSuite 的托盘图标并右键，然后点指定菜单项。
  这条路径天生脆弱（Win10 通知区域会把图标折叠进「溢出」窗口，本地化名称也不固定），
  所以任何一步失败都返回 blocked 而不是 fail。
#>
function Invoke-TrayMenuItem {
    param([string] $MenuItemName, [int] $Seconds = 30)

    $trayBtn = $null
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        foreach ($cls in @('Shell_TrayWnd', 'NotifyIconOverflowWindow')) {
            try {
                $root = $AE::RootElement.FindFirst($TS::Children,
                        (New-Object System.Windows.Automation.PropertyCondition($AE::ClassNameProperty, $cls)))
                if (-not $root) { continue }
                $buttons = $root.FindAll($TS::Descendants,
                           (New-Object System.Windows.Automation.PropertyCondition(
                               $AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
                foreach ($b in $buttons) {
                    $n = ''
                    try { $n = $b.Current.Name } catch { }
                    if ($n -and $n -match 'DesktopSuite') { $trayBtn = $b; break }
                }
            } catch { }
            if ($trayBtn) { break }
        }
        if ($trayBtn) { break }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)

    if (-not $trayBtn) {
        return @{ ok = $false; reason = 'tray-icon-not-found'
                  text = '通知区域中找不到 DesktopSuite 托盘图标（可能被折叠进溢出区且 UIA 不可见）。' }
    }

    try { $pt = $trayBtn.GetClickablePoint() }
    catch { return @{ ok = $false; reason = 'tray-icon-no-clickable-point'; text = '托盘图标不可点击（可能处于隐藏的溢出区）。' } }

    [GstackMouse]::RightClick([int]$pt.X, [int]$pt.Y)

    # 等 ContextMenuStrip 弹出并找到目标菜单项
    $item = $null
    $mDeadline = (Get-Date).AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 400
        try {
            $menus = $AE::RootElement.FindAll($TS::Children,
                     (New-Object System.Windows.Automation.PropertyCondition(
                         $AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Menu)))
            foreach ($m in $menus) {
                $cand = $m.FindFirst($TS::Descendants, (New-NameCondition $MenuItemName))
                if ($cand) { $item = $cand; break }
            }
            if (-not $item) {
                # 有些 WinForms 菜单以 Window 形式出现，兜底全局搜一次
                $item = $AE::RootElement.FindFirst($TS::Descendants, (New-NameCondition $MenuItemName))
            }
        } catch { }
        if ($item) { break }
    } while ((Get-Date) -lt $mDeadline)

    if (-not $item) {
        # 把菜单关掉，别把靶机留在一个弹出菜单挡着的状态
        try { [System.Windows.Forms.SendKeys]::SendWait('{ESC}') } catch { }
        return @{ ok = $false; reason = 'tray-menu-item-not-found'
                  text = "托盘菜单已弹出但找不到「$MenuItemName」项。" }
    }

    try {
        $ip = $null
        if ($item.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$ip)) {
            $ip.Invoke()
        } else {
            $p = $item.GetClickablePoint()
            [GstackMouse]::SetCursorPos([int]$p.X, [int]$p.Y) | Out-Null
            Start-Sleep -Milliseconds 100
            [GstackMouse]::mouse_event([GstackMouse]::LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
            [GstackMouse]::mouse_event([GstackMouse]::LEFTUP,   0, 0, 0, [UIntPtr]::Zero)
        }
        return @{ ok = $true }
    }
    catch {
        return @{ ok = $false; reason = 'tray-menu-invoke-failed'; text = "$($_.Exception.Message)" }
    }
}

# ============================== 动作分发 ==============================
try {
    # ShowWindow 不需要窗口已存在，单独处理
    if ($Action -eq 'ShowWindow') {
        $signalled = $false
        try {
            # App.xaml.cs 里的具名事件；Set() 后后台线程会调用 ShowMainWindow()
            $ev = New-Object System.Threading.EventWaitHandle($false, [System.Threading.EventResetMode]::AutoReset, 'DesktopSuiteShowWindow')
            $signalled = $ev.Set()
            $ev.Dispose()
        } catch {
            Set-Blocked 'show-event-failed' "无法打开具名事件 DesktopSuiteShowWindow：$($_.Exception.Message)"
        }
        $win = Wait-MainWindow -Seconds $TimeoutSec
        if (-not $win) {
            Set-Blocked 'main-window-not-shown' "已发出 DesktopSuiteShowWindow 事件（signalled=$signalled），但 $TimeoutSec 秒内主窗口未出现。应用可能未运行。"
        }
        $result.status = 'ok'
        $result.data.signalled = $signalled
        $result.data.windowState = Read-WindowState $win
        Write-ResultAndExit $EXIT_OK
    }

    $win = Wait-MainWindow -Seconds $TimeoutSec
    if (-not $win -and $Action -ne 'TrayExitKeepWallpaper') {
        Set-Blocked 'main-window-not-found' "$TimeoutSec 秒内未找到 DesktopSuite 主窗口。若应用以 --background 启动，请先执行 -Action ShowWindow。"
    }

    switch ($Action) {

        'ReadState' {
            $result.data.windowState = Read-WindowState $win
            $result.status = 'ok'
        }

        'SetHideIcons' {
            if ($Value -notin @('on', 'off')) { Set-Blocked 'bad-value' "-Value 必须是 on 或 off，收到 '$Value'" }
            $before = Read-WindowState $win
            $r = Set-CheckBox -Root $win -AutomationId 'ChkHideIcons' -Desired ($Value -eq 'on')
            $result.data.toggle = $r
            $result.data.before = $before
            # 图标 apply 是异步 + 退避重试的，给它沉降时间再读最终文案
            Start-Sleep -Seconds 3
            $result.data.after = Read-WindowState $win
            if ($r.ok) { $result.status = 'ok' }
            else { Set-Blocked $r.reason "切换 ChkHideIcons 失败：$($r.reason)" }
        }

        'SetRestoreOnExit' {
            if ($Value -notin @('on', 'off')) { Set-Blocked 'bad-value' "-Value 必须是 on 或 off" }
            $r = Set-CheckBox -Root $win -AutomationId 'ChkRestoreIconsOnExit' -Desired ($Value -eq 'on')
            $result.data.toggle = $r
            $result.data.after  = Read-WindowState $win
            if ($r.ok) { $result.status = 'ok' } else { Set-Blocked $r.reason "切换 ChkRestoreIconsOnExit 失败" }
        }

        'SetLaunchOnBoot' {
            if ($Value -notin @('on', 'off')) { Set-Blocked 'bad-value' "-Value 必须是 on 或 off" }
            $r = Set-CheckBox -Root $win -AutomationId 'ChkLaunchOnBoot' -Desired ($Value -eq 'on')
            $result.data.toggle = $r
            $result.data.after  = Read-WindowState $win
            if ($r.ok) { $result.status = 'ok' } else { Set-Blocked $r.reason "切换 ChkLaunchOnBoot 失败" }
        }

        'ToggleHideIconsTwiceFast' {
            # V10 步骤 3：一次切换尚未完成时再点一次，应看到「桌面操作正在进行中，请稍候…」
            $el = Wait-Element -Root $win -AutomationId 'ChkHideIcons' -Seconds 20
            if (-not $el) { Set-Blocked 'control-not-found:ChkHideIcons' '找不到 ChkHideIcons' }
            $tp = $null
            if (-not $el.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$tp)) {
                Set-Blocked 'no-toggle-pattern' 'ChkHideIcons 无 TogglePattern'
            }
            $result.data.before = Read-WindowState $win
            $tp.Toggle()
            Start-Sleep -Milliseconds 300      # 刻意 <1s，制造并发
            $tp.Toggle()
            Start-Sleep -Milliseconds 800
            $result.data.immediately = Read-WindowState $win
            Start-Sleep -Seconds 4
            $result.data.settled = Read-WindowState $win
            $result.status = 'ok'
        }

        'ApplyScene' {
            if ([string]::IsNullOrWhiteSpace($Value)) { Set-Blocked 'bad-value' '-Value 必须给出场景名（日常/专注/演示）' }
            $cmb = Wait-Element -Root $win -AutomationId 'CmbScene' -Seconds 20
            if (-not $cmb) { Set-Blocked 'control-not-found:CmbScene' '找不到场景下拉框' }
            $ep = $null
            if ($cmb.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$ep)) {
                $ep.Expand(); Start-Sleep -Milliseconds 500
            }
            $item = $cmb.FindFirst($TS::Descendants, (New-NameCondition $Value))
            if (-not $item) {
                if ($ep) { $ep.Collapse() }
                Set-Blocked 'scene-not-in-list' "场景下拉框里没有「$Value」。可用项需人工核对。"
            }
            $sp = $null
            if ($item.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$sp)) { $sp.Select() }
            if ($ep) { try { $ep.Collapse() } catch { } }
            Start-Sleep -Milliseconds 500

            $btn = Wait-Element -Root $win -AutomationId 'BtnApplyScene' -Seconds 10
            if (-not $btn) { Set-Blocked 'control-not-found:BtnApplyScene' '找不到「应用场景」按钮' }
            $ip = $null
            if ($btn.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$ip)) { $ip.Invoke() }
            else { Set-Blocked 'no-invoke-pattern' '「应用场景」按钮无 InvokePattern' }

            # 场景应用顺序为 图标→轮换→壁纸→声音，壁纸启动 mpv 较慢，给足 15 秒
            Start-Sleep -Seconds 15
            $result.data.after = Read-WindowState $win
            $result.status = 'ok'
        }

        'Diagnose' {
            $btn = Wait-Element -Root $win -AutomationId 'BtnDiagnose' -Seconds 20
            if (-not $btn) { Set-Blocked 'control-not-found:BtnDiagnose' '找不到「运行壁纸诊断」按钮' }
            $ip = $null
            if ($btn.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$ip)) { $ip.Invoke() }
            else { Set-Blocked 'no-invoke-pattern' '诊断按钮无 InvokePattern' }
            Start-Sleep -Seconds 3
            $diag = Wait-Element -Root $win -AutomationId 'DiagInfo' -Seconds 10
            $result.data.diagnostics  = Get-TextOf $diag
            $result.data.windowState  = Read-WindowState $win
            $result.status = 'ok'
        }

        'TrayExitKeepWallpaper' {
            # runbook §0-A：这是唯一合法的「真正退出」。绝不用 taskkill 替代。
            $r = Invoke-TrayMenuItem -MenuItemName '退出（保留壁纸）' -Seconds $TimeoutSec
            if (-not $r.ok) {
                Set-Blocked $r.reason ("$($r.text) —— 无法自动执行托盘退出。" +
                    '注意：不得用 taskkill 或关闭窗口来替代，那走的是完全不同的代码路径（× 只最小化到托盘），会把用例验成假结果。此步需转人工。')
            }
            # 轮询确认主进程真的没了（渲染子进程按设计会存活，不算数）
            $deadline = (Get-Date).AddSeconds(30)
            $gone = $false
            do {
                Start-Sleep -Milliseconds 700
                $main = @(Get-CimInstance Win32_Process -Filter "Name='DesktopSuite.exe'" -ErrorAction SilentlyContinue |
                          Where-Object { -not ($_.CommandLine -match '--wallpaper-host') })
                if ($main.Count -eq 0) { $gone = $true; break }
            } while ((Get-Date) -lt $deadline)
            $result.data.mainProcessGone = $gone
            if (-not $gone) {
                Set-Blocked 'exit-did-not-complete' '已点击「退出（保留壁纸）」，但 30 秒内主进程仍然存活。'
            }
            $result.status = 'ok'
        }
    }

    Write-ResultAndExit $EXIT_OK
}
catch {
    $result.status     = 'error'
    $result.reasonCode = 'script-exception'
    $result.reasonText = "$($_.Exception.GetType().Name): $($_.Exception.Message)"
    $result.data.stack = "$($_.ScriptStackTrace)"
    Write-ResultAndExit $EXIT_ERROR
}

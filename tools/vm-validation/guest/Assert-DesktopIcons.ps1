<#
.SYNOPSIS
    桌面图标可见性断言器 —— 整套 VM 验证 harness 的基石。

.DESCRIPTION
    真实枚举 Explorer 的桌面图标窗口链，读取 SysListView32 的 WS_VISIBLE 样式位，
    输出结构化 JSON 并用退出码表达结论。

    【真值源对齐】
    本脚本刻意与产品代码 DesktopSuite.Desktop.DesktopShell 的判定逻辑逐行对齐：
      1. FindWindow("Progman")   → FindWindowEx(progman, 0, "SHELLDLL_DefView")
      2. 若失败，遍历桌面窗口的 WorkerW 子窗口 → FindWindowEx(workerW, 0, "SHELLDLL_DefView")
      3. FindWindowEx(defView, 0, "SysListView32")
      4. 读 SysListView32 的 GWL_STYLE，取 WS_VISIBLE (0x10000000) 位
    产品 AreIconsVisible() 返回 bool?，null == 找不到 SysListView32。本脚本必须给出同样的三态。

    【为什么不用 IsWindowVisible】
    IsWindowVisible 会连带检查所有祖先窗口的可见性；产品只读 SysListView32 自身的样式位。
    两者在 DefView 被隐藏时会给出不同答案。本脚本以「样式位」为判据（与产品一致），
    IsWindowVisible 仅作为辅助信息一并记录，绝不参与裁决。

    【防误报：unknown 绝不等于 hidden】
    这正是产品 IconApplyOutcome.Unknown 想防的坑。测试脚本自己不能犯同样的错误：
      - blocked : 我们根本没资格看桌面（session 0 / 非 WinSta0 窗口站）→ 环境问题，判 BLOCKED
      - unknown : 有资格看，但窗口链不存在（如 Explorer 被结束）→ 真·未知，判 UNKNOWN
      - hidden  : 窗口链在，WS_VISIBLE 为 0
      - visible : 窗口链在，WS_VISIBLE 为 1
    宿主机侧必须把 blocked 报成 BLOCKED、把 unknown 报成 UNKNOWN，都不得折叠成 FAIL 或 PASS。

.PARAMETER OutFile
    JSON 结果落盘路径。强烈建议始终提供 —— vmrun runProgramInGuest 不回传 stdout，
    宿主机只能靠 copyFileFromGuestToHost 取回这个文件。

.PARAMETER Label
    本次采样的标签（写进 JSON，便于宿主机把证据和用例步骤对上）。

.PARAMETER Samples
    连续采样次数（默认 1）。用于捕捉「登录后图标短暂可见随后被自启重新隐藏」的窗口期
    —— 见 runbook §0-C 与 V11-B 步骤 6。

.PARAMETER IntervalMs
    多次采样之间的间隔毫秒数（默认 1000）。

.PARAMETER Quiet
    不向 stdout 打印 JSON（仍然写 OutFile）。

.OUTPUTS
    退出码：
      0 = visible  （图标可见）
      1 = hidden   （图标已隐藏）
      2 = unknown  （窗口链不可读 —— 真·未知，不是 hidden）
      3 = blocked  （无交互桌面访问权：session 0 或非 WinSta0）
      4 = error    （脚本自身异常）

.NOTES
    vmrun 调用方式（必须带 -interactive，否则进程落在 session 0，永远拿到 blocked）：
      vmrun -T ws -gu <U> -gp <P> runProgramInGuest <vmx> -interactive `
        "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe" `
        -NoProfile -ExecutionPolicy Bypass -File C:\gstack\scripts\Assert-DesktopIcons.ps1 `
        -OutFile C:\gstack\evidence\icons.json
#>
[CmdletBinding()]
param(
    [string] $OutFile,
    [string] $Label = 'adhoc',
    [ValidateRange(1, 600)]
    [int]    $Samples = 1,
    [ValidateRange(50, 60000)]
    [int]    $IntervalMs = 1000,
    [switch] $Quiet
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 退出码常量 —— 与文件头 .OUTPUTS 一致，宿主机侧 Run-Validation.ps1 依赖这套映射
$EXIT_VISIBLE = 0
$EXIT_HIDDEN  = 1
$EXIT_UNKNOWN = 2
$EXIT_BLOCKED = 3
$EXIT_ERROR   = 4

#region Win32 互操作
# 这段 C# 只做只读的窗口查询，不修改任何窗口状态。
$nativeSource = @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class GstackShell
{
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowW(string lpClassName, string lpWindowName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowExW(IntPtr hWndParent, IntPtr hWndChildAfter,
                                              string lpszClass, string lpszWindow);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDesktopWindow();

    // ---------------------------------------------------------------------
    // 「只按窗口类名查找」的安全封装。
    // 必须在 C# 侧传 null：PowerShell 把 $null 传给 string 形参时会变成空串 ""，
    // 而 FindWindow(class, "") 语义是「标题为空的窗口」。WorkerW / SHELLDLL_DefView
    // 恰好标题为空所以侥幸能中，但 Progman 标题是 "Program Manager"，于是永远返回 0，
    // 导致桌面明明正常却被判 unknown(defview-not-found)。
    // ---------------------------------------------------------------------
    public static IntPtr FindByClass(string className)
    {
        return FindWindowW(className, null);
    }

    public static IntPtr FindChildByClass(IntPtr parent, IntPtr childAfter, string className)
    {
        return FindWindowExW(parent, childAfter, className, null);
    }

    // 仅 64 位 user32 导出 GetWindowLongPtrW；32 位下它是宏，必须退回 GetWindowLongW。
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern IntPtr GetProcessWindowStation();

    [DllImport("user32.dll")]
    public static extern IntPtr GetThreadDesktop(uint dwThreadId);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetUserObjectInformationW(IntPtr hObj, int nIndex,
                                                        [Out] StringBuilder pvInfo,
                                                        int nLength, out int lpnLengthNeeded);

    public const int  UOI_NAME    = 2;
    public const int  GWL_STYLE   = -16;
    public const long WS_VISIBLE  = 0x10000000L;

    /// <summary>读窗口样式，自动适配 32/64 位宿主进程。</summary>
    public static long GetStyle(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return 0;
        if (IntPtr.Size == 8) return GetWindowLongPtrW(hWnd, GWL_STYLE).ToInt64();
        return (long)(uint)GetWindowLongW(hWnd, GWL_STYLE);
    }

    /// <summary>WS_VISIBLE 样式位 —— 与产品 DesktopShell.IsVisible 完全一致的判据。</summary>
    public static bool HasStyleVisible(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return false;
        return (GetStyle(hWnd) & WS_VISIBLE) != 0;
    }

    public static string GetClass(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return "";
        var sb = new StringBuilder(256);
        int n = GetClassNameW(hWnd, sb, sb.Capacity);
        return n > 0 ? sb.ToString() : "";
    }

    /// <summary>窗口站名称。交互桌面为 "WinSta0"；服务会话为 "Service-0x0-3e7$" 之类。</summary>
    public static string GetWindowStationName()
    {
        var sb = new StringBuilder(256);
        int needed;
        if (GetUserObjectInformationW(GetProcessWindowStation(), UOI_NAME, sb, sb.Capacity * 2, out needed))
            return sb.ToString();
        return "(unavailable)";
    }

    /// <summary>桌面对象名称。交互桌面通常为 "Default"；锁屏为 "Winlogon"。</summary>
    public static string GetDesktopName()
    {
        var sb = new StringBuilder(256);
        int needed;
        if (GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), UOI_NAME, sb, sb.Capacity * 2, out needed))
            return sb.ToString();
        return "(unavailable)";
    }
}
'@

try {
    if (-not ('GstackShell' -as [type])) {
        Add-Type -TypeDefinition $nativeSource -Language CSharp -ErrorAction Stop
    }
}
catch {
    # 连 P/Invoke 都编不出来属于环境彻底不可用，直接 error 退出，不要伪装成 unknown。
    $msg = "Add-Type failed: $($_.Exception.Message)"
    if (-not $Quiet) { Write-Output (@{ verdict = 'error'; reasonCode = 'pinvoke-compile-failed'; reasonText = $msg } | ConvertTo-Json) }
    exit $EXIT_ERROR
}
#endregion

function Format-Hwnd {
    param([IntPtr] $Handle)
    if ($Handle -eq [IntPtr]::Zero) { return '0 (none)' }
    return ('0x{0:X}' -f $Handle.ToInt64())
}

<#
  防御性句柄强制转换：某些桌面状态下（登录/锁屏切换的瞬态、多 WorkerW 枚举），
  Find-DefViewChain 返回的 defView / listView 可能以集合（ArrayList）形式落到调用方，
  直接传给 HasStyleVisible/IsWindowVisible（形参 IntPtr）会触发
  "无法将 ArrayList 转换为 Int32" 的 ArgumentTransformationMetadataException，
  把整脚本打成 exit 4 / verdict=error。这里强制取第一个标量句柄，保证永不抛转换异常。
#>
function Coerce-Handle {
    param($Value)
    if ($null -eq $Value) { return [IntPtr]::Zero }
    if ($Value -is [array] -or $Value -is [System.Collections.ArrayList] -or $Value -is [System.Collections.IEnumerable]) {
        try { $first = @($Value)[0]; if ($first -is [IntPtr]) { return $first } } catch {}
        try { return [IntPtr]([int]@($Value)[0]) } catch {}
        return [IntPtr]::Zero
    }
    if ($Value -is [IntPtr]) { return $Value }
    try { return [IntPtr]([int]$Value) } catch { return [IntPtr]::Zero }
}

<#
  复刻产品 DesktopShell.FindDefView() 的搜索顺序。
  返回一个对象，除了句柄本身还带上「从哪条路径找到的」，因为 Win10 上多显示器 / 壁纸引擎
  会把 SHELLDLL_DefView 从 Progman 迁到 WorkerW 下 —— 这个信息对排障极其重要。
#>
function Find-DefViewChain {
    $result = [ordered]@{
        progman                 = [IntPtr]::Zero
        defView                 = [IntPtr]::Zero
        defViewPath             = 'none'      # progman | workerw | none
        defViewParentClass      = ''
        workerWCount            = 0
        listView                = [IntPtr]::Zero
        # 额外诊断：产品搜索路径之外是否还藏着一个 DefView（Progman 下的 WorkerW）。
        # 产品代码不搜这条路径，若只有这里找得到，说明产品自身也会判 Unknown —— 这是产品缺陷线索，
        # 但绝不改变本脚本的裁决（裁决必须与产品视角一致）。
        strayDefViewUnderProgmanWorkerW = [IntPtr]::Zero
    }

    # 路径 1：Progman 直属子窗口
    $progman = [GstackShell]::FindByClass('Progman')
    $result.progman = $progman
    if ($progman -ne [IntPtr]::Zero) {
        $dv = [GstackShell]::FindChildByClass($progman, [IntPtr]::Zero, 'SHELLDLL_DefView')
        if ($dv -ne [IntPtr]::Zero) {
            $result.defView            = $dv
            $result.defViewPath        = 'progman'
            $result.defViewParentClass = 'Progman'
        }
    }

    # 路径 2：桌面窗口下的 WorkerW（壁纸引擎 / 多显示器场景）
    $desktop = [GstackShell]::GetDesktopWindow()
    $ww = [IntPtr]::Zero
    while ($true) {
        $ww = [GstackShell]::FindChildByClass($desktop, $ww, 'WorkerW')
        if ($ww -eq [IntPtr]::Zero) { break }
        $result.workerWCount++
        if ($result.defView -eq [IntPtr]::Zero) {
            $dv = [GstackShell]::FindChildByClass($ww, [IntPtr]::Zero, 'SHELLDLL_DefView')
            if ($dv -ne [IntPtr]::Zero) {
                $result.defView            = $dv
                $result.defViewPath        = 'workerw'
                $result.defViewParentClass = 'WorkerW'
            }
        }
    }

    # 额外诊断路径（产品不搜）：Progman 下挂的 WorkerW
    if ($result.defView -eq [IntPtr]::Zero -and $progman -ne [IntPtr]::Zero) {
        $pww = [IntPtr]::Zero
        while ($true) {
            $pww = [GstackShell]::FindChildByClass($progman, $pww, 'WorkerW')
            if ($pww -eq [IntPtr]::Zero) { break }
            $stray = [GstackShell]::FindChildByClass($pww, [IntPtr]::Zero, 'SHELLDLL_DefView')
            if ($stray -ne [IntPtr]::Zero) { $result.strayDefViewUnderProgmanWorkerW = $stray; break }
        }
    }

    if ($result.defView -ne [IntPtr]::Zero) {
        $result.listView = [GstackShell]::FindChildByClass($result.defView, [IntPtr]::Zero, 'SysListView32')
    }
    return $result
}

<#
  环境资格检查。返回 $null 表示有资格看桌面；否则返回 reasonCode 字符串。
  这是 BLOCKED / UNKNOWN 分流的唯一依据 —— 必须在窗口链搜索之前跑。
#>
function Test-DesktopAccess {
    $sessionId = [System.Diagnostics.Process]::GetCurrentProcess().SessionId
    $winsta    = [GstackShell]::GetWindowStationName()

    # session 0 是服务会话，物理上看不到用户桌面。
    # vmrun runProgramInGuest 不加 -interactive 时就落在这里 —— 最常见的假 unknown 来源。
    if ($sessionId -eq 0) { return 'session-0-non-interactive' }

    # 非 WinSta0 的窗口站同样看不到交互桌面。
    if ($winsta -and $winsta -ne 'WinSta0' -and $winsta -ne '(unavailable)') {
        return 'non-winsta0-window-station'
    }
    return $null
}

function Get-IconSample {
    param([int] $Index)

    $sample = [ordered]@{
        index                = $Index
        timestampLocal       = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff')
        timestampUtc         = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
        verdict              = 'error'
        reasonCode           = ''
        reasonText           = ''
        progman              = '0 (none)'
        defView              = '0 (none)'
        defViewPath          = 'none'
        workerWCount         = 0
        listView             = '0 (none)'
        defViewStyleVisible  = $null
        listViewStyleVisible = $null
        listViewIsWindowVisible = $null
        strayDefView         = '0 (none)'
        explorerInSession    = $false
    }

    $blocked = Test-DesktopAccess
    if ($blocked) {
        $sample.verdict    = 'blocked'
        $sample.reasonCode = $blocked
        $sample.reasonText = if ($blocked -eq 'session-0-non-interactive') {
            '进程运行在 session 0（服务会话），看不到交互桌面。vmrun 调用缺少 -interactive 参数。这是环境问题，不是产品缺陷。'
        } else {
            "进程窗口站为 $([GstackShell]::GetWindowStationName())，非 WinSta0，无法访问交互桌面。这是环境问题，不是产品缺陷。"
        }
        return $sample
    }

    # 记录同会话内 explorer.exe 是否存活 —— 用于区分「V9 故意结束 Explorer」与「桌面真的坏了」
    $mySession = [System.Diagnostics.Process]::GetCurrentProcess().SessionId
    $sample.explorerInSession = [bool](Get-Process -Name explorer -ErrorAction SilentlyContinue |
                                       Where-Object { $_.SessionId -eq $mySession })

    $chain = Find-DefViewChain
    $sample.progman      = Format-Hwnd $chain.progman
    $sample.defView      = Format-Hwnd $chain.defView
    $sample.defViewPath  = $chain.defViewPath
    $sample.workerWCount = $chain.workerWCount
    $sample.listView     = Format-Hwnd $chain.listView
    $sample.strayDefView = Format-Hwnd $chain.strayDefViewUnderProgmanWorkerW

    if ($chain.defView -ne [IntPtr]::Zero) {
        $sample.defViewStyleVisible = [GstackShell]::HasStyleVisible($chain.defView)
    }

    # ---- 裁决 ----
    # 与产品 AreIconsVisible() 一致：找不到 SysListView32 就是 null == unknown。
    if ($chain.listView -eq [IntPtr]::Zero) {
        $sample.verdict = 'unknown'
        if (-not $sample.explorerInSession) {
            $sample.reasonCode = 'explorer-not-running'
            $sample.reasonText = '本会话内没有 explorer.exe，桌面图标层不存在。若这是 V9 有意为之，unknown 即预期结果；否则说明桌面崩溃。'
        }
        elseif ($chain.defView -eq [IntPtr]::Zero -and $chain.strayDefViewUnderProgmanWorkerW -ne [IntPtr]::Zero) {
            $sample.reasonCode = 'defview-outside-product-search-path'
            $sample.reasonText = 'SHELLDLL_DefView 挂在 Progman 下的 WorkerW 里，产品的两条搜索路径都覆盖不到 —— 产品自身此刻也会判 Unknown。这是产品搜索路径缺口的线索。'
        }
        elseif ($chain.defView -eq [IntPtr]::Zero) {
            $sample.reasonCode = 'defview-not-found'
            $sample.reasonText = 'Progman 与桌面级 WorkerW 下均未找到 SHELLDLL_DefView。Explorer 可能尚未就绪（登录早期）或已被第三方桌面工具接管。'
        }
        else {
            $sample.reasonCode = 'listview-not-found'
            $sample.reasonText = '找到了 SHELLDLL_DefView，但其下没有 SysListView32。图标层未创建 —— 不得据此判定为 hidden。'
        }
        return $sample
    }

    $styleVisible = [GstackShell]::HasStyleVisible($chain.listView)
    $sample.listViewStyleVisible    = $styleVisible
    $sample.listViewIsWindowVisible = [GstackShell]::IsWindowVisible($chain.listView)

    if ($styleVisible) {
        $sample.verdict    = 'visible'
        $sample.reasonCode = 'ws-visible-set'
        $sample.reasonText = 'SysListView32 的 WS_VISIBLE 样式位为 1 —— 桌面图标可见。'
    }
    else {
        $sample.verdict    = 'hidden'
        $sample.reasonCode = 'ws-visible-clear'
        $sample.reasonText = 'SysListView32 的 WS_VISIBLE 样式位为 0 —— 桌面图标已隐藏。'
    }
    return $sample
}

# ------------------------------- 主流程 -------------------------------
$exitCode = $EXIT_ERROR
$report = $null

try {
    # 注意：这个局部变量不能叫 $samples —— PowerShell 变量名大小写不敏感，会与
    # param([int] $Samples) 撞名；参数变量带 [int] 类型约束，赋 ArrayList 会抛
    # ArgumentTransformationMetadataException，导致本脚本 100% 失败（exit 4 / verdict=error）。
    $sampleList = New-Object System.Collections.ArrayList
    for ($i = 1; $i -le $Samples; $i++) {
        [void]$sampleList.Add((Get-IconSample -Index $i))
        if ($i -lt $Samples) { Start-Sleep -Milliseconds $IntervalMs }
    }

    $last     = $sampleList[$sampleList.Count - 1]
    $verdicts = @($sampleList | ForEach-Object { $_.verdict })

    $report = [ordered]@{
        schema         = 'gstack.desktop-icons/1'
        label          = $Label
        computerName   = $env:COMPUTERNAME
        userName       = $env:USERNAME
        sessionId      = [System.Diagnostics.Process]::GetCurrentProcess().SessionId
        windowStation  = [GstackShell]::GetWindowStationName()
        desktopName    = [GstackShell]::GetDesktopName()
        psBitness      = $(if ([IntPtr]::Size -eq 8) { 'x64' } else { 'x86' })
        osLastBootUtc  = $(try { (Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ') } catch { '(unavailable)' })
        sampleCount    = $sampleList.Count
        intervalMs     = $IntervalMs
        # 聚合标志：给 V11-B / V2-B 这类「窗口期捕捉」用例判读用
        anyVisible     = [bool]($verdicts -contains 'visible')
        anyHidden      = [bool]($verdicts -contains 'hidden')
        anyUnknown     = [bool]($verdicts -contains 'unknown')
        anyBlocked     = [bool]($verdicts -contains 'blocked')
        # 最终裁决恒取最后一次采样 —— 语义明确，不做任何「多数表决」之类的模糊处理
        verdict        = $last.verdict
        reasonCode     = $last.reasonCode
        reasonText     = $last.reasonText
        samples        = @($sampleList)
    }

    switch ($last.verdict) {
        'visible' { $exitCode = $EXIT_VISIBLE }
        'hidden'  { $exitCode = $EXIT_HIDDEN }
        'unknown' { $exitCode = $EXIT_UNKNOWN }
        'blocked' { $exitCode = $EXIT_BLOCKED }
        default   { $exitCode = $EXIT_ERROR }
    }
    $report.exitCode = $exitCode
}
catch {
    $report = [ordered]@{
        schema     = 'gstack.desktop-icons/1'
        label      = $Label
        verdict    = 'error'
        reasonCode = 'script-exception'
        reasonText = "$($_.Exception.GetType().Name): $($_.Exception.Message)"
        stack      = "$($_.ScriptStackTrace)"
        exitCode   = $EXIT_ERROR
    }
    $exitCode = $EXIT_ERROR
}

$json = $report | ConvertTo-Json -Depth 6

if ($OutFile) {
    try {
        $dir = Split-Path -Parent $OutFile
        if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        # UTF8 with BOM：宿主机 PowerShell 5.1 读回中文 reasonText 不乱码
        Set-Content -LiteralPath $OutFile -Value $json -Encoding UTF8
    }
    catch {
        # 落盘失败不改变裁决，但要让 stdout 留痕
        Write-Warning "写入 OutFile 失败：$($_.Exception.Message)"
    }
}

if (-not $Quiet) { Write-Output $json }

exit $exitCode

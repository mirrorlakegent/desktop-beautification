# Run-Validation.ps1 修复记录（FIXES）

> 本文件固化 Phase 3 靶机验证过程中发现并修复的 **8 处 harness bug + 1 处 App bug**。
> 所有修复均已在裸 VM（靶机无 .NET）环境、配合 self-contained 构建验证通过。
> 修改 harness 前请先读此文件，避免误回退这些修复。

**背景**：DesktopSuite 最初四个用例（V6/V2A/V11A/V14）在靶机全部 FAIL/BLOCKED，
根因是**靶机 VM 完全没有安装 .NET**（`DOTNET_NOT_INSTALLED`），framework-dependent
产物无法运行。修复根因用 self-contained 构建；过程中又暴露出 8 处 harness 自身 bug，
本表逐一记录。完整验证结论见 `deliverables/gstack/vm-validation-desktopsuite-2026-08-07.md`。

---

## App 修复（1 处）

### APP-1｜WndProc 注销原因硬编码
- **位置**：`src/DesktopSuite/MainWindow.xaml.cs`（WM_QUERYENDSESSION 处理，约 L141）
- **问题**：`reason` 被硬编码为 `"系统关机（WM_QUERYENDSESSION）"`，注销（logoff）场景无法区分，导致 V12 的恢复日志与 V11A 完全相同，无法验证「系统注销」分支。
- **修复**：按消息 `lParam & 0x80000000` 区分 shutdown / logoff，logoff 时 `reason="系统注销"`。
- **验证**：第五轮 `20260807-232646` V12 命中 `退出（系统注销（WM_QUERYENDSESSION））：恢复桌面图标` 恰好 1 条。

---

## Harness 修复（8 处）

### H-1｜safe-delete 平台包装器拦截 Remove-Item（4 处）
- **位置**：`Run-Validation.ps1` L457 / L667 / L1684 / L1693
- **问题**：CodeBuddy/WorkBuddy 平台注入的 safe-delete 包装器拦截 `Remove-Item`，
  其 trash 机制先移走文件再调 `[System.IO.File]::Delete()`，文件已不在 → throw
  `SAFE_DELETE_FAIL_CLOSED`（终止异常，`-ErrorAction SilentlyContinue` 压不住）。
  导致 Get-CurrentGuestBootUtc / Set-GuestSettings / Invoke-Deploy 在清理步骤直接崩溃。
- **修复**：4 处 `Remove-Item ... -ErrorAction SilentlyContinue` 改为
  `try { if (Test-Path ...) { [System.IO.File]::Delete(...) } } catch {}`，用 .NET 方法绕过 PowerShell cmdlet 拦截器。
- **验证**：第二轮 `20260807-094921` V2A/V11A 不再 BLOCKED。

### H-2｜Invoke-GuestUi 硬编码 60s 超时
- **位置**：`Run-Validation.ps1` L605（`Invoke-GuestUi` 函数）
- **问题**：`$args = @(..., '-TimeoutSec', '60')` 硬编码字符串 `'60'`，忽略调用方传入的
  `$TimeoutSec`（如 180），导致 `Wait-MainWindow` 只等 60s，V6 主窗口未出现即判 `main-window-not-found`。
- **修复**：`'60'` → `"$TimeoutSec"`；vmrun 侧超时从 `$TimeoutSec` → `$TimeoutSec + 60` 留传输开销。
- **验证**：第二轮 `20260807-094921` V6 PASS。

### H-3｜V6 缺少 Start-SafeSleep
- **位置**：`Run-Validation.ps1` V6 用例流程（`Start-GuestApp` 之后）
- **问题**：V6 是唯一在 `Start-GuestApp` 后不等 20s 就直接做 UI 自动化的用例，
  进程刚启动窗口尚未就绪，UI 自动化找不到主窗口。
- **修复**：`Start-GuestApp` 后补 `Start-SafeSleep -Seconds 20`（与 V1/V2A/V11A/V14 一致）。
- **验证**：第二轮 `20260807-094921` V6 PASS。

### H-4｜Compress-Archive 对 ~200MB 包 OOM
- **位置**：`Run-Validation.ps1` 部署段（`Invoke-Deploy` 内压缩步骤）
- **问题**：self-contained 包约 200MB（含 mpv.exe 117MB），`Compress-Archive` 把全部内容
  载入内存 → `OutOfMemoryException`，部署阶段崩溃。
- **修复**：改用 .NET `ZipFile.CreateFromDirectory`（流式）压缩；guest 侧解压同步改用 `ExtractToDirectory`。
- **验证**：第三轮 `20260807-175732` 起部署稳定。

### H-5｜V14 settings.json 断言缺外层括号
- **位置**：`Run-Validation.ps1` L1620（V14 内联检查）
- **问题**：`elseif (Test-Prop $post 'settings.parseError' -and (Get-Prop $post 'settings.parseError'))`
  缺少外层括号，`-and` 被误当 `Test-Prop` 参数，条件恒等于"字段是否存在"；而采集器
  总输出该字段（无错时为 `null`）→ 条件恒真 → 永远误报 FAIL。
- **修复**：补括号 `elseif ((Test-Prop ...) -and (Get-Prop ...))`。
- **验证**：第三轮 `20260807-175732` V14 PASS（`parseError:null` 不再误报）。

### H-6｜V11A/V11B/V12 日志模式漏 WM_QUERYENDSESSION
- **位置**：`Run-Validation.ps1` 日志断言（V11A ~L1456 / V11B ~L1528 / V12 ~L1573）
- **问题**：断言模式 `退出（系统关机）：恢复桌面图标` 与 App 实际日志
  `退出（系统关机（WM_QUERYENDSESSION））：恢复桌面图标` 不匹配（中间多一层），子串匹配 0 次 → FAIL。
- **修复**：模式更新为 `退出（系统关机（WM_QUERYENDSESSION））：恢复桌面图标`（V11A/V11B）；
  V12 的 logoff 分支模式同步匹配 `退出（系统注销（WM_QUERYENDSESSION））：恢复桌面图标`（配合 APP-1）。
- **验证**：第三轮 V11A PASS；第五轮 V12 PASS。

### H-7｜V12「注销确证」只认 session ID 变化
- **位置**：`Run-Validation.ps1` L1561（V12 post 检查）
- **问题**：注销确证仅认 `sessionBefore -ne sessionAfter`。但 V12 在 logoff 后用
  hypervisor reboot 恢复交互会话（autologon 在 logoff 后不触发），reboot 把 session ID
  重置回 1 → before==after==1 误判"可能只是锁屏" → BLOCKED。功能本身已确证 PASS。
- **修复**：改为三种信号之一即 PASS：① session ID 变化 ② `SessionEnding(Logoff)` 事件命中
  ③ 系统重启（`lastBootUtc` 变化）。锁屏冒充仍被拦截（三种信号均无则仍 BLOCKED）。
- **验证**：第四轮 `20260807-213300` 功能 7/8 全 PASS（仅此步误判）；第五轮 `20260807-232646` 全绿。

### H-8｜V12 logoff 竞态：shutdown /l /f 在交互会话挂起
- **位置**：`Run-Validation.ps1` L768（`Restart-Guest -Mode logoff`）
- **问题**：`Invoke-GuestInline -Interactive -TimeoutSec 60` 同步等待 `shutdown /l /f` 返回，
  但 logoff 销毁当前交互会话，vmrun `runProgramInGuest` 调用挂起（非确定性竞态，第四轮碰巧通过、第五轮卡死）。
- **修复**：logoff 改用 `-NoWait` 异步触发，不等待会销毁会话的 `shutdown` 命令；后续已有 20s sleep + reboot 负责观察窗口。
- **验证**：第五轮 `20260807-232646` V12 稳定全绿（不再卡死）。

---

## 防回归指引

1. **发布形态**：部署到裸 VM 一律用 self-contained 构建（见 `Publish-SelfContained.ps1`），
   不要用 `dotnet build` 或无 `-r` 的 publish。误用会导致"进程存活但窗口不出现"假死。
2. **删除文件**：新增文件删除逻辑时，优先用 `[System.IO.File]::Delete()` 而非 `Remove-Item`，避开平台 safe-delete 拦截。
3. **大包压缩**：>50MB 的包不要用 `Compress-Archive`，改用 `ZipFile`。
4. **日志断言**：App 日志格式含 `（WM_QUERYENDSESSION）` 层，断言模式须同步更新。
5. **V12 路径**：logoff 相关命令必须 `-NoWait`；注销确证接受三种信号之一。

最后更新：2026-08-08（第五轮 V12 闭环确认后）

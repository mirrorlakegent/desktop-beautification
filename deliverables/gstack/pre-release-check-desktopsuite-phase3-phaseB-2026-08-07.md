# DesktopSuite Phase 3 — Phase B 验证报告

**日期**: 2026-08-07  
**执行人**: gstack-qa-lead  
**靶机**: `E:\VMwar_xitongwenjian\win10\Windows 10 x64.vmx` (VMware Win10)  
**快照**: `gstack-clean-autologon`  
**Harness SHA256**: `B4CC8716491BF7722DFE3565630B78017C58001020922DFF049D406C480F9FC8`  
**Assert-DesktopIcons.ps1 SHA**: `3155E5DE8F4523DC4CE927902F760DF08C3D556085FB680A02F7666D15B094FD` ✅  
**Collect-State.ps1 SHA**: `0BF1143FC447CFC193DA3B3E44E18BB4A9D7315278BA662AD388AD924221318F` ✅  

---

## Harness 完整性确认

| 检查项 | 行号 | 状态 |
|--------|------|------|
| Assert-Setting 括号修复 | L828 `if ((Test-Prop $State 'settings.parseError') -and (Get-Prop ...))` | ✅ 在位 |
| Invoke-Deploy 前置（init） | L1811-1819: Start→Wait-Tools→Wait-GuestOps→Invoke-Deploy→Wait-InteractiveSession | ✅ 在位 |
| Invoke-Deploy 前置（RevertBetweenCases） | L1827-1835: Restore→Start→Wait-Tools→Wait-GuestOps→Invoke-Deploy→Wait-InteractiveSession | ✅ 在位 |
| Restart-Guest | L748-752: `vmrun reset hard`（从 `shutdown /r /t 0 /f` 演进，logoff 路径仍保留 `/f`） | ✅ 在位 |

**部署链路验证**: 全部 10 个用例的 session probe 均返回健康输出（exit=0 visible 或 exit=1 hidden），未出现 stale-build 污染（exit=4 / ArrayList→Int32），证明 `Invoke-Deploy` 在每个用例前成功覆盖了快照中的旧脚本。

---

## Phase B 用例裁决汇总（10 个用例）

| 用例id | verdict | 一句话原因 | 真缺陷还是基础设施 |
|--------|---------|-----------|-------------------|
| V1 | PASS | 三步断言全通过（visible→hidden→visible），无 ShowWindow 降级 | 真实产品裁决 |
| V5 | BLOCKED | 自动化部分全通过；托盘文案三态与双闪视觉判据需人工 | 基础设施（无自动化视觉验证） |
| V10 | BLOCKED | 成功态/并发态反馈已自动核对；过渡文案与卡死判据需人工 | 基础设施（无自动化视觉验证） |
| V8 | PASS | WallpaperLibrary 根目录+两个固定壁纸就位→PASS | 真实产品裁决 |
| V6 | **FAIL** | 专注/演示场景失败：`mpv.exe` 缺失于 `C:\gstack\app\` | **真实产品缺陷** |
| V7 | PASS | 场景意图持久化自动核对通过 | 真实产品裁决 |
| V3 | BLOCKED | 托盘图标不可见（tray-icon-not-found），前置步骤已通过 | 基础设施（UIA 找不到托盘） |
| V4 | BLOCKED | 与 V3 同因；前置步骤已通过（Restore=false→PASS） | 基础设施（UIA 找不到托盘） |
| V9 | **FAIL** | harness 报 P1-7 回归（intent False→True），但前置条件未满足 | **需人工复核**（见下） |
| V13 | NA | VMware 单虚拟显示器，无法构造 ≥2 屏场景 | 基础设施（VM 限制） |

**统计**: PASS=3 | FAIL=2 | BLOCKED=4 | NA=1

---

## FAIL 用例详细证据

### V6 — 场景应用失败（mpv.exe 缺失）

**根因**: `mpv.exe` 未包含在应用部署包中（`C:\gstack\app\`），导致所有依赖视频壁纸的场景应用失败。壁纸库文件本身存在（milkyway-1.mp4 21MB、night-city-1.mp4 43MB），但播放器缺失。

**失败步骤**:

| 步骤 | 状态 | 期望 | 实际 |
|------|------|------|------|
| 「专注」Status 无失败说明 | FAIL | 场景应用成功 | `mpv.exe was not found. Copy mpv.exe into the app output folder (C:\gstack\app\) or add it to PATH.（设置已回滚）` |
| 「专注」ActiveSceneName 一致 | FAIL | `ActiveSceneName=专注` | `ActiveSceneName=`（空，因回滚） |
| 「演示」Status 无失败说明 | FAIL | 场景应用成功 | 同专注：mpv.exe not found |
| 「演示」ActiveSceneName 一致 | FAIL | `ActiveSceneName=演示` | `ActiveSceneName=`（空） |

**通过步骤**: 「日常」场景全部通过（Status→PASS、ActiveSceneName=日常→PASS、轮换=True→PASS、声音=False→PASS），因为「日常」不依赖视频壁纸。

**关键日志行**:
```
[08:29:01][V6][FAIL] 「专注」Status 无失败说明 → FAIL :: high：应用场景失败：专注 —— mpv.exe was not found. Copy mpv.exe into the app output folder (C:\gstack\app\) or add it to PATH.（设置已回滚）
[08:30:12][V6][FAIL] 「演示」ActiveSceneName 一致 → FAIL :: ActiveSceneName 期望 演示，实际
[08:31:00][V6][FAIL] ==== V6 结论：FAIL ====
```

**分类**: 真实产品缺陷。应用包缺少 mpv.exe 依赖，导致视频壁纸场景功能不可用。

---

### V9 — Explorer 崩溃恢复（P1-7 回归，前置条件未满足）

**harness 裁决**: FAIL（critical）  
**建议裁决**: BLOCKED（前置条件未满足，P1-7 检查在错误上下文下执行）

**原因分析**:

V9 的测试流程是：杀死 Explorer → 期望桌面进入 Unknown 不可读态 → 在 Unknown 态下操作隐藏图标 → 验证 intent 不被改写（P1-7）。

但实际执行中：
1. 08:43:35 杀死 Explorer
2. 08:43:47 断言结果为 `visible`（Windows 10 在 ~12 秒内自动重启了 Explorer）
3. 前置步骤标记 BLOCKED：「结束 Explorer 后断言为 visible，未能制造 Unknown 场景，本用例前提不成立」
4. 但 harness 继续执行了 SetHideIcons on，app 正常处理（因为此时处于 visible 态）
5. P1-7 检查发现 intent 从 False 变为 True → 报 FAIL

**问题**: P1-7 检查的是「Unknown 态下 intent 不被改写」。但 Unknown 态从未实现（Explorer 自动重启），intent 变更发生在 visible 态，属于正常行为。FAIL 为误报。

**失败步骤**:

| 步骤 | 状态 | 期望 | 实际 |
|------|------|------|------|
| ★P1-7：Unknown 期间 intent 未被改写 | FAIL | Unknown 态下 intent 不变 | intent False→True（但实际处于 visible 态） |

**BLOCKED 步骤**:
- Shell 进入不可读态 → BLOCKED：结束 Explorer 后断言为 visible，未能制造 Unknown 场景
- ★P1-8：UI 明确报错不静默 → BLOCKED：未能读到 Status 文案

**通过步骤**:
- Explorer 恢复后图标层可读 → PASS（hidden，ws-visible-clear）

**关键日志行**:
```
[08:43:58][V9][BLOCK] Shell 进入不可读态 → BLOCKED :: 结束 Explorer 后断言为 visible，未能制造 Unknown 场景，本用例前提不成立
[08:44:27][V9][FAIL] ★P1-7：Unknown 期间 intent 未被改写 → FAIL :: critical，P1-7 回归：False → True
```

**分类**: 需人工复核。harness 报 FAIL，但前置条件（Unknown 态）未满足，P1-7 检查在 visible 态下执行，intent 变更属正常行为。建议在能稳定制造 Unknown 态的环境上重跑（如禁用 Explorer 自动重启）。

---

## BLOCKED 用例说明

| 用例 | BLOCKED 原因 | 已通过的自动化步骤 |
|------|-------------|-------------------|
| V5 | 托盘文案三态与双闪视觉判据需人工看截图/录屏 | 复选框On→hidden→PASS、意图DesiredIconsHidden=True→PASS |
| V10 | 过渡文案与卡死判据需人工 | 成功态隐藏/显示WM_COMMAND 0x7402→PASS×2 |
| V3 | tray-icon-not-found（UIA 找不到托盘图标） | 自启=False→PASS、退出前hidden→PASS、RestoreIconsOnExit=true→PASS |
| V4 | tray-icon-not-found（同 V3） | RestoreIconsOnExit=false→PASS、退出前hidden→PASS |

**V3/V4 补充**: V11A/V12（Phase A）已自动覆盖 RestoreIconsOnTeardown 同段代码（含意图保护），只是触发源不同（重启/注销 vs 托盘退出）。

---

## 证据路径

- **Batch 1 证据**: `tools\vm-validation\evidence\20260807-081952\`（V1, V5, V10, V8, V6）
- **Batch 2 证据**: `tools\vm-validation\evidence\20260807-083246\`（V7, V3, V4, V9, V13）
- **汇总 summary.json**: `tools\vm-validation\evidence\20260807-phaseB-consolidated\summary.json`

---

## 沟通记录

- **Batch 1 中间回报**: 无法通过 SendMessage 发送（Agent 邮箱未开通），已直接向用户报告。
- **Batch 2 终版**: 本报告。

---

## 放行判定

Phase B（10 用例）+ Phase A（6 用例）= V1–V14 全部执行完毕。

- **真实 PASS**: V1, V7, V8（Phase B）+ V12（Phase A）= 4
- **真实 FAIL**: V6（Phase B: mpv.exe 缺失）+ V2A, V2B（Phase A）= 3
- **需复核 FAIL**: V9（Phase B: 前置条件未满足，疑似误报）
- **BLOCKED**: V5, V10, V3, V4（Phase B）+ V11A, V11B（Phase A）= 6
- **NA**: V13（Phase B: 单显示器）
- **PASS (Phase A)**: V14 = 1

**判定**: 🔴 **No-Go**。存在 3 个确认的真实产品缺陷（V6 mpv.exe 缺失、V2A/V2B 重启后图标状态不符预期），加上 V9 待复核。6 个 BLOCKED 用例需人工补验后才能给出最终放行结论。

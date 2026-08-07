# DesktopSuite Phase 3 — 全用例验证终版报告（V1–V14）

**日期**：2026-08-07
**场景**：QA 测试 + 发布前检查
**参与成员**：gstack-qa-lead（质量门神）× 3 个子实例（qa-lead-2 / qa-v12-deploy / qa-v12-smoke）
**靶机**：`E:\VMwar_xitongwenjian\win10\Windows 10 x64.vmx`（VMware Win10）
**Harness SHA256**：`B4CC8716491BF7722DFE3565630B78017C58001020922DFF049D406C480F9FC8`
**Assert-DesktopIcons.ps1**：`3155E5DE...` ✅ | **Collect-State.ps1**：`0BF1143F...` ✅

---

## 📌 TL;DR

- **整体结论**：🔴 **No-Go**（放行阻断）
- **V1–V14 全部 16 个用例已执行完毕**（Phase A 6 个 + Phase B 10 个）
- 确认真实产品缺陷 **4 个**（V2A / V6 / V11A / V14），疑似误报 1 个（V9）
- 基础设施 BLOCKED **6 个**（需人工补验或环境改进）
- 下一步：产品团队修复 4 个真实缺陷 → QA 补验 6 个 BLOCKED → 重跑后放行

---

## 🎯 核心结论卡片

| 项目 | 内容 |
|------|------|
| Go / No-Go | 🔴 **No-Go** |
| 严重度分布 | 🔴 4 / 🟠 1 / 🟡 6 / 🟢 4 / ⬜ 1(NA) |
| 关键行动项 | 6 条 |
| 建议负责人 | 产品团队（V2A/V6/V11A/V14）、QA 基础设施（V3/V4/V5/V10/V11B/V12）|

---

## 1. 质量门神（gstack-qa-lead）核心结论

Phase A（门槛用例，2026-08-06）和 Phase B（剩余用例，2026-08-07）分两批执行。Harness 经历四层缺陷修复（部署顺序颠倒 / L775 括号 / shutdown 缺 /f / ConvertTo-Json 挂死），最终版 `B4CC8716` 四层修复全部在位，10+6=16 个用例全部基于合法输出（零 stale-build 污染）。

**关键判断**：V11A 和 V14 是最严重的两个真实缺陷 —— 前者意味着用户关机后拿空桌面（SessionEnding 恢复闩锁命中 0 次），后者意味着异常退出后配置文件损坏（settings.json 被写坏）。V6 是部署包遗漏 mpv.exe 导致视频壁纸功能不可用。这三个必须修完才能放行。

---

## 2. 全用例裁决汇总（V1–V14，按编号排序）

| # | 用例 | Phase | Verdict | 分类 | 一句话原因 |
|---|------|-------|---------|------|-----------|
| 1 | V1 | B | 🟢 PASS | 真实裁决 | 三步断言全通过（visible→hidden→visible），无 ShowWindow 降级 |
| 2 | V2A | A | 🔴 FAIL | 真实缺陷 | 手动支线重启后图标仍 hidden；SessionEnding 未触发恢复 |
| 3 | V2B | A | 🟢 PASS | 真实裁决 | 自启支线全通过 |
| 4 | V3 | B | 🟡 BLOCKED | 基础设施 | tray-icon-not-found（UIA 找不到托盘图标），前置步骤已通过 |
| 5 | V4 | B | 🟡 BLOCKED | 基础设施 | 同 V3；Restore=false→PASS |
| 6 | V5 | B | 🟡 BLOCKED | 基础设施 | 自动化部分全通过；托盘文案三态与双闪视觉判据需人工 |
| 7 | V6 | B | 🔴 FAIL | **真实缺陷** | mpv.exe 缺失于 `C:\gstack\app\`，专注/演示场景失败 |
| 8 | V7 | B | 🟢 PASS | 真实裁决 | 场景意图持久化通过 |
| 9 | V8 | B | 🟢 PASS | 真实裁决 | WallpaperLibrary + 两个固定壁纸就位 |
| 10 | V9 | B | 🟠 FAIL | **需人工复核** | harness 报 P1-7 回归，但前置条件（Unknown 态）未满足，疑似误报 |
| 11 | V10 | B | 🟡 BLOCKED | 基础设施 | 成功态反馈已核对；过渡文案与卡死判据需人工 |
| 12 | V11A | A | 🔴 FAIL | **真实缺陷** | SessionEnding 闩锁命中 0 次，关机后桌面空 |
| 13 | V11B | A | 🟡 BLOCKED | 基础设施 | `--background` 重启路径未发生 |
| 14 | V12 | A | 🟡 BLOCKED | 基础设施 | 注销后 420s 未自动登录回会话 |
| 15 | V13 | B | ⬜ NA | 基础设施 | VMware 单虚拟显示器，无法测多屏 |
| 16 | V14 | A | 🔴 FAIL | **真实缺陷** | 进程强杀后 settings.json 损坏 |

**统计**：PASS=4 | FAIL(确认)=4 | FAIL(待复核)=1 | BLOCKED=6 | NA=1

---

## 3. 真实产品缺陷详述

### 🔴 V11A — SessionEnding 关机恢复未生效（P0）

- **现象**：真实关机→重登录后图标未恢复，桌面空
- **证据**：日志**无任何 SessionEnding 行**；"退出（系统关机）：恢复桌面图标"闩锁命中 **0 次**（期望 1）
- **关联发现**：`DesktopSuite.exe` 曾否决 `shutdown /r /t 0`（无 `/f` 时 300s 未下线）→ 说明处理器**注册了且被调用了**，只是没走完恢复逻辑就返回了 cancel
- **排查方向**：处理器体内为什么中断（异常、阻塞、或错误返回值），而非"为什么没注册"
- **证据路径**：`evidence\20260806-124400\V11A\`

### 🔴 V14 — 异常退出后 settings.json 损坏（P0）

- **现象**：进程强杀后 `settings.json` 损坏（"未损坏"步骤 high 失败）
- **其余步骤**：图标保持隐藏 PASS、intent 落盘 PASS、重启不卡死 PASS
- **建议**：原子写（write-temp→rename）+ 启动时校验 + 损坏自动备份恢复
- **证据路径**：`evidence\20260806-124400\V14\`

### 🔴 V6 — mpv.exe 缺失导致视频壁纸不可用（P1）

- **现象**：「专注」「演示」场景应用失败，错误信息 `mpv.exe was not found`
- **根因**：`mpv.exe` 未包含在 `C:\gstack\app\` 部署包中；壁纸库文件本身存在（milkyway-1.mp4 21MB、night-city-1.mp4 43MB）
- **通过部分**：「日常」场景全部通过（不依赖视频壁纸）
- **修复**：将 mpv.exe 打入应用输出目录或加入 PATH
- **证据路径**：`evidence\20260807-081952\V6\`

### 🔴 V2A — 手动支线重启后图标未恢复（P1，需产品确认）

- **现象**：手动启动支线重启后图标仍 hidden（合法输出 exit=1/hidden/ws-visible-clear）
- **核心问题**：手动支线关机时 app 未运行，无法触发 SessionEnding 恢复
- **需产品负责人拍板**：手动支线的恢复时机预期是什么？（关机时恢复 vs 下次启动时恢复）
- **证据路径**：`evidence\20260806-124400\V2A\`

---

## 4. 疑似误报：V9 — Explorer 崩溃恢复

- **harness 裁决**：FAIL（critical，P1-7 回归：intent False→True）
- **实际情况**：杀死 Explorer 后 Windows 10 在 ~12 秒内自动重启，断言返回 `visible` 而非预期的 `unknown` → 前置条件未满足 → P1-7 检查在 visible 态下执行，intent 变更属正常行为
- **建议**：在能稳定制造 Unknown 态的环境上重跑（如禁用 Explorer 自动重启），或人工复核
- **证据路径**：`evidence\20260807-083246\V9\`

---

## 5. 基础设施 BLOCKED 汇总

| 用例 | BLOCKED 原因 | 已通过的自动化步骤 | 补验建议 |
|------|-------------|-------------------|---------|
| V3 | UIA 找不到托盘图标 | 自启=false→PASS、退出前 hidden→PASS、RestoreIconsOnExit=true→PASS | 改进 UIA 定位或人工截图 |
| V4 | 同 V3 | RestoreIconsOnExit=false→PASS、退出前 hidden→PASS | 同 V3 |
| V5 | 托盘文案三态与双闪需人工视觉判读 | 复选框 On→hidden→PASS、意图=True→PASS | 人工看截图/录屏 |
| V10 | 过渡文案与卡死判据需人工 | 成功态隐藏/显示 WM_COMMAND 0x7402→PASS×2 | 人工看截图/录屏 |
| V11B | `--background` 重启路径未发生 | 前置步骤通过 | 修复重启机制后重跑 |
| V12 | 注销后 420s 未自动登录 | 注销前产品状态正确 | 修复自动登录配置后重跑 |
| V13 | VMware 单虚拟显示器 | — | 需多屏环境或物理机 |

> **V3/V4 补充**：V11A/V12（Phase A）已自动覆盖 RestoreIconsOnTeardown 同段代码（含意图保护），只是触发源不同（重启/注销 vs 托盘退出）。

---

## 6. Harness 四层缺陷修复记录（已完成）

| # | 缺陷 | 修复 | 验证状态 |
|---|------|------|---------|
| ① | ConvertTo-Json 挂死 | 改用纯 PS 序列化器（Collect-State.ps1 `0BF1143F`） | ✅ 15 成员 9s 落盘 exit 0 |
| ② | Wait-InteractiveSession 排在 Invoke-Deploy 前 | 前置 Invoke-Deploy（L1811-1819 / L1827-1835） | ✅ 零 stale-build |
| ③ | L775 Assert-Setting 缺内层括号 | `if ((Test-Prop ...) -and (Get-Prop ...))` | ✅ settings 不再误报 |
| ④ | shutdown /r /t 0 缺 /f | `vmrun reset hard`（演进版，logoff 仍保留 /f） | ✅ 不再被否决 |

---

## ✅ 行动清单

| # | 行动 | 负责方 | 紧急度 | 期望完成 |
|---|------|--------|--------|---------|
| 1 | 修复 V11A：SessionEnding 处理器体内中断（注册了但未走完恢复逻辑） | 产品团队 | P0 | 下个迭代 |
| 2 | 修复 V14：settings.json 原子写 + 启动校验 + 损坏备份恢复 | 产品团队 | P0 | 下个迭代 |
| 3 | 修复 V6：将 mpv.exe 打入 `C:\gstack\app\` 或加入 PATH | 产品/部署 | P1 | 立即 |
| 4 | 确认 V2A：手动支线关机时恢复时机预期（关机时 vs 下次启动时） | 产品负责人 | P1 | 本周 |
| 5 | 补验 V9：在禁用 Explorer 自动重启的环境上重跑 | QA | P2 | 下轮验证 |
| 6 | 补验 V3/V4/V5/V10：人工截图托盘文案与过渡反馈 | QA | P2 | 下轮验证 |
| 7 | 修复 V11B/V12 基础设施后重跑 | QA 基础设施 | P2 | 下轮验证 |
| 8 | 删除死代码 `Coerce-Handle`（L223，0 调用点） | 工程清理 | P3 |  opportune |

---

## ⚠️ 待完善 / 已知局限

- **V13 多屏测试**：VMware 单虚拟显示器无法构造 ≥2 屏场景，需多屏环境或物理机验证
- **V9 Explorer 自动重启**：Windows 10 默认行为导致 Unknown 态难以稳定制造，需特殊环境配置
- **V3/V4 托盘图标**：UIA 在该 Win10 环境下找不到托盘图标，可能需要调整 UIA 查询策略或改用坐标点击
- **D: 盘 VM**：`D:\Program Files\VMwar_xitongwenjian\win10\Windows 10 x64.vmx`（pid 15392）一直在跑，与本次验证无关，归属待用户确认
- **Phase B 报告交叉引用错误**：gstack-qa-lead 的 Phase B 报告"放行判定"段把 V12 标为 PASS、V2B 标为 FAIL、V11A 标为 BLOCKED，本报告已修正

---

## 📚 成员产出索引

- **gstack-qa-lead（Phase A 报告）**：`deliverables/gstack/pre-release-check-desktopsuite-phase3-2026-08-06.md`
- **gstack-qa-lead（Phase B 报告）**：`deliverables/gstack/pre-release-check-desktopsuite-phase3-phaseB-2026-08-07.md`
- **Phase A summary.json**：`tools/vm-validation/evidence/20260806-124400/summary.json`
- **Phase B summary.json**：`tools/vm-validation/evidence/20260807-phaseB-consolidated/summary.json`
- **Phase B Batch 1 证据**：`tools/vm-validation/evidence/20260807-081952/`（V1/V5/V10/V8/V6）
- **Phase B Batch 2 证据**：`tools/vm-validation/evidence/20260807-083246/`（V7/V3/V4/V9/V13）

---

> 本报告由软件工坊 AI 协作生成，关键决策请由工程负责人复核。

# DesktopSuite Phase 3 真机验证报告（V2A/V2B/V11A/V11B/V12/V14）

**日期**：2026-08-06
**场景**：QA 测试 + 发布（Phase 3 真机回归门禁）
**参与成员**：gstack-qa-lead-2（harness 修复 + 六用例执行）、qa-v12-deploy（断言根因定位 + 真机验证）、qa-v12-smoke（Collector 源校验 + 独立复测待命）

---

## 📌 TL;DR（执行摘要）

- 整体结论：🔴 **No-Go（放行阻断）**
- 真实裁决：**1 PASS / 3 FAIL / 2 BLOCKED**
- 关键发现：测试基础设施多层缺陷已全部修复，本次结果**首次可信**；暴露**两个真实产品缺陷**（V11A SessionEnding 未生效、V14 异常退出致 settings.json 损坏）与**两个基础设施 BLOCKED**（V11B 重启未发生、V12 注销后自动登录未恢复）。
- 阻塞项：V11A、V14 需产品团队修复；V11B、V12 需环境/脚本修复后复测；V2A 需产品负责人确认预期。
- 下一步：产品团队接 V11A/V14；环境侧修复自动登录与 --background 重启路径后复测 V11B/V12。

---

## 🎯 核心结论卡片

| 项目 | 内容 |
|------|------|
| Go / No-Go | 🔴 No-Go |
| 严重度分布 | 🔴 2（真实产品缺陷）/ 🟠 1（预期待澄清）/ 🟡 2（基础设施 BLOCKED）/ 🟢 1（PASS） |
| 关键行动项 | 6 条 |
| 建议负责人 | 产品团队（V11A/V14）、QA 基础设施（V11B/V12 自动登录）、产品负责人（V2A 预期） |

---

## 1. 各成员核心结论

### 🔧 gstack-qa-lead-2（harness 修复 + 六用例执行）
- 核心判断：Phase A 首次完整跑暴露 harness 共 4 处致命缺陷（ConvertTo-Json 挂死、部署排序、Assert-Setting 括号缺失、重启缺 /f），全部修复后（harness sha `A60FC9B8112FEC85`）重跑，产出首个可信裁决。
- 关键建议：基础快照仍含坏版断言脚本，建议用修好的脚本重拍快照，消除探针单点故障。

### 🔍 qa-v12-deploy（断言根因 + 真机验证）
- 核心判断：断言阻塞根因 = guest L67 `[int]$Samples` 与 L379 `$samples` 大小写撞名 → `ArgumentTransformationMetadataException`；已实机部署 host `3155E5DE` + SHA 校验 + 实时会话验证 `exit 0`/`visible`。`Coerce-Handle` 为死代码（基于错误理论），建议删除。
- 关键建议：每用例跑前必须重部署 + SHA 校验（基础快照含坏版）。

### ✅ qa-v12-smoke（Collector 源校验）
- 核心判断：Host `Collect-State.ps1`（`0BF1143F`）SHA 复核一致、可信；独立复测因 E: 被占用延后。BLOCKED 仅适用于 stale `0C4EB1F9`。
- 关键建议：等 E: 释放后补一次 collector 独立复测以双层保险。

---

## 2. 综合审查发现（去重合并后按严重度排序）

| # | 严重度 | 类别 | 位置 | 问题描述 | 建议 | 来源 |
|---|--------|------|------|---------|------|------|
| 1 | 🔴 | 产品/回归 | V11A §5 V11-A | 真实关机→重登录后图标未恢复（期望 visible 实际 hidden）；日志**无任何 SessionEnding 行**；"退出（系统关机）：恢复桌面图标"闩锁命中 **0 次**（期望 1）。SessionEnding 处理器未生效 → 用户关机后拿空桌面。 | 产品团队排查 SessionEnding / WM_QUERYENDSESSION 处理与恢复闩锁 | gstack-qa-lead-2 |
| 2 | 🔴 | 产品/数据完整性 | V14 §5 V14 | 进程被强杀后 `settings.json` **损坏**（"settings.json 未损坏"步骤 high 失败）。其余（图标保持隐藏、intent 落盘、重启不卡死）均 PASS。 | 产品团队修复异常退出时的原子写/恢复；settings.json 加校验与备份 | gstack-qa-lead-2 |
| 3 | 🟠 | 产品预期/测试设计 | V2A §5 V2-A | 手动启动支线下，重启后（启动程序前）图标仍为 hidden（exitCode=1 合法输出），用例期望 visible（基于退出恢复语义）。手动支线关机时 app 未运行，无法触发 SessionEnding 恢复。 | 产品负责人确认手动支线预期：是否应在下次手动启动时才恢复 | gstack-qa-lead-2 |
| 4 | 🟡 | 基础设施 | V11B §5 V11-B | "首次重启进入 --background 态"步骤 BLOCKED：重启未发生（无断言数据，产品行为未触达）。 | 排查 --background 支线的重启机制与 Invoke-Deploy 是否成功；修复后复测 | gstack-qa-lead-2 |
| 5 | 🟡 | 基础设施/环境 | V12 §5 V12 | 注销后未能在 420s 内自动登录回交互会话（"请确认自动登录已配置"）。注销前产品状态正确（图标隐藏、RestoreIconsOnExit=True）。 | 修复自动登录快照/配置后复测 | gstack-qa-lead-2 |
| 6 | 🟢 | — | V2B §5 V2-B | 隐藏意图跨重启保留（--background 自启支线）PASS | — | gstack-qa-lead-2 |

> **判读铁律（qa-v12-deploy）**：好版断言返回 `exitCode=0/visible/ws-visible-set` 或 `exitCode=1/hidden/ws-visible-clear`（均合法）；坏版返回 `exitCode=4/script-exception/ArrayList→Int32`。本次所有断言均为合法输出 → `Invoke-Deploy` 成功、好版确实运行，**无 stale-build 产物**。

### 🔗 交叉关联分析：V11A 根因假设收窄（主理人汇编）

发现 #1（V11A）与「已知局限」中的副发现（`DesktopSuite.exe` 否决系统关机）**是同一根因的两面**，合并后可显著收窄排查范围：

| 观测 | 来源 | 单独看的解读 |
|------|------|------------|
| 日志无任何 SessionEnding 行；恢复闩锁命中 0 次 | V11A 断言数据 | "SessionEnding 处理器未生效" |
| `shutdown /r /t 0`（无 `/f`）被否决，300s 未下线；加 `/f` 才能重启 | harness 缺陷④ 排查过程 | "app 阻塞了关机" |

**矛盾点**：若 SessionEnding / `WM_QUERYENDSESSION` 处理器**根本没注册**，Windows 不会赋予该进程否决关机的能力 —— 无处理器的进程会被直接终止。**能否决，恰恰证明处理器已注册且已被系统调用。**

**修正后的根因假设**：处理器已注册并被触发，但**在体内中断，未走到恢复逻辑就返回了"拒绝关机"**。三个候选方向（按可能性排序）：

1. **处理器体内抛异常** → 恢复代码未执行，日志因异常未写出（解释"无 SessionEnding 行"），同时消息未正确应答 → 系统判定为无响应/否决。
2. **处理器阻塞或超时** → 恢复逻辑卡在耗时操作（如同步 I/O、等待 UI 线程），未在系统给的窗口期内返回。
3. **返回值/应答语义写反** → 错误地返回了 cancel 而非 allow，恢复逻辑被短路跳过。

**给产品团队的排查建议**：不要从"为什么没注册"入手（该方向已被否决）。应在处理器入口/出口加无缓冲落盘日志（异常路径也要写），复现关机场景后查它执行到哪一步中断。若日志依然为空，重点查方向 1（异常吞噬 + 日志缓冲未 flush 即进程终止）。

> 该关联为主理人汇编所得，两位成员分别观测到其中一面，未做交叉。假设本身**尚未经实验验证**，请产品团队以此为起点而非结论。

---

## ✅ 行动清单

| # | 行动 | 负责方 | 紧急度 | 期望完成 |
|---|------|--------|--------|---------|
| 1 | 修复 SessionEnding 关机恢复（V11A）：处理器已注册且被调用（由"否决关机"反证），需查其**体内为何中断**——优先在入口/出口加无缓冲落盘日志，排查异常吞噬 / 阻塞超时 / 应答语义写反，详见 §2 交叉关联分析 | 产品团队 | P0 | 下个迭代 |
| 2 | 修复异常退出 `settings.json` 损坏（V14）：原子写（临时文件+rename）或启动校验+备份 | 产品团队 | P0 | 下个迭代 |
| 3 | 确认 V2A 手动支线预期（恢复时机） | 产品负责人 | P1 | 本周 |
| 4 | 修复 V11B --background 重启路径 + V12 注销后自动登录（环境/脚本）后复测 | QA 基础设施 | P1 | 本周 |
| 5 | 用修好脚本重拍基础快照，消除探针单点故障；删除死代码 `Coerce-Handle` | QA 基础设施 | P2 | 本周 |
| 6 | E: 释放后补一次 collector 独立复测（`0BF1143F`） | qa-v12-smoke | P2 | E: 空闲时 |

---

## ⚠️ 待完善 / 已知局限

- V11B / V12 为基础设施 BLOCKED，未触达产品行为，其真实 PASS/FAIL 待环境修复后复测确定。
- V2A 为真实断言结果，但性质介于"产品缺陷"与"测试预期偏差"之间，需产品负责人拍板。
- 仅 qa-v12-smoke 的独立 collector 复测（`0BF1143F`）因 E: 占用延后，尚未执行；当前结论依赖 gstack-qa-lead-2 单次完整跑。
- 基础快照仍含坏版断言脚本（`BA8FD6AD`），`-RevertBetweenCases` 依赖 `Invoke-Deploy` 覆盖，存在部署失败即 stale 的风险。
- 副发现：gstack-qa-lead-2 观测到 `DesktopSuite.exe` 疑似否决系统关机（`shutdown /r /t 0` 无 /f 时 300s 未下线），已用 /f 绕过；该行为是否预期需产品确认（可能与 V11A SessionEnding 处理相关）。

---

## 📚 成员产出索引

- gstack-qa-lead-2：
  - 完整裁决 `tools\vm-validation\evidence\20260806-124400\summary.json`
  - 被污染轮（勿采信）`tools\vm-validation\evidence\20260806-110336\summary.json`
  - harness 修复 `Run-Validation.ps1`（sha `A60FC9B8112FEC85`）
- qa-v12-deploy：断言修复真机验证 `tools\vm-validation\_pull\assert-fixverify.json`、`tools\vm-validation\_verify2.txt`
- qa-v12-smoke：host `Collect-State.ps1`（`0BF1143F`）SHA 复核记录

---

> 本报告由软件工坊 AI 协作生成，关键决策请由工程负责人复核。

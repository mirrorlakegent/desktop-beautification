# 图标整理空白桌面 · 根因重诊与架构修复复盘

**日期**：2026-08-09
**场景**：调试复盘（根因重诊 + 架构修复 + 发布前复验）
**参与成员**：排障手（investigator） + 质量门神（qa-lead）

---

## 📌 TL;DR（执行摘要）

- 整体结论：🟡 有条件通过（条件 Go）
- 整体结论说明：用户二次反馈"启用图标整理后图标消失、但自绘围栏容器不出现"——上一次的"关掉分层窗口"修复**没有命中真正的根因**。本次排障手定位到真正的病根并做了架构级重写，质量门神静态复验通过并补了一处发布前守卫。
- 真正的根因：**把一个 WPF `Window` 用 `SetParent` 改挂到桌面 shell 下这种写法本身不可靠**（DWM 不合成被改父窗口的内容），不是上次以为的"分层窗口"。
- 阻塞项数量：0（无硬阻塞）
- 下一步：打出新的自包含 exe（已完成），请用户在真机复验围栏可见性 / 点击穿透 / 磁贴可点；保留 git 回退与开关作止血手段。

---

## 🎯 核心结论卡片

| 项目 | 内容 |
|------|------|
| Go / No-Go | 🟡 条件 Go（无硬阻塞，上线前补重入守卫——已补） |
| 严重度分布 | 🔴 0 / 🟠 1（重入泄漏，已修复）/ 🟡 2（Z-order 未显式置顶、DPI 变更闪烁）/ 🟢 1（多屏混合 DPI 已知限制） |
| 关键行动项 | 4 条（见下） |
| 建议负责人 | 主理人（打包/发布/回滚）+ 用户（真机验收） |

---

## 1. 各成员核心结论

### 🔧 排障手（调试与根因）
- 核心判断：之前修复只把 `AllowsTransparency` 从 `true` 改成 `false`（针对 WS_EX_LAYERED 分层窗口），但故障根源不是分层窗口，而是"**被改父的 WPF Window**"——`SetParent` 把顶层窗口变成 shell 子窗口后，DWM 经常不合成其内容，窗口建了、图标藏了，但它就是不绘制，于是桌面空白、看不到任何围栏盒子。证据链：EnableFences 必然走到 `new FenceLayer()`+`Show()`（窗口一定创建）、挂载目标 `GetParent(DefView)` 在两种 shell 形态下都正确、坐标/region 数学自洽、而同样"裸子窗口"的壁纸能正常显示——直指 Window+SetParent 这一写法本身。
- 关键建议：放弃"创建顶层 Window 再 SetParent"的套路，改为与已验证的壁纸窗口同款——用 `HwndSource` 把 WPF 视觉树寄宿进一个"从创建起父窗口就是桌面宿主（GetParent(DefView)/Progman）"的裸子窗口。已落地在 `FenceLayer.cs`（去掉 `: Window` 与 `MountToDesktop`，新增 `CreateDesktopWindow()` 用 `HwndSourceParameters.ParentWindow=host`），公开 API 不变，`MainWindow`/`FenceBox` 零改动。Release 编译 0 错误 0 警告。

### ✅ 质量门神（QA测试与发布）
- 核心判断：修复真实自洽、与已验证路径同构、API 兼容、无旧引用残留；建议**条件 Go**。独立编译验证 0/0。明确四项风险：① `Show`/`CreateDesktopWindow` 无重入守卫，启动重试路径与手动开关竞态下会把 `_source` 覆盖、首个 HWND 泄漏（中危）；② Z-order 依赖"新建子窗口置顶"，未显式 `SetWindowPos`，需真机确认盒子恒在图标层之上（低-中危）；③ `WM_DISPLAYCHANGE`/`WM_DPICHANGED` 重建有瞬时闪烁、且依赖 PerMonitor DPI 清单（低危）；④ 多屏混合 DPI 用单一 `_dpiX/_dpiY` 会轻微错位（已知限制，非回归）。
- 关键建议：上线前补一处重入守卫（`if(_source!=null) return;`）——**主理人已落实**；其余以"多屏 + 高分屏优先"的 48h Canary 验证；保留 git 回退与 `FencesEnabled=false` / 新增 `FencesV2Enabled` 开关作快速止血阀。

---

## 2. 综合审查发现（去重合并后按严重度排序）

| # | 严重度 | 类别 | 位置 | 问题描述 | 建议 | 来源成员 |
|---|--------|------|------|---------|------|---------|
| 1 | 🟠 | 资源泄漏 | `FenceLayer.CreateDesktopWindow` | 无重入守卫，启动重试与手动开关竞态下 `_source` 被覆盖，首个 HwndSource/HWND 从未 Dispose | 加 `if(_source!=null) return;` 守卫（已修复） | 质量门神 |
| 2 | 🟡 | 渲染/Z-order | `FenceLayer.CreateDesktopWindow` | 未显式 `SetWindowPos(HWND_TOP)`，依赖新建子窗口置顶，需真机确认盒子恒在隐藏图标层之上 | 真机验收；若被遮挡则补显式置顶 | 质量门神 |
| 3 | 🟡 | 稳定性 | `FenceLayer.WndProcLayer` | 显示/DPI 变化重建有瞬时闪烁，`TransformToDevice` 可能滞后一帧 | 低优先，后续优化 | 质量门神 |
| 4 | 🟢 | 兼容性 | 多屏混合 DPI | 单一 `_dpiX/_dpiY` 在混合 DPI 多屏下轻微错位 | 已知限制（非回归），后续 PerMonitorV2 | 质量门神 |

> 前文"根因：WS_EX_LAYERED 分层窗口"在上一次复盘（debug-fences-blank-2026-08-09.md）中被误判，本次已纠正为"Window+SetParent 改父不可靠"。

---

## ✅ 行动清单（至少 3 条具体可执行项）

| # | 行动 | 负责方 | 紧急度 | 期望完成 |
|---|------|--------|--------|---------|
| 1 | 落重入守卫（`if(_source!=null) return;`）并重新打包自包含 exe | 主理人（已完成） | P0 | 2026-08-09 |
| 2 | 真机复验：开"图标整理"→ 原生图标隐藏 **且** 围栏盒子出现；盒子外点击穿透、双击打开文件；"＋新建分类"磁贴可见可点；拖头/拖文件改分类/双击改名/折叠均正常持久化 | 用户 | P0 | 真机验收当天 |
| 3 | 真机确认 Z-order：盒子恒在隐藏图标层之上；若被遮挡补 `SetWindowPos(HWND_TOP)` | 主理人（按验收反馈） | P1 | 反馈后 1 日内 |
| 4 | 48h Canary（多屏/高分屏真机优先），监控 `HostLog` 中 `FenceLayer.* 失败`；保留 git revert 与 `FencesEnabled=false` 作止血 | 主理人 + 用户 | P1 | 发布后 48h |

---

## ⚠️ 待完善 / 已知局限

- 沙箱无交互桌面，**无法 GUI 真机验收**；本次仅静态通读 + Release 编译验证（0/0）。围栏可见性、点击穿透、Z-order、拖拽手感必须由用户在真机确认。
- 未在 `WallpaperChildWindow` 之外显式设定盒子 Z-order；若真机发现盒子被壁纸/图标层遮挡，需补 `SetWindowPos(HWND_TOP)`。
- `WM_DISPLAYCHANGE`/`WM_DPICHANGED` 触发重建有瞬时闪烁（低危）。
- 多屏混合 DPI 下轻微错位（已知限制，非回归）；PerMonitorV2 支持待后续 Phase 6。
- 回滚预案：git revert 到本次重写之前的 commit（`2660998`）；或 `layout.json` 置 `FencesEnabled=false` / 新增 `FencesV2Enabled=false` 跳过启用。

---

## 📚 成员产出索引

- gstack-investigator（排障手）原始产出：FenceLayer.cs 重写 + 根因报告（本次对话 Agent 返回，已汇编）
- gstack-qa-lead（质量门神）原始产出：发布前复验 + Ship checklist + 回滚/Canary（本次对话 Agent 返回，已汇编）
- 编译日志：`_build_refix.txt`（investigator Release 编译 0/0）、`_build_qa.txt`（qa 独立编译 0/0）、`_build_publish_refix3.txt`（打包 EXIT=0）
- 发布产物：`src/DesktopSuite/bin/x64/Release/self-contained/DesktopSuite.exe`（自带 .NET 8，2026-08-09 21:11）
- 启动器：`D:\WorkBuddy\桌面美化\桌面整理一键启动.vbs`

---

> 本报告由软件工坊 AI 协作生成，关键决策请由工程负责人复核。

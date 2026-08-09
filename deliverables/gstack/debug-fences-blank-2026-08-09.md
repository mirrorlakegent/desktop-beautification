# Fences 空白桌面 Bug 调试复盘

**日期**：2026-08-09
**场景**：调试复盘（runtime 渲染 bug）
**参与成员**：排障手（gstack-investigator） + 质量门神（gstack-qa-lead）

---

## 📌 TL;DR（执行摘要）
- 整体结论：🟢 已修复（根因定位精准，修复经独立复验通过，已重打包 exe 并推送双远程）
- 阻塞项数量：0
- 现象：启用「图标整理 / Fences」后原生图标消失，但自绘围栏容器（盒子）不出现 → 空白桌面
- 根因：FenceLayer 用 `AllowsTransparency=true`（`WS_EX_LAYERED` 分层窗口）却被 `SetParent` 挂为桌面 shell 子窗口，DWM 不绘制分层子窗口 → 盒子不可见
- 下一步：用户真机重跑自包含 exe 验收

---

## 🎯 核心结论卡片

| 项目 | 内容 |
|------|------|
| Go / No-Go | 🟢 Go（修复已发布） |
| 严重度分布 | 🔴 0 / 🟠 0 / 🟡 1（收尾项：新建分类磁贴纳入点击区，已修）/ 🟢 完成 |
| 关键行动项 | 3 条（见下） |
| 建议负责人 | 用户真机验收 |

---

## 1. 各成员核心结论

### 🔧 排障手（根因分析 + 修复）
- 核心判断：空白桌面 = 分层窗口被 `SetParent` 挂为子窗口后 DWM 不绘制。FenceLayer 构造函数 `AllowsTransparency=true` 生成 `WS_EX_LAYERED`，`MountToDesktop()` 第 240 行 `SetParent` 挂到桌面 DefView 宿主——两者组合使盒子永远不渲染。代码库内 `WorkerWHost.cs` 已证明正确范式是普通（非分层）窗口挂桌面。
- 关键建议 / 实施：① `AllowsTransparency=false`；② `Background` 改为不透明深底（`Brushes.Transparent` 在非分层窗口会变纯黑）；③ `FenceBox` 面板画刷 alpha 改 255；④ 顺带把「＋新建分类」磁贴矩形并入 `ApplyRegion` 命中区（此前被裁剪不可点）。构建 0 错误/0 警告。

### ✅ 质量门神（独立静态复验）
- 核心判断：6 项检查全部 CONFIRMED、0 回归、ship-ready。重点确认 `Background` 已彻底不是 `Transparent`/`FromArgb` 半透明（否则会再现黑块/不渲染），`SetParent` 挂载逻辑与 `WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE` 块完好，`SetWindowRgn` 点击穿透在「非分层」窗口上等价有效，虚拟化（无文件移动、双击仅 `Process.Start` 原文件）不受影响。
- 关键建议：多显示器定位仍为近似（已知 Phase-6 项，非回归）；并指出 add-tile 此前被裁剪——已由排障手在本次一并修复。

---

## 2. 综合审查发现（去重合并后按严重度排序）

| # | 严重度 | 类别 | 位置 | 问题描述 | 建议 | 来源成员 |
|---|--------|------|------|---------|------|---------|
| 1 | 🔴→🟢 | 渲染/Win32 | FenceLayer.cs:62,67 | 分层窗口被 `SetParent` 挂为子窗口后 DWM 不绘制 → 盒子不可见（空白桌面）。已修复：`AllowsTransparency=false` | 改非分层窗口 | 排障手 |
| 2 | 🟡→🟢 | 背景 | FenceLayer.cs:72 / FenceBox.cs:52,60 | 非分层窗口无 alpha 通道，`Brushes.Transparent`/半透明画刷会渲染成黑块。已修复：改不透明 RGB 画刷 | 同 #1 | 排障手 |
| 3 | 🟡→🟢 | 交互 | FenceLayer.cs:312-335 | 「＋新建分类」磁贴位于盒子并集之外，被 `SetWindowRgn` 裁剪 → 不可见不可点（预存问题，修复后暴露）。已修复：并入命中区 | 同 #1 | 排障手 |
| 4 | 🟢 | 已知限制 | FenceLayer.cs:30-31 | 多显示器定位近似（Phase-6 项），本次未动 | 后续 Phase 处理 | 质量门神 |

---

## ✅ 行动清单

| # | 行动 | 负责方 | 紧急度 | 期望完成 |
|---|------|--------|--------|---------|
| 1 | 真机重跑自包含 exe，确认启用图标整理后盒子可见、可拖拽归类、可新建分类 | 用户 | P0 | 立即 |
| 2 | 真机验证盒子外点击穿透原生桌面（右键/框选正常）、多显示器下位置 | 用户 | P1 | 验收时 |
| 3 | 后续 Phase：多显示器精确定位、文件监听增量归类（Phase 4）、系统托盘入口（Phase 5）、Explorer 重启恢复 + PerMonitorV2（Phase 6） | 工程 | P2 | 计划排期 |

---

## ⚠️ 待完善 / 已知局限

- 多显示器精确定位、DPI 缩放为近似（Phase-6 项），非本次回归。
- 沙箱无交互桌面，本次为编译验证 + 静态复验；真实拖拽手感、点击穿透、多屏行为需真机确认。
- 修复已重打包自包含 exe（含 .NET 8 运行时，无需本机装运行时）并推送 Gitee + GitHub。

---

## 📚 成员产出索引

- gstack-investigator（排障手）原始产出：根因确认 + 靶向修复（AllowsTransparency=false / 不透明背景 / 磁贴命中区）+ 构建 0/0 报告
- gstack-qa-lead（质量门神）原始产出：6 项静态复验全 CONFIRMED、0 回归、ship-ready 结论

---

> 本报告由软件工坊 AI 协作生成，关键决策请由工程负责人复核。

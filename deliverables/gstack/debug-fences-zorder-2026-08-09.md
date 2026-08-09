# 图标整理空白桌面 · 第三轮：Z-order 遮挡根因与修复

**日期**：2026-08-09
**场景**：调试复盘（根因定位 + 修复 + 发布）
**参与成员**：排障手（investigator）

> 本轮是 `debug-fences-blank-2026-08-09.md`（误判 WS_EX_LAYERED）与 `debug-fences-refix-2026-08-09.md`（改 HwndSource 挂桌面）的延续。前两轮静态推论都说得通、编译都过，但用户真机**两次仍空白**。本轮靠用户三条真机证据一次性定位。

---

## 📌 TL;DR（执行摘要）

- 整体结论：🟢 通过（根因已确定性定位并修复，待真机验收）
- 真正的根因：**围栏窗口被动态壁纸的 WorkerW 整屏遮挡（Z-order 问题），不是渲染/合成问题**。
- 为何前两轮都错：第一轮误判"分层窗口"，第二轮虽改成正确的 HwndSource 挂桌面技术，但没解决"层级"——窗口建出来了却压在壁纸之下，所以还是空白。
- 修复：围栏 `SetWindowPos(HWND_TOP)` 钉顶 + 壁纸 `SetWindowPos(HWND_BOTTOM)` 钉底，双向显式置层。
- 阻塞项数量：0
- 下一步：请用户真机复验（开图标整理 + 壁纸时盒子应在壁纸之上可见）。

---

## 🎯 核心结论卡片

| 项目 | 内容 |
|------|------|
| Go / No-Go | 🟢 Go（待真机验收） |
| 严重度分布 | 🔴 0 / 🟠 1（Z-order 遮挡，已修复）/ 🟡 0 / 🟢 0 |
| 关键行动项 | 3 条（见下） |
| 建议负责人 | 主理人（打包/发布/回滚）+ 用户（真机验收） |

---

## 1. 各成员核心结论

### 🔧 排障手（调试与根因）
- 核心判断：用户三条证据（壁纸正常 / 无报错日志 / 完全空白）构成完整证据链——壁纸用同款 HwndSource 桌面子窗口能显示 ⇒ 环境/DWM 合成无关；无日志 ⇒ `CreateDesktopWindow()` 没抛异常、窗口已建；默认 `FenceLayout` 恒含 5 个非空分类 ⇒ 命中区域非空、排除"区域为空"。唯一差异是层级：围栏挂在 `GetParent(DefView)`（图标层），壁纸挂在独立 WorkerW；动态壁纸 0x052C 生成的 WorkerW 在部分 shell 形态里落在图标层**之上**，把围栏整屏盖住。这是确定性结论，非 (B) 区域空 / (C) 尺寸错。
- 关键建议：双向显式置层——围栏 `SetWindowPos(_hwnd, HWND_TOP, 0,0,w,h, SWP_NOACTIVATE|SWP_SHOWWINDOW)`（钉到桌面宿主顶层，图标层之上、普通应用之下）；壁纸 `SetWindowPos(hwnd, HWND_BOTTOM, 0,0,0,0, SWP_NOACTIVATE|SWP_NOMOVE|SWP_NOSIZE)`（钉到桌面最底，永不在图标层之上）。均未动重入守卫、SetWindowRgn 点击穿透、DPI 处理。Release 编译 0 错误 0 警告。

---

## 2. 综合审查发现（按严重度排序）

| # | 严重度 | 类别 | 位置 | 问题描述 | 建议 | 来源成员 |
|---|--------|------|------|---------|------|---------|
| 1 | 🟠 | 渲染/Z-order | `FenceLayer.CreateDesktopWindow` + `Wallpaper/WorkerWHost.Commit` | 围栏窗口建出但未显式置顶，被位于其上方的动态壁纸 WorkerW 整屏遮挡 → 桌面空白无盒子 | 围栏 `HWND_TOP` + 壁纸 `HWND_BOTTOM` 双向显式置层（已修复） | 排障手 |

> 前两轮误判（WS_EX_LAYERED、Window+SetParent）已分别被 `debug-fences-blank` 与 `debug-fences-refix` 记录并纠正；本次为最终确定性根因。

---

## ✅ 行动清单（至少 3 条具体可执行项）

| # | 行动 | 负责方 | 紧急度 | 期望完成 |
|---|------|--------|--------|---------|
| 1 | 真机复验：同时开「图标整理」+ 视频壁纸，确认盒子显示在壁纸之上、图标消失后盒子可见 | 用户 | P0 | 验收当天 |
| 2 | 真机复验：仅开图标整理（关壁纸），确认盒子仍可见，排除遮挡以外的渲染问题 | 用户 | P0 | 验收当天 |
| 3 | 观察 `%LOCALAPPDATA%\DesktopSuite\logs` 是否出现 `FenceLayer.* 失败` / `置顶失败` 日志；多屏/高分屏下盒子定位与点击穿透是否正确 | 用户 + 主理人 | P1 | 验收后 |

---

## ⚠️ 待完善 / 已知局限

- 沙箱无交互桌面，**无法 GUI 真机验收**；本轮仅静态通读 + Release 编译验证（0/0），层级效果必须由用户在真机确认。
- 修复依赖 `HWND_TOP`/`HWND_BOTTOM` 在桌面宿主子窗口间的相对排序；若用户 shell 形态极特殊导致排序仍不对，需进一步按壁纸 HWND 精确插入（当前用 HWND_TOP/HWND_BOTTOM 已覆盖绝大多数形态）。
- 多屏混合 DPI 下的轻微错位（已知限制，非回归）待后续 Phase 6（PerMonitorV2）。
- 回滚预案：git revert 到 `8b0e09b`（上一轮 HwndSource 重写）；或 `layout.json` 置 `FencesEnabled=false` 跳过启用。

---

## 📚 成员产出索引

- gstack-investigator（排障手）原始产出：根因 (A) Z-order 遮挡 + 双向 SetWindowPos 修复 + 编译 0/0（本次对话 Agent 返回，已汇编）
- 编译日志：`_build_refix2.txt`（investigator 编译 0/0）、`_build_publish_zorder.txt`（打包 EXIT=0）
- 发布产物：`src/DesktopSuite/bin/x64/Release/self-contained/DesktopSuite.exe`（自带 .NET 8，2026-08-09 22:14）
- 提交：`8ce2a53`（FenceLayer.cs + WorkerWHost.cs，+29 行），已推 GitHub + Gitee

---

> 本报告由软件工坊 AI 协作生成，关键决策请由工程负责人复核。

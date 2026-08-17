# 桌面整理（Fences）根因锁定与纯 Win32 重写设计

**日期**：2026-08-11
**场景**：调试复盘 / 架构根因 / 重写设计
**参与成员**：主理人（沽思航） + 排障手（gstack-investigator）

---

## 📌 TL;DR（执行摘要）

- 整体结论：🟡 有条件通过（根因已 100% 锁定，待架构重写）
- 五轮空白 bug 的真因：**WPF `HwndSource` 作为桌面 WorkerW 子窗口时，其 D3D 重定向位图不被 DWM 合成**——与样式位、z-order、挂载点都无关。
- 实证的可用合成路径：壁纸宿主 `DSWallpaperHost` 用**纯 Win32 非 layered 子窗口** + mpv 的 D3D present 成功显示，证明该拓扑下"原生 Win32 窗口"能被 DWM 合成。
- 修复方向：将 FenceLayer 从 WPF 视觉树重写为**纯 Win32 窗口 + Direct2D 绘制**（沿用壁纸配方），保留布局模型与点击穿透逻辑。
- 下一步：待用户拍板渲染 API 与交付里程碑，排障手实现 → 构建部署真机验收。

---

## 🎯 核心结论卡片

| 项目 | 内容 |
|------|------|
| Go / No-Go | 🟡 条件 Go（架构重写方案已定，待实现验收） |
| 严重度分布 | 🔴 1（架构级根因：WPF-under-WorkerW 不合成）/ 🟢 0 / 🟡 0 / 🟢 0 |
| 关键行动项 | 3 条（窗口重写 / 绘制 / 交互解耦） |
| 建议负责人 | 排障手（实现）+ 主理人（构建部署验收） |

---

## 1. 各成员核心结论

### 🔧 排障手（调试与根因）
- 核心判断：**决定性日志（20:23 真机）证明 TOOLWINDOW 非根因**——清除后窗口属性完美（ex=0x08000000、visible、满屏、DWM 开、z-order 最顶），仍不可见。对比同父、同属性、可见的壁纸宿主 `DSWallpaperHost`（plain Win32），唯一差异是围栏为 WPF HwndSource（`cls=HwndWrapper[...]`）。故真因是 **WPF HwndSource 在桌面 WorkerW 下不被 DWM 合成**。
- 关键建议：弃用 WPF `HwndSource`/`Border`/`Canvas`/`FenceBox` 视觉树，改用与壁纸同源的纯 Win32 窗口（`CreateWindowEx` 非 layered 子窗口 + `HWND_TOP`），用 **Direct2D** 画盒子，保留 `FenceCategory` 布局模型与 `ApplyRegion` 命中区。绘制与命中区强制共用同一坐标函数。
- 工作量估计：**约 6–9 人日**，交互层（命中测试/拖拽/重命名/双击打开/折叠/新建/项归类）占主导（2.5–3.5 人日）。

---

## 2. 综合审查发现（去重合并后按严重度排序）

| # | 严重度 | 类别 | 位置 | 问题描述 | 建议 | 来源成员 |
|---|--------|------|------|---------|------|---------|
| 1 | 🔴 | 架构 | FenceLayer.cs（WPF HwndSource 宿主） | WPF `HwndSource` 作为桌面 WorkerW 子窗口时，D3D 重定向位图不被 DWM 合成 → 窗口属性全对也不显示 | 重写为纯 Win32 窗口 + Direct2D 绘制，沿用壁纸 `DSWallpaperHost` 配方 | 排障手 |
| 2 | 🟢 | 卫生 | FenceLayer.cs EXSTYLE | 旧 fixup 曾 `ex |= WS_EX_TOOLWINDOW`（主动加钉子）；现已清除 | 保留清除，保留 `WS_EX_NOACTIVATE` | 排障手 |
| 3 | 🟡 | 注释 | NativeMethods.cs:294-297 | 注释称"mpv uses UpdateLayeredWindow"与实际不符（壁纸宿主实测非 layered，该 P/Invoke 全仓库未用） | 后续清理注释，不据此设计 | 排障手 |

---

## 3. 纯 Win32 重写设计要点

### 3.1 可复用的壁纸窗口配方（`WallpaperChildWindow.cs`）
- 类名：`_className = $"DSWallpaperHost_{Guid.NewGuid():N}"`（每次唯一）
- 类注册：`WNDCLASSEX{ style=0, lpfnWndProc=字段持有(存活约束), hInstance=GetModuleHandle(null) }` → `RegisterClassEx`
- 建窗：`CreateWindowEx(WS_EX_NOACTIVATE, className, "...", WS_CHILD|WS_VISIBLE|WS_CLIPSIBLINGS|WS_CLIPCHILDREN, 0,0,w,h, parentHwnd, 0, hInstance, 0)`；w/h 取父 client rect（0,0 起）
- z-order：壁纸走 `HWND_BOTTOM`；围栏用 `HWND_TOP`（压图标层之上）
- 呈现：窗口自身 `DefWindowProc` 不绘制，mpv `--wid` 直接 present 进该 HWND

### 3.2 围栏绘制选型
- **主选 Direct2D**（`ID2D1HwndRenderTarget`）：复用 mpv 已实证 DWM 合成路径；圆角/文字/半透明原生支持。
- 否定普通 GDI/GDI+ `WM_PAINT`：在 layered 父下同样不合成（与 WPF 同一下场）。
- B 计划（仅 fallback）：`WS_EX_LAYERED` + `UpdateLayeredWindow`（GDI+ DIB，per-pixel alpha），但 layered-on-layered alpha 不稳，置信度低。
- 若 D2D 也空白：升级"手动 D3D11 swapchain (FLIP_DISCARD) present 到 HWND" = mpv 精确路径。

### 3.3 点击穿透（保持不变）
- 完整复用 `ApplyRegion`（`SetWindowRgn` per-box 并集 + add-tile）。绘制坐标与命中区坐标强制共用同一函数（沿用现有 DPI 双重计数，不"修正"）。

### 3.4 需解耦的 WPF 耦合点
- `BuildBoxes`/`BuildAddTile` → 改为"触发重绘(`InvalidateRect`)+重算命中区"。
- 交互：`BeginBoxDrag/BoxDragMove/BoxDragEnd`、`Recategorize`、`RenameCategory`、`OnCollapsedChanged`、`AddCategory`、项拖拽/双击打开/重命名 → 在 WndProc 实现命中测试（`WM_LBUTTONDOWN/MOVE/UP` 映射到 盒子/header/折叠钮/项/add-tile）转发核心逻辑。
- 双击打开真实文件（`Process.Start`，不移动文件）语义保留。

---

## ✅ 行动清单（待用户拍板后执行）

| # | 行动 | 负责方 | 紧急度 | 期望完成 |
|---|------|--------|--------|---------|
| 1 | 窗口创建照搬 `WallpaperChildWindow`（纯 Win32 非 layered 子窗口 + HWND_TOP） | 排障手 | P0 | 实现后 0.5 人日 |
| 2 | Direct2D 渲染盒子（圆角/header/文字/箭头/项名/add-tile） | 排障手 | P0 | 1.5–2 人日 |
| 3 | Win32 命中测试 + 交互重写（或按里程碑先交付静态渲染） | 排障手 | P1 | 2.5–3.5 人日 |
| 4 | 构建自包含包 + 部署 `D:\WorkBuddy\ds\` + 真机验收（保留 `_diagFullWindow` 验证合成恢复） | 主理人 | P0 | 每轮 |

---

## ⚠️ 待完善 / 已知局限

- 合成不确定性：D2D `HwndRenderTarget` 在 layered WorkerW 下的合成性仍需真机首验（已有 D3D11 swapchain / GDI+ layered 两条 fallback）。
- 交互层重写是最大代码量与错误面，建议先用"静态渲染 + 点击穿透"里程碑验证架构，再补交互。
- 多屏 / PerMonitorV2 / Explorer 重启恢复（Phase 6）尚未排期。

---

## 📚 成员产出索引

- gstack-investigator（排障手）原始产出：FenceLayer 纯 Win32 重写设计方案（含 `WallpaperChildWindow.cs` 配方提取、Direct2D 选型、交互解耦清单、6–9 人日工作量估计）。

---

> 本报告由软件工坊 AI 协作生成，关键决策请由工程负责人复核。

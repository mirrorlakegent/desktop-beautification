# DesktopSuite 围栏（Fences）M3 交互 — QA 复验报告（静态代码级）

> 审查对象：`src/DesktopSuite/Desktop/Organizer/FenceLayer.cs`、`FenceStore.cs`、`FenceNative.cs`、`FenceCategory.cs`
> 对照基线：`deliverables/M3_QA_Report.md`（首轮 No-Go；二轮回退为条件 Go，列出问题1~7）
> 审查角色：QA / 发布负责人（只读审查，**未修改任何源文件**）
> 方法：纯静态代码复验（无真机）+ Release x64 干净编译
> 日期：2026-08-12
> 调度：gstack-fence-m3（主理人 沽思航）

---

## TL;DR

- **总体判定：🟡 条件 Go**（代码级 6/6 修复确认落地、无新增阻塞/严重回归；3 项 P0 需真机验收）。
- **干净编译**：`dotnet build -c Release -r win-x64` → **0 错误 0 警告**（本轮环境 obj 可写，已对修复后源码重跑成功；首轮报告因 obj 锁定未能重跑，其遗留的「重跑干净编译」建议项已闭环）。
- **6 项修复逐条核对**：全部 ✅ 真实、正确、无回归。
- **仍开放（非阻塞，继承自首轮）**：问题5（mouseup 坐标忽略，🟡）、问题7（键盘上下文菜单忽略，⚪）。
- **本轮新发现（均非阻塞、非回归）**：① DPI 取值源不一致（DefaultLayout 用 `GetDpiForSystem` / FenceLayer 用 `GetDpiForWindow`，仅多屏异 DPI 场景可能错位，属已声明 deferred）；② DPI 变化取消拖拽态未落盘（保留最后有效位置，可接受）。
- **判定口径**：静态分析无法跑真机，故给「条件 Go」，条件 = 真机确认 3 项 P0 运行时表现（见行动清单 A）。

---

## 核心结论卡片

| 项 | 值 |
|----|----|
| 总体判定 | 🟡 条件 Go |
| 修复完成度 | 6 / 6 ✅ |
| 编译（Release x64） | 0 错误 / 0 警告 |
| 新增阻塞 / 严重回归 | 0 |
| 仍开放（非阻塞） | 问题5（🟡）、问题7（⚪） |
| 本轮新跟踪项 | F-R1（🟡 多屏 DPI 源）、F-R2（⚪ DPI 取消拖拽未落盘） |
| 真机验收项 | 3 项 P0 + 功能/回归清单 |

**严重度分布**：P0 代码级未闭环 0 项（原 3 项转为「真机验收」）；🟢 已修复 6；🟡 开放 2（含新 1）；⚪ 信息 2（含新 1）。

---

## 复验方法

1. 通读 `M3_QA_Report.md`，提取 6 项待复验修复 + 问题5/7 开放项。
2. 静态阅读 `FenceLayer.cs` / `FenceStore.cs` / `FenceNative.cs` / `FenceCategory.cs` 全量源码。
3. 关键不变量核查（Grep）：旧实例字段 `_layoutNeedsInitialGrid` 全仓 **0 命中**（已彻底移除）；两处 DPI 取值与回退；`UncategorizedId` 定义与引用一致。
4. 几何单位审计（hybrid 约定）：X/Y 物理（`_virtualLeft/_virtualTop`、`wa.Left/wa.Top` 物理）；Width/Height 逻辑（`BuildBoxes` ×`_dpiX/_dpiY`、`DefaultLayout` 间隔 ×`s`）。
5. 编译验证：`dotnet build -c Release -r win-x64` 干净 0/0。

---

## 六项修复逐条确认

### 修复1（原问题1，🔴 P0）：BuildBoxes 自动网格门控改为内容判定
**证据**：`FenceLayer.cs:302-308`
```csharp
bool needsInitialGrid = _layout.Categories.Count > 0 &&
    _layout.Categories.All(c => c.Width <= 0 || c.Height <= 0);
if (needsInitialGrid) { AutoLayoutGrid(); ... }
```
- ✅ **语义正确**：当且仅当「存在分类 且 全部分类均无有效尺寸（Width≤0 或 Height≤0）」才跑网格。一旦任一分类 `Width>0 且 Height>0`（来自 DefaultLayout / 持久化 `fences.json` / AutoLayoutGrid），`All(...)`=false → 绝不重跑网格 → 不覆盖用户坐标。这是最保守的写法，永不覆盖已有有效用户坐标。
- ✅ **持久化路径闭合**：`EndDrag()` 末行 `FenceStore.Current.Save(_layout!)`（:616）；`ToggleCollapse`(:658)、`NewCategory`(:687)、`DeleteCategory`(:736) 同样 Save。拖拽坐标跨重启可保留（运行时见 P0-1）。
- ✅ **旧字段彻底移除**：Grep `_layoutNeedsInitialGrid` 全仓 0 命中。
- **判定**：代码级 ✅ 正确。运行时验收见 P0-1。

### 修复2（原问题2，🔴 P0）：双击标题粘连 → OnLButtonDblClk 先 EndDrag()
**证据**：`FenceLayer.cs:620-625`
```csharp
private void OnLButtonDblClk(int x, int y)
{
    EndDrag();          // 收尾前序 DOWN 引发的拖拽（双击吞掉第二次 UP）
    var hit = HitTest(x, y);
    ...
}
```
- ✅ 双击序列 `DOWN→UP→DOWN→DBLCLK` 中第二次 UP 被 DBLCLK 取代；第二次 DOWN（命中标题）会置 `_dragCat`+`SetCapture`。DBLCLK 开头 `EndDrag()` 立即终态化、清除捕获，盒子不再「粘」光标。
- ✅ 项双击时 `_dragCat==null`，`EndDrag` 为安全空操作；随后 `HitTest`→Item→`OpenItem(b.Paths[hit.ItemIndex])`（全路径）正常打开。
- **判定**：代码级 ✅ 正确。运行时验收见 P0-2。

### 修复3（原问题3，🟠）：DPI/几何语义（hybrid 约定）
**证据**：`FenceStore.cs:97-105`、`FenceCategory.cs:34-39`、`FenceLayer.cs:314-318`
- ✅ **DefaultLayout 间隔已按 DPI 缩放**：`double s = DpiScale(); col1 = col0 + 260*s; row1 = row0 + 300*s; row2 = row0 + 600*s;`（步长 24/260/300/600 均 ×`s`=dpi/96）。
- ✅ **X/Y 落在物理基准**：`wa.Left/wa.Top` 来自 `MONITORINFO.rcWork`（物理像素），直接作 `cat.X/Y`，无二次缩放；`BuildBoxes` 用 `cat.X - _virtualLeft`（物理减物理）得客户坐标（:314-315）。
- ✅ **尺寸换算一致**：`right = left + (int)Math.Round(cat.Width * _dpiX)`、`bottom = top + (int)Math.Round(hLogical * _dpiY)`（:317-318）；`Width/Height` 注释已更正为「逻辑像素（96-DPI 基准）」（FenceCategory.cs:34-39）。
- ✅ **150% DPI 不重叠（数值证）**：盒子物理宽=240×s，列间距=260×s → 间隙=20×s>0；盒子高=280×s，行间距=300×s → 间隙=20×s>0（s>0 恒成立）。旧代码不缩放 → 150% 时 240 物理宽 > 260 物理间距 → 重叠，已修复。
- **判定**：代码级 ✅ 正确。运行时验收见 P0-3。

### 修复4（原问题4，🟢 低）：WM_CAPTURECHANGED 重入
**证据**：`FenceLayer.cs:607-618` + `:451-454`
```csharp
private void EndDrag()
{
    if (_dragCat == null) return;
    var cat = _dragCat;
    _dragCat = null;                       // 先置 null
    if (FenceNative.GetCapture() == _hwnd) FenceNative.ReleaseCapture();  // 触发 WM_CAPTURECHANGED
    BuildBoxes(); ApplyRegion(); UpdateVisual(); Save(...);
}
```
- ✅ `ReleaseCapture()` 同步触发本窗口 `WM_CAPTURECHANGED→WndProc→EndDrag`；此时 `_dragCat==null` → `if (_dragCat==null) return;` 提前返回，消除重入双执行（BuildBoxes/ApplyRegion/UpdateVisual/Save 仅跑一次）。
- **判定**：代码级 ✅ 正确，无回归。

### 修复（原问题6，🟡）：OnDisplayOrDpiChange 清拖拽态
**证据**：`FenceLayer.cs:973-977`
```csharp
if (_dragCat != null)
{
    _dragCat = null;
    if (FenceNative.GetCapture() == _hwnd) FenceNative.ReleaseCapture();
}
```
- ✅ DPI/显示器变化（`WM_DISPLAYCHANGE/WM_DPICHANGED`，不 Close/Show）开头先清 `_dragCat` 并释放捕获，避免拖拽态随重建泄漏/卡死。
- ✅ 随后以新 `_dpiX/_dpiY` 重算并重排（`BuildBoxes` 在 :1002，晚于 :997-998 的 DPI 重取），顺序正确。
- **判定**：代码级 ✅ 正确，无回归。

### 修复（GetDpiForSystem P/Invoke）：签名 / 用法 / 回退
**证据**：`FenceNative.cs:39-42` + `FenceStore.cs:190-199`
```csharp
[DllImport("user32.dll")]
public static extern uint GetDpiForSystem();          // 无参，返回 UINT
...
uint dpi = FenceNative.GetDpiForSystem();
if (dpi >= 1) return dpi / 96.0;     // dpi==0 → 落入下方回退
return 1.0;                          // 失败/0 回退 96 DPI
```
- ✅ 签名正确（`uint GetDpiForSystem()`，紧邻 `GetDpiForWindow`）。
- ✅ 用法正确：`dpi >= 1` 判定，dpi==0（pre-Win10 或不支持）→ 回退 1.0，与要求「返回 0 → 回退 1.0」一致；try/catch 兜底。
- **判定**：代码级 ✅ 正确，无回归。

---

## 交互代码全扫（M3 消息/命中/绘制）— 新增问题

**扫描范围**：`WndProc` 新增 `LBUTTONDOWN/MOVE/UP/DBLCLK/CONTEXTMENU/COMMAND`、`HitTest`、`OnLButtonDown/OnMouseMove/EndDrag/OnLButtonUp/OnLButtonDblClk/OpenItem/ToggleCollapse/NewCategory/OnContextMenu/OnContextCommand/DeleteCategory`、`DrawBoxes` emoji 回退。

**结果**：未引入任何空引用、越界、资源泄漏、命中测试与绘制度量不一致的严重问题。逐项确认：

- **命中/绘制度量一致**：`HeaderH=Min(28×dpiY, h)`、`ItemLineH=20×dpiY`、`ItemTopPad=8×dpiY`、`CollapseBtnW=28×dpiX`、`CollapseBtnInner=8×dpiX`，在 `HitTest`(:523-547) 与 `DrawBoxes`(:854-896) 完全一致；项行 `maxItems/起始/行高` 同公式。
- **折叠按钮命中区** `[b.Right-28, b.Top]–[b.Right-8, b.Top+hh]` 与 `DrawCollapseChevron` 绘制中心一致。
- **OpenItem** 用 `b.Paths[ItemIndex]`（全路径），与 `HitTest` 的 `b.Items.Count` 守卫对齐，索引不越界。
- **右键菜单** `id=1000+b.CategoryIndex`，`TrackPopupMenuEx` 模态同步返回后 `DestroyMenu`，无 GDI 菜单泄漏；`DeleteCategory` 先护内置「未分类」（`id` 比对 + `FirstOrDefault` 判空）再 `RemoveAt`，成员归位未分类。
- **UpdateVisual** 的 GDI 资源（`bmp/hBmp/hdcMem/hdcScreen`）在 `finally` 全部释放；`DrawBoxes` 内 `Font/Brush/Pen` 均 `using`。
- **空布局**：`_boxRects` 空 → `ApplyRegion` 设空 region（全透明穿透），无全屏黑块/崩溃。

### 本轮新发现（非阻塞、非回归）

| ID | 严重度 | 描述 | 判定 |
|----|--------|------|------|
| F-R1 | 🟡 低（跟踪） | DPI 取值源不一致：`DefaultLayout` 用 `GetDpiForSystem`（主屏 DPI），`FenceLayer` 运行时用 `GetDpiForWindow(_hwnd)`（窗口所在屏 DPI）。**单主屏（支持场景）二者相等**；仅「多屏异 DPI + 围栏窗口落在副屏」时，默认布局间距缩放因子与盒子渲染缩放因子可能不一致，副屏初始布局可能错位/轻微重叠。属已声明 deferred 的多屏范围（风险6），不阻塞 M3。 | 非阻塞 |
| F-R2 | ⚪ 信息 | `OnDisplayOrDpiChange` 取消进行中拖拽时仅清 `_dragCat`+`ReleaseCapture`，未调用 `EndDrag` 的 `Save`；但 `cat.X/Y` 仍保留拖拽中最后有效位置，后续 `BuildBoxes` 以新 DPI 重排，行为可接受（DPI 变化本就使拖拽偏移失效）。 | 可接受 |

---

## 仍开放问题（继承自首轮，未回归、非阻塞）

| ID | 严重度 | 描述 |
|----|--------|------|
| 问题5 | 🟡 低 | `OnLButtonUp` 直接 `EndDrag()` 忽略传入 x/y，终态坐标依赖最后一次 `WM_MOUSEMOVE`；若「最后 move」与「up」间有位移且无 move 消息，落盘坐标与视觉落点偏差数像素。影响极小，可迭代。 |
| 问题7 | ⚪ 信息 | `WM_CONTEXTMENU` 的 `lParam==(-1,-1)`（Shift+F10/应用键）直接 return，无键盘上下文菜单。桌面围栏场景可接受。 |

---

## 行动清单

### A. 真机验收清单（主理人/用户在 Windows 11 x64 真机执行，本静态审查不可替代）

**P0（必须）**
- [ ] **P0-1** 拖拽后重启应用，布局保留（验证 修复1：内容门控 + Save）。
- [ ] **P0-2** 标题栏双击，盒子不「粘」光标（验证 修复2：OnLButtonDblClk 先 EndDrag）。
- [ ] **P0-3** 150% DPI 下：默认布局（工作/娱乐/工具/临时/未分类）盒子尺寸与位置正确、互不重叠（验证 修复3：间隔×s）。建议同时跑 100% 与 150% 各一遍。

**P1（功能）**
- [ ] 折叠/展开切换 + 重启保留。
- [ ] 项行双击用默认程序打开（全路径，含中文/空格路径）。
- [ ] emoji 标题图标正常显示（单色已知限制），150% DPI 下无错位。

**P2（功能）**
- [ ] 新建分类（＋磁贴）→ 空盒出现在底部 → 重启保留。
- [ ] 右键删除非内置盒 → 成员归位「未分类」→ 重启保留；右键「未分类」灰显无效。

**回归**
- [ ] 点击穿透：盒外点选桌面原生图标正常；盒内点击不穿透。
- [ ] DPI/分辨率变化后重排 + 穿透仍正确；多屏（可选）确认可见或裁剪属 deferred。

### B. 代码侧（可选，非阻塞）

- [ ] 登记 F-R1 为 FenceStore 后续清理项（默认布局与运行时 DPI 取值源统一，或明确仅主屏支持）。
- [ ] 问题5（可选）：将 `EndDrag(int x,int y)` 用 mouseup 客户坐标算终态，消除数像素偏差。

---

## 免责声明

- 本报告为**静态代码复验**，未运行真机/VM，未做运行时行为验证。3 项 P0 与全部功能/回归项的最终判定依赖真机验收（清单 A）。
- 「条件 Go」不等同于「免测发布」：修复 1/2/3 的代码逻辑已确认正确，但其运行时表现（跨重启持久化、双击不粘连、150% DPI 渲染）必须由用户在 Windows 11 x64 真机确认。
- 编译验证在 Release x64 下 0 错误 0 警告，证明 6 项修复语法/语义正确且可构建；**编译通过不替代运行时验收**。
- 多屏异 DPI 场景（F-R1）属设计已声明 deferred 范围，不在 M3 验收强制项内。

---

## 补充复验（2026-08-12 追加）：第 7 项修复 — OnMouseMove「松手即终态」

> 由 gstack-investigator 排障并落地的**单拖粘连**修复，独立于原 6 项修复中的「问题2 双击粘连」，于本轮复验后补充确认。

**根因**：`WS_EX_NOACTIVATE` 窗口的鼠标捕获是「弱」的，且拖拽期间命中区滞留在旧盒子位置，松手点常在命中区外 → `WM_LBUTTONUP` 经常收不到 → 盒子黏在光标。原 `OnMouseMove` 仅依赖「持有捕获即继续拖」，丢失 UP 即永久粘连。

**修复**：`FenceLayer.cs:578-588`，`OnMouseMove` 在 `lButton==false` 时确定性调用 `EndDrag()`。`lButton` 信号源经核实为 `WndProc` 派发处的 `(wParam & MK_LBUTTON) != 0`（:442-443，逐消息可靠按钮位，**非** async `GetKeyState`），故 `lButton==false` 仅当左键真正抬起时成立，不会在拖拽中途误终止。

**代码级判定：✅ 正确，无新增回归**
- 正常拖拽（按钮按住）→ `MK_LBUTTON` 置位 → 走移动 + 16ms 节流路径，行为不变。
- 松手（即便 UP 丢失）→ 后续 `WM_MOUSEMOVE(lButton=false)` → `EndDrag()`；`_dragCat` 先置 null（:617）使重入/幂等安全；释放捕获后与 `OnLButtonUp` / `OnLButtonDblClk` 两路收敛同一终态。
- 捕获释放后窗口不再收 mousemove，故 `!lButton` 分支仅在「捕获仍在但按钮已抬」的弱捕获场景生效，精准命中目标。
- 双信号（WM_LBUTTONUP + 后续 !lButton move）均到达时，第二次 `EndDrag` 命中 `_dragCat==null` 早返回，`BuildBoxes/ApplyRegion/Save` 仅一次，fences.json 不重复写。

**对总体判定影响**：不降级。本修复强化 P0 拖拽终止健壮性（与问题2 双击修复互补），🟡 条件 Go 维持。

### 针对本修复的回归用例（供真机 / 消息注入）

**关键设计原则**：`!lButton→EndDrag` 在「收到带 lButton=false 的 WM_MOUSEMOVE」时触发。若松手后鼠标**完全静止**，不会生成 WM_MOUSEMOVE，该确定性分支无法触发，仍依赖（通常可达的）`WM_LBUTTONUP` 主路径。故回归用例必须包含「松手后再移动鼠标」以覆盖新路径。

- **R1（P0-核心）** 单拖盒子 → 松手 → 继续移动鼠标（无按键）→ 盒子立即掉落、不再跟随光标。反例：盒子黏光标。**必须「松手后再动一下」**。
- **R2（P0-防唤醒）** 拖到空白处松手（移动确认掉落）→ 移回旧盒子标题位置（不按键）→ 不重新唤醒拖拽（`_dragCat==null` 早返回）。
- **R3（P0-2 复用）** 标题双击不粘连（OnLButtonDblClk 开头 EndDrag 仍有效）；双击后移动鼠标确认无拖拽。本修复叠加后，残留 mousemove(lButton=false) 仅触发幂等 EndDrag。
- **R4（边界-静止松手）** 单拖 → 松手且鼠标静止 2-3s → 观察：正常环境 WM_LBUTTONUP 通常到达 → 掉落，验证主路径未破坏。若极端丢失 UP 且静止，盒子暂留至下次移动——属已知边界（非本修复回归）。
- **R5（回归-功能复用）** 折叠/展开、新建、删除、emoji 渲染、点击穿透、150% DPI 布局——复用既有用例，确认 OnMouseMove 改动未影响非拖拽路径。
- **R6（双信号幂等）** 正常拖拽松手 + 移动 → 确认 EndDrag 仅落盘一次（HostLog「拖拽结束」一次、fences.json 正常）。
- **R7（可选-消息注入，确定性）** 注入序列 `WM_LBUTTONDOWN(标题)→WM_MOUSEMOVE(lButton=true,移)→WM_MOUSEMOVE(lButton=false,模拟丢失UP)` → 断言盒子落点固定、`_dragCat==null`、捕获释放。适合 CI 确定性复验「丢失 WM_LBUTTONUP」极端场景。

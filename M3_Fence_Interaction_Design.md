# DesktopSuite 围栏（Fences）M3 交互架构设计

> 评审对象：`FenceLayer.cs` / `FenceStore.cs` / `FenceCategory.cs` / `FenceNative.cs`
> 范围：M3 五项交互（拖拽移动 P0 / 折叠展开 P1 / 双击打开 P1 / emoji 修复 P1 / 新建删除 P2）
> 约定：本文给出**接口契约 + 关键片段**，不写完整实现；实现方按契约落地即可。

---

## 0. 前提约束（已读代码确认）

| 项 | 现状 |
|----|------|
| 窗口 | 纯 Win32 子窗口，挂在桌面 WorkerW 之下；样式 `WS_CHILD\|WS_VISIBLE\|WS_CLIPSIBLINGS\|WS_CLIPCHILDREN`，扩展 `WS_EX_NOACTIVATE` + 后置补加 `WS_EX_LAYERED`；回退为顶层 `WS_POPUP` layered（`WS_EX_TOOLWINDOW`） |
| 渲染 | GDI+ → 离屏 `Bitmap(_winW,_winH,Format32bppArgb)` → `UpdateLayeredWindow`（常量 alpha，`AlphaFormat=0`）；无 WM_PAINT 自绘 |
| 点击穿透 | `SetWindowRgn` 把各盒子矩形 union 设为命中区；窗口**无** `WS_EX_TRANSPARENT` |
| 坐标 | `_boxRects` 为窗口**客户物理像素**，已扣 `_virtualLeft/_virtualTop`；鼠标消息 `lParam` 也是窗口客户坐标 → **同基准，可直接比较** |
| DPI | `_dpiX=_dpiY=GetDpiForWindow(_hwnd)/96`；几何均按 DPI 缩放 |
| 持久化 | `FenceStore.Current.Save(FenceLayout)` / `Load()`，原子写（`File.Replace`） |
| 内置盒 | `UncategorizedId = "00000000-0000-0000-0000-000000000001"`，**不可删除** |
| 打开文件 | 既有模式 `Process.Start(new ProcessStartInfo(path){UseShellExecute=true})`（见 MainWindow.xaml.cs:889），`.lnk` 由 Shell 解析 |

> ⚠️ **头号正确性陷阱（P0，必须先解）**：`BuildBoxes()` 当前**无条件**调用 `AutoLayoutGrid()`，每次都把 `cat.X/Y/Width/Height` 重写回网格。一旦实现拖拽，每次 `BuildBoxes()` 都会把用户拖好的位置吃回。M3 必须给 `AutoLayoutGrid` 加开关（见 §5 风险1 + §6 契约）。
> **状态**：该开关**已实现**，采用 content-based 形式（见 §6 附），M3.0 已闭环。

> **【0.1 几何单位约定（M3 修订，回应 QA 报告「问题3」）】**
> QA 审查确认 `Width/Height` 的语义在三类来源不统一，且此前被「AutoLayoutGrid 每次启动覆盖坐标」的独立 bug 掩盖；该 bug 修复（见上 ⚠️）后，单位不一致会在高 DPI 下暴露为盒子尺寸/重叠错乱。M3 设计采用**统一约定**如下：
> - **`X` / `Y`：虚拟屏物理像素（不变）。** `BuildBoxes` 用 `b.Left = round(cat.X - _virtualLeft)`；拖拽落盘 `cat.X = clientLeft + _virtualLeft`（物理）；`PrimaryWorkArea()` 返回物理坐标。
> - **`Width` / `Height`：逻辑像素（96-DPI 基准）。** `BuildBoxes` 经 `cat.Width * _dpiX` / `cat.Height * _dpiY` 转物理；`AutoLayoutGrid` 用 `cat.Width = boxW / _dpiX` 落逻辑。
> - **为何 hybrid（而非全物理/全逻辑）**：全物理 → 高 DPI 下盒子视觉变小，不符美化软件「恒定视觉尺寸」诉求；全逻辑 → 需把 `X/Y` 也改逻辑并让 `BuildBoxes` 做 DPI 换算，改动面大且与现有拖拽契约（物理 `X/Y`）冲突，留作后续清理（非 M3 阻塞）。hybrid 在现有 `BuildBoxes` 下渲染自洽：位置绝对物理（钉在物理桌面不动），尺寸逻辑（跨 DPI 视觉一致）。
> - **对齐动作（实现方，非交互契约阻塞项）**：
>   1. `FenceCategory` 注释：`Width/Height` 由「Box width in physical pixels」改为「逻辑像素（96-DPI 基准）；渲染时 `BuildBoxes` 乘以 `_dpiX/_dpiY` 得物理像素」；`X/Y` 维持「虚拟屏物理像素」。
>   2. `FenceStore.DefaultLayout`：其 `Width/Height=240/280` 在逻辑约定下即「逻辑 240/280」→ DPI 下放大是**预期 DPI 感知行为，非 bug**。但 DefaultLayout 的**间隔与定位仍是物理像素**（gap 260/300、`PrimaryWorkArea` 物理坐标）：高 DPI 下逻辑宽 240→物理 360 会超过物理间隔 260 → 盒子重叠。须把 DefaultLayout 的间隔/尺寸改为**逻辑**（与 `AutoLayoutGrid` 的 `gapX=20*_dpiX`≈逻辑 20 对齐），即按逻辑 gap 算列/行偏移后再落盘（`X/Y` 仍存物理）。该修复归 FenceStore owner，**不阻塞 M3 交互**。
>   3. **新建分类必须给定 `Width/Height > 0`（逻辑像素）**：当前 `BuildBoxes` 的网格开关是 content-based（`All(c => c.Width<=0 || c.Height<=0)`），只要存在一个已定型（>0）的分类，新分类就不会被 AutoLayoutGrid 接管，且 `Width=0` 会渲染成 0 宽盒。故新建分类应自带逻辑尺寸（如 240×280）并由自由槽位逻辑定位 `X/Y`。
> - **影响范围**：仅几何单位的「文档与实现对齐」，拖拽/双击/折叠/新建/删除交互契约**不受影响**（拖拽只改 `X/Y` 物理，不碰 `Width/Height`；双击打开用 `BoxRect.Paths[i]`，BoxRect 已随盒携带全路径）。

---

## 1. Win32 消息处理总表

新增 / 修改的消息与处理逻辑如下。`WndProc` 当前只处理 `WM_PAINT / WM_SIZE / WM_DESTROY / WM_DISPLAYCHANGE / WM_DPICHANGED`，其余走 `DefWindowProc`。

| 消息 | 触达条件 | 处理逻辑 | 注意事项 |
|------|----------|----------|----------|
| **WNDCLASSEX.style** | 注册窗口类时 | 必须加 `CS_DBLCLKS (0x0008)` | 当前为 `0` → `WM_LBUTTONDBLCLK` 永不触发；这是双击打开功能的前置硬改 |
| **WM_LBUTTONDOWN (0x0201)** | 命中区内按下左键 | 命中测试（见 §2 顺序）→ ① `_addTileRect`：新建分类；② 折叠按钮子区：切换 `Collapsed`；③ 标题栏带：记偏移 + `SetCapture(_hwnd)` 进入拖拽；④ 项行（未折叠）：仅记录候选，不拖 | 命中后即 `return 0`；不要转发 `DefWindowProc`；区域外点击已被 `SetWindowRgn` 穿透到桌面，不会进本窗口 |
| **WM_MOUSEMOVE (0x0200)** | 拖拽中（`_dragCat!=null` 且已捕获） | 按偏移算新客户坐标 → 钳制 → 写 `cat.X/Y` → `BuildBoxes()` → **节流** `UpdateVisual()` | 仅在有捕获时处理；未捕获时忽略（或转 `DefWindowProc`） |
| **WM_LBUTTONUP (0x0202)** | 拖拽结束 | `ReleaseCapture()` → 终态 `cat.X/Y` → `BuildBoxes()`+`UpdateVisual()`+`ApplyRegion()` → `FenceStore.Current.Save(_layout)` → 清 `_dragCat` | 区域外释放也能收到（因 `SetCapture`）；只有 mouseup 才写盘，**move 过程不 Save** |
| **WM_LBUTTONDBLCLK (0x0203)** | 项行上双击（需 `CS_DBLCLKS`） | 命中测试定位盒子+项行 `i` → 取 `BoxRect.Paths[i]`（**全路径**；`b.Items` 仅作绘制 basename）→ `Process.Start(UseShellExecute=true)` | 仅对未折叠盒子的项区生效；标题栏双击忽略 |
| **WM_CAPTURECHANGED (0x0215)** | 捕获被系统抢走（Alt-Tab 等） | 终态化当前拖拽（`_dragCat` 已有最新位置则直接 `Save`+重建），清状态 | 兜底：避免拖拽状态卡死 |
| **WM_RBUTTONUP (0x0205)** / **WM_CONTEXTMENU (0x007B)** | 盒上右键 | 命中测试定位盒子 → `TrackPopupMenuEx` 弹出菜单（"删除此分类"等）→ `WM_COMMAND` 执行删除 | `WM_CONTEXTMENU` 的 `lParam` 是**屏幕坐标**，需 `ScreenToClient`/`GetWindowRect` 换算；`TrackPopupMenu` 独立于 `WS_EX_NOACTIVATE` 工作 |
| **WM_NCHITTEST (0x0084)** | 系统命中测试 | **默认不处理**（交 `DefWindowProc`）。区域命中已由 `SetWindowRgn` 保证点击落在盒内并送达本窗口 | 不要返回 `HTCAPTION`（会移动整个全屏窗口，见 §5）；除非 M3.0 验证点击不到达，再考虑返回 `HTCLIENT` 兜底 |
| WM_PAINT / WM_SIZE / WM_DESTROY / WM_DISPLAYCHANGE / WM_DPICHANGED | 维持现状 | `WM_SIZE` 仅更新 `_winW/_winH` 并重绘；**不得**在 `WM_SIZE` 里重建盒子位置 | `WM_DPICHANGED` 已走 `OnDisplayOrDpiChange` 重建，保持 |

> **拖拽方案结论（P0）**：用 **手动拖拽**（`WM_LBUTTONDOWN`+`SetCapture`+`WM_MOUSEMOVE`+`WM_LBUTTONUP`），**不用** `WM_NCHITTEST` 返回 `HTCAPTION`。
> 理由：① 窗口是全屏覆盖桌面的，系统拖 `HTCAPTION` 会移动整个窗口而非逻辑盒子；② 盒子是画在 bitmap 上的，没有原生标题栏，`HTCAPTION` 无可映射对象；③ 我们要改的是逻辑 `cat.X/Y` 并触发 `UpdateLayeredWindow`，不是移动 HWND。手动拖是唯一正确路径。

---

## 2. 命中测试坐标映射

```
窗口客户坐标 (x,y) = lParam 低16位 / 高16位
  x = (int)(lParam & 0xFFFF)
  y = (int)((lParam >> 16) & 0xFFFF)
_boxRects[i] 已是窗口客户物理像素（b.Left = round(cat.X - _virtualLeft)）
→ 直接比较： if (x>=b.Left && x<=b.Right && y>=b.Top && y<=b.Bottom)

虚拟坐标 ↔ 客户坐标（仅在落盘边界换算）
  cat.X = clientLeft + _virtualLeft        // 因为 b.Left = round(cat.X - _virtualLeft)
  cat.Y = clientTop  + _virtualTop
```

**标题栏带（拖拽热区）**
```
headerH = round(HeaderHeight * _dpiY)          // HeaderHeight=28 逻辑
标题带 = [b.Left, b.Top, b.Right, b.Top + headerH]
```

**折叠按钮子区（画在标题栏右侧）**
```
btnW  = round(28 * _dpiX)
pad   = round(8  * _dpiX)
折叠按钮 = [b.Right - btnW, b.Top, b.Right - pad, b.Top + headerH]
// 绘制：在该区画 ▾(展开时)/▸(折叠时) 或 −/+ 圆标
```

**项行 i（双击打开热区，仅未折叠）**
```
lineH = round(20 * _dpiY)
rowTop(i) = b.Top + headerH + round(8*_dpiY) + i*lineH
项行 i = [b.Left, rowTop(i), b.Right, rowTop(i)+lineH]
// 注意：绘制用 b.Items（basename）；打开用 b.Paths[i]（BoxRect 已随盒携带全路径，等价于 cat.MemberPaths[i]）
```

**新建磁贴**
```
_addTileRect 已是客户像素（BuildBoxes 计算），直接比较
```

**头部热区命中顺序**（WM_LBUTTONDOWN 内）
```
1) 命中 _addTileRect            → 新建分类，return
2) 命中某盒 折叠按钮子区        → 切换 Collapsed，return
3) 命中某盒 标题带(非按钮)      → 记偏移+SetCapture，进入拖拽，return
4) 命中某盒 项行(未折叠)        → 记录候选(供 dblclk)，return
5) 其它                         → return 0（区域外不会进来，纯保险）
```

---

## 3. emoji 渲染修复（GDI+ 字体回退）

**根因**：`DrawString` 用 `"Segoe UI"`，该字体不含彩色 emoji 字形 → 显示为「□□」tofu。
**方案**：把 `IconRef` 前缀单独用 `Segoe UI Emoji` 绘制，正文仍用 `Segoe UI`；设 `TextRenderingHint = AntiAliasGridFit`。

```csharp
// DrawBoxes 中替换原 DrawString(title,...) 的调用
private void DrawTitleWithEmoji(Graphics g, string iconRef, string label,
    Font baseFont, Brush brush, RectangleF rect, StringFormat sf)
{
    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
    if (string.IsNullOrEmpty(iconRef)) {
        g.DrawString(label, baseFont, brush, rect, sf);
        return;
    }
    // emoji 用专门字体（GDI+ 只能出单色字形，但至少不再是方块）
    using var emojiFont = new Font("Segoe UI Emoji", baseFont.Size, FontStyle.Regular);
    string prefix = iconRef + " ";
    var prefixSize = g.MeasureString(prefix, emojiFont, rect.Width, sf);
    g.DrawString(prefix, emojiFont, brush,
        new RectangleF(rect.X, rect.Y, prefixSize.Width, rect.Height), sf);
    // 正文紧随其后
    g.DrawString(label, baseFont, brush,
        new RectangleF(rect.X + prefixSize.Width, rect.Y,
                       rect.Width - prefixSize.Width, rect.Height), sf);
}
```

> **限制（已知）**：GDI+ 不支持彩色 emoji 合成，即便 `Segoe UI Emoji` 也只能出**单色**字形（仍是实质性修复，去掉了豆腐块）。要彩色需 DirectWrite/`IDWriteTextLayout`+`ID2D1RenderTarget` 或 Win2D（`Microsoft.Graphics.Win2D` 的 `CanvasTextLayout`），属后续增强，**M3 不强制**。

---

## 4. 持久化调用点（已读 FenceStore.cs 确认签名）

```csharp
// FenceStore API（确认）
public static FenceStore Current { get; }          // 单例
public FenceLayout Load()                           // 原子读，保证含未分类盒
public void Save(FenceLayout layout)                // 原子写（File.Replace）
```

运行时 `_layout` 即 `Show(items, layout)` 注入的同一引用，`BuildBoxes` 直接读它。所以交互只需**改 `_layout.Categories[i]` 字段 → 调 `Save(_layout)`**。

| 交互 | 调用点 | 调用 |
|------|--------|------|
| 拖拽结束 | `WM_LBUTTONUP`（捕获释放后） | `FenceStore.Current.Save(_layout)` |
| 折叠/展开 | 切换 `Collapsed` 后 | `FenceStore.Current.Save(_layout)` |
| 新建分类 | 加入 `_layout.Categories` 后 | `FenceStore.Current.Save(_layout)` |
| 删除分类 | 移除+成员归位后 | `FenceStore.Current.Save(_layout)` |
| 双击打开 | **不调用**（布局未变） | — |
| 拖拽过程 `WM_MOUSEMOVE` | **不调用**（性能/JSON 抖动） | — |

> `Save` 是同步整文件原子写，分类数很少时开销可忽略；**切勿在 mousemove 每帧调用**。

---

## 5. 风险与边界

| # | 风险 | 等级 | 应对 |
|---|------|------|------|
| 1 | **`AutoLayoutGrid` 强制重写位置**：`BuildBoxes()` 无条件调它，会吃回拖拽结果 | P0（已实现） | 加 content-based 开关（`All(c=>c.Width<=0||c.Height<=0)` 才跑，已落地）；新建分类自带 `Width/Height>0`（逻辑像素）永不被网格覆盖。详见 §6 附 + §0.1 |
| 2 | **交互点击是否真到达窗口**：M1/M2 只验证过"渲染 + 穿透到桌面"，**从未真机验证过窗口收到鼠标消息** | 中 | 逻辑上"穿透成立 ⇒ 系统已对窗口做命中测试 ⇒ 区域内点击必送达"。但 M3.0 必须在真机加临时日志确认 `WM_LBUTTONDOWN/UP/DBLCLK` 可达；若不到达，补 `WM_NCHITTEST` 返回 `HTCLIENT` 或排查 Explorer 桌面层拦截 |
| 3 | **`CS_DBLCLKS` 缺失**：类 style=0 → 双击消息永不触发 | P0 | `WNDCLASSEX.style |= 0x0008` |
| 4 | **`WS_EX_NOACTIVATE` + `SetCapture`**：拖拽中捕获是否稳定 | 低 | `SetCapture` 在 noactivate 窗口正常生效；用 `WM_CAPTURECHANGED` 兜底终态化，避免 Alt-Tab 卡死 |
| 5 | **`UpdateLayeredWindow` 全屏重绘开销**：拖拽每帧推 1920×1080 位图 | 中 | mousemove 按 ~16ms 节流（Stopwatch 累计）；`ApplyRegion` 只在 mouseup 调一次；最后再补一帧 `UpdateVisual` |
| 6 | **多显示器/虚拟坐标偏移**：`_virtualLeft/_virtualTop` 假设窗口原点 == 虚拟屏原点；多屏负坐标下若宿主原点 ≠ 虚拟原点则偏差 | 中（已知 deferred） | 钳制与存储统一用虚拟坐标；真机校准宿主原点；多屏精确钳制留待后续 phase |
| 7 | **DPI 变化**：`WM_DPICHANGED` 已重建；拖拽中切 DPI 概率极低 | 低 | 钳制用当前 `_dpi`；重建后拖拽状态随 `Close/Show` 重置（天然安全） |
| 8 | **右键菜单 + NOACTIVATE**：`TrackPopupMenu` 是否能拿到焦点 | 低 | `TrackPopupMenuEx` 独立工作；简单场景可直接"右键删除"（降级方案，免建菜单） |
| 9 | **桌面刷新致窗口重建**：Explorer F5 / 换壁纸可能重建 WorkerW 子树，本窗口被销毁重建；内存拖拽状态丢失 | 中 | `WM_CAPTURECHANGED` 兜底；`Show/Close` 重入后确保 `_dragCat/_dragOffset` 为零值 |
| 10 | **emoji 彩色** | 已知 | GDI+ 单色；彩色需 DirectWrite/Win2D，M3 不强制 |

**点击穿透 ⇄ 命中区的关键关系**（设计基准）：
- `SetWindowRgn` 把窗口裁剪到盒子 union；**盒内**点击送达本窗口（触发上述所有交互），**盒外**透明穿透到桌面。
- 因此"穿透可用"已证明系统对窗口做命中测试 → 盒内交互点击可达（风险2 的理论依据）。
- 拖拽期间**不要**每帧更新 region（靠 `SetCapture` 保收消息即可），仅在 mouseup 把 region 同步到最终位置。

---

## 6. 建议实现顺序（里程碑切片）

### M3.0 — 验证 + 防回归 spike（P0 地基）
- [ ] `WNDCLASSEX.style |= CS_DBLCLKS`。
- [ ] 网格开关已实现（content-based）；补 DefaultLayout 逻辑化 gap 的 FenceStore 对齐项（见 §0.1 动作2），避免高 DPI 重叠。
- [ ] 临时 `HostLog` 打点，真机确认 `WM_LBUTTONDOWN/UP/DBLCLK` 三类消息可达（风险2）。

### M3.1 — 拖拽移动盒子（P0）
- [ ] `WM_LBUTTONDOWN` 命中标题带 → 记 `_dragOffsetX/Y` + `SetCapture`。
- [ ] `WM_MOUSEMOVE`（已捕获）→ 算新客户坐标 → 钳制 → 写 `cat.X/Y` → `BuildBoxes()` → 节流 `UpdateVisual()`。
- [ ] `WM_LBUTTONUP` → `ReleaseCapture` → `BuildBoxes`+`UpdateVisual`+`ApplyRegion` → `FenceStore.Current.Save(_layout)`。
- [ ] 钳制：客户坐标限制在 `[0, winW-physW]`×`[0, winH-physH]`（折叠时 physH=headerH）。

### M3.2 — 折叠/展开（P1）
- [ ] 标题栏右侧画折叠按钮（§2 子区）；`WM_LBUTTONDOWN` 命中即切换 `cat.Collapsed` → 重建 + `Save`。
- [ ] `BuildBoxes` 折叠时按 `HeaderHeight` 计算盒高（已有逻辑，保持）。

### M3.3 — 双击打开项（P1）
- [ ] `WM_LBUTTONDBLCLK`（依赖 M3.0 的 `CS_DBLCLKS`）命中项行 `i` → `Process.Start(new ProcessStartInfo(b.Paths[i]){UseShellExecute=true})`（`b.Paths` 即全路径，BoxRect 已携带）。
- [ ] 用 `b.Paths[i]`（全路径），勿用 `b.Items[i]`（basename 仅绘制）。

### M3.4 — emoji 修复（P1）
- [ ] 采用 §3 `DrawTitleWithEmoji` 字体回退；记录彩色限制（风险10）。

### M3.5 — 新建 / 删除分类（P2）
- [ ] **新建**：`_addTileRect` 命中 → `new FenceCategory{Id=Guid.NewGuid().ToString(), DisplayName="新建分类", IconRef="📁", X/Y/Width/Height=自由槽位(Width>0)}` → 加入 `_layout.Categories` → `Save` → 重建。
  - 自由槽位：扫描现有盒找一个不重叠位置（如最底盒下方 + gap），**直接写坐标**，使其不被 `AutoLayoutGrid` 覆盖。
- [ ] **删除**：右键菜单（或右键直删）→ 移除该分类；其 `MemberPaths` 移入 `UncategorizedId` 盒；禁止删除内置未分类盒（`if(cat.Id==UncategorizedId) return;`）→ `Save` → 重建。

### M3.6 — 真机回归（收尾）
- [ ] 点击到达 / 穿透 / DPI 重建 / 多屏钳制（stretch）逐项验证。

---

## 附：关键实现契约速查（给实现方）

```csharp
// FenceLayer 需新增的私有状态
private FenceCategory? _dragCat;
private int _dragOffsetX, _dragOffsetY;   // 客户像素：光标 - 盒左上

// BuildBoxes 网格开关（已落地为 content-based，与实现一致）
private void BuildBoxes() {
    _boxRects.Clear(); _addTileRect = null;
    if (_layout == null) return;
    // 仅当“所有分类都尚未定型(Width<=0||Height<=0)”时才跑网格；
    // 一旦有任一已定型分类（拖拽持久化 / DefaultLayout / 新建带尺寸），永不覆盖。
    bool needsInitialGrid = _layout.Categories.Count > 0 &&
        _layout.Categories.All(c => c.Width <= 0 || c.Height <= 0);
    if (needsInitialGrid) AutoLayoutGrid();
    // ... 其余按现有逻辑（用 cat.X/Y/Width/Height 算 _boxRects）
}

// 坐标换算（cat.X/Y 为虚拟屏物理像素）
int cx = (int)(lParam & 0xFFFF), cy = (int)((lParam >> 16) & 0xFFFF);
double newCatX = (cx - _dragOffsetX) + _virtualLeft;   // 落盘用物理虚拟坐标
double newCatY = (cy - _dragOffsetY) + _virtualTop;

// 打开项（沿用既有模式；b.Paths 为全路径，BoxRect 已随盒携带）
Process.Start(new ProcessStartInfo(b.Paths[hit.ItemIndex]) { UseShellExecute = true });

// 持久化（拖拽结束/折叠/新建/删除后）
FenceStore.Current.Save(_layout);
```

> 不需要为 M3 引入 `WS_EX_TRANSPARENT`；命中区仍由 `SetWindowRgn` 负责。emoji 彩色与多屏精确钳制为已知 deferred，不在 M3 强制范围。

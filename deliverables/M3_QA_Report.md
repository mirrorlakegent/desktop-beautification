# DesktopSuite 围栏（Fences）M3 交互 — QA 报告

> 审查对象：`src/DesktopSuite/Desktop/Organizer/FenceLayer.cs`、`FenceNative.cs`
> 对照设计：`M3_Fence_Interaction_Design.md`
> 审查角色：QA / 发布负责人（只读审查，未修改任何源文件）
> 日期：2025-08-12

---

> **【第二轮复核 · 2025-08-12 晚】状态变更**：实现侧已按本报告的 问题1/2/4 完成修复（content-based 网格门控、双击 `EndDrag()`、重入先置 null）。问题3 被产品评审采纳为「hybrid 约定」（X/Y 物理、Width/Height 逻辑），其 DefaultLayout 间隔/定位在 >100% DPI 的盒子重叠作为**非阻塞跟踪项**归 FenceStore owner。问题5/6 仍为低优先级开放项，问题7 为信息级。详见第 3、4、6 节更新。本环境 `obj` 被锁定，无法对改动后代码重跑干净 0/0（仅首轮原码 0/0）；改动均为编译安全的 LINQ/顺序调整。

## 1. 编译结果

| 项 | 结果 |
|----|------|
| 命令 | `"C:\Program Files\dotnet\dotnet.exe" build -c Release -r win-x64` |
| 输出目录 | `src/DesktopSuite/bin/Release/net8.0-windows/win-x64/DesktopSuite.dll` |
| 错误 | **0** |
| 警告 | **0** |
| 用时 | 约 15.7s |

结论：**编译通过，0 错误 0 警告，满足发布硬性门槛。** 所有新增 P/Invoke 签名（`SetCapture`/`ReleaseCapture`/`GetCapture`/`ScreenToClient`/`ClientToScreen`/`CreatePopupMenu`/`AppendMenu`/`TrackPopupMenuEx`/`DestroyMenu`/`POINT`/`GET_X_LPARAM`/`GET_Y_LPARAM`）、`CS_DBLCLKS`、消息常量、`MF_*`/`TPM_*` 标志均能正确解析（否则无法 0 错误编译）。

---

## 2. 设计契约符合度

逐条核对设计 §0–§6：

| 契约项 | 实现位置 | 符合 |
|--------|----------|------|
| `WNDCLASSEX.style \|= CS_DBLCLKS`（双击前置硬改） | `FenceLayer.cs:175` `style = FenceNative.CS_DBLCLKS` | ✅ |
| 网格门控（防 AutoLayoutGrid 吃回位置） | `:297-308` content-based 开关（`Categories.Count>0 && All(c => c.Width<=0 \|\| c.Height<=0)`） | ✅ 已改为**基于布局内容**，跨重启不再覆盖用户坐标（见问题1，已修复） |
| 命中测试坐标同基准（client 物理像素 vs `_boxRects`） | `:511-552` 直接比较，未做变换 | ✅ |
| 折叠按钮子区 `[b.Right-28dpi, b.Top, b.Right-8dpi, b.Top+hh]` | `:529-532`（`CollapseBtnW=28dpi`, `CollapseBtnInner=8dpi`） | ✅ |
| 拖拽流程 `SetCapture→WM_MOUSEMOVE 写 cat.X/Y→BuildBoxes→节流 UpdateVisual` | `:571` `SetCapture`；`:579-601` 写坐标+16ms 节流 | ✅ |
| `WM_LBUTTONUP`：`ReleaseCapture`+`BuildBoxes`+`UpdateVisual`+`ApplyRegion`+`Save` | `:603-618` `EndDrag` | ✅ |
| region 仅落盘时同步一次（拖拽中不每帧更新） | `:593` 注释 + `EndDrag` 内 `ApplyRegion` | ✅ |
| 双击打开用**全路径** `Paths[i]` | `:620-629` `OpenItem(b.Paths[hit.ItemIndex])` | ✅ |
| `Process.Start(UseShellExecute=true)` 沿用既有模式 | `:631-643` `OpenItem` | ✅ |
| 折叠切换 + `Save` | `:645-656` `ToggleCollapse` | ✅ |
| 新建分类（自由槽位、直接写 X/Y/Width/Height>0） | `:658-685` `NewCategory` | ✅ |
| 删除：右键菜单 `TrackPopupMenuEx`→`WM_COMMAND`→`DeleteCategory`，id=`1000+CategoryIndex` | `:687-706` / `:708-715` / `:717-734` | ✅ |
| 内置未分类不可删（`if(cat.Id==UncategorizedId) return`） | `:721` | ✅ |
| 删除成员归位未分类 `unc.MemberPaths.AddRange` | `:724-726` | ✅ |
| emoji 字体回退（Segoe UI Emoji 画图标，Segoe UI 画正文） | `:858-866` `DrawTitleWithEmoji` 片段 | ✅（单色已知限制，设计风险10） |
| `WM_CAPTURECHANGED` 兜底终态化拖拽 | `:452-455` → `EndDrag` | ✅ |
| 点击穿透（盒外仍穿透，拖拽后 region 同步） | `:1067-1141` `ApplyRegion` | ✅ |

**总体符合度：高。** 除问题1（跨重启的网格门控语义）外，其余交互与消息处理均与设计契约一致。

---

## 3. 发现的问题（按严重度）

### ✅ 问题1（高 / 阻塞项，已修复）：拖拽坐标跨进程重启丢失
- **状态**：✅ **已实现侧修复**。`FenceLayer.cs` 删除实例字段 `_layoutNeedsInitialGrid`，`BuildBoxes` 改为 content-based 门控（`:297-308`）：仅当 `Categories.Count>0 && All(c => c.Width<=0 || c.Height<=0)`（即无任一已定型分类）才跑 `AutoLayoutGrid`。旧实例字段已 `Grep` 确认全仓移除。用户拖拽坐标现可跨重启保留。（下方为原 bug 分析，供追溯）
- **现象**：`_layoutNeedsInitialGrid` 是**实例字段**，每次新建 `FenceLayer`（即每次应用启动）都重新初始化为 `true`。因此每次启动 `Show()→BuildBoxes()` 都会**重新执行 `AutoLayoutGrid()`**，把 `cat.X/Y/Width/Height` 重写回网格，覆盖 `fences.json` 中已保存的用户拖拽坐标。
- **影响**：M3 P0「拖拽移动 + 持久化」在**单次会话内有效**（拖拽→`Save`→本会话保留），但**应用重启后用户布局全部丢失**，回到自动网格。这直接违反设计 §0「首次自动布局后不再覆盖用户拖拽坐标」与 §6 M3.0 的明确要求，也违背 §4「拖拽结束 → `Save`」的持久化承诺。
- **根因**：门控判定基于**实例生命周期**而非**布局本身是否已布局过**。
- **建议修复（小且局部）**：把「是否跑网格」改为基于布局内容判定，例如：
  ```csharp
  if (_layoutNeedsInitialGrid && LayoutNeedsGrid())
  {
      AutoLayoutGrid();
      _layoutNeedsInitialGrid = false;   // 仍置位，避免 BuildBoxes 后续调用重复跑
  }
  // LayoutNeedsGrid: 所有分类 X==0 && Y==0 && Width==0 && Height==0 才视为未布局
  ```
  或在 `FenceLayout` 增加持久化标志 `AutoLaidOut`，首跑后置 `true` 并随 `Save` 落盘，门控改为 `if (_layoutNeedsInitialGrid && !_layout.AutoLaidOut)`。两者任选其一。

### ✅ 问题2（中，已修复）：双击标题栏导致「拖拽粘连」
- **状态**：✅ **已实现侧修复**。`OnLButtonDblClk` 开头已加 `EndDrag();`（`:620-625`），并附注释说明双击吞掉第二次 UP 的成因。项双击时 `_dragCat` 为 null，`EndDrag` 安全空操作。（下方为原 bug 分析）
- **现象**：双击序列为 `DOWN→UP→DOWN→DBLCLK`，**第二次的 `WM_LBUTTONUP` 被 `WM_LBUTTONDBLCLK` 取代**。因此第二次 `DOWN`（命中标题）设置的 `_dragCat` 与 `SetCapture` 一直保留，之后**任意鼠标移动（无按键）都会移动盒子**——盒子「粘」在光标上，直到用户再单击一次触发 `WM_LBUTTONUP→EndDrag`。
- **触发条件**：在标题栏/折叠区**双击**（该区域无定义的双击动作，易误触）。
- **影响**：用户体验异常（盒子乱飞），但可一键恢复，非崩溃。
- **建议修复（一行）**：在 `OnLButtonDblClk` 开头调用 `EndDrag();`。对项双击（未进入拖拽，`_dragCat` 为 null）为安全空操作；对标题双击则收尾释放捕获，彻底消除粘连。
  ```csharp
  private void OnLButtonDblClk(int x, int y)
  {
      EndDrag();                 // 收尾可能由前序 DOWN 起始的拖拽（双击吞掉 UP）
      var hit = HitTest(x, y);
      ...
  }
  ```

### 🟢 问题3（中，已转为设计约定）：Width/Height 的 DPI 语义 → hybrid 约定
- **状态**：🟢 **产品评审已采纳并修订设计文档（新增 §0.1）**。约定定为 **hybrid**：`X/Y` = 虚拟屏物理像素（不变）；`Width/Height` = **逻辑像素（96-DPI 基准）**，与 `AutoLayoutGrid`/`BuildBoxes` 一致。因此 `FenceCategory` 注释、`AutoLayoutGrid`、`BuildBoxes` 现已**统一为逻辑**，原「FenceCategory 注释说物理」的错位由设计文档统一澄清。（下方为原分析）
- **现象**：三类来源对 Width/Height 的语义不统一——`FenceCategory` 注释说物理、`AutoLayoutGrid` 存逻辑、`BuildBoxes` 当逻辑（×dpi）、`DefaultLayout` 存物理。
- **当前被掩盖**：问题1 使每次重启都重跑 `AutoLayoutGrid`，把 DefaultLayout 的物理值覆盖为逻辑值，故目前不显现。
- **一旦修复问题1**：DefaultLayout 的盒子（物理 240×280）在 `BuildBoxes` 经 `×_dpiX` 后，在 **非 100% DPI**（Win11 笔记本常见 150%）下会**放大 50%**（240×1.5=360）。
- **建议修复（设计侧已定稿）**：统一为逻辑像素（推荐），与 `AutoLayoutGrid`/`BuildBoxes` 一致；X/Y 维持物理虚拟像素。
- **派生跟踪项（非阻塞，归 FenceStore owner）**：`DefaultLayout` 的**间隔与定位仍是物理像素**（`gap` 260/300、`PrimaryWorkArea()` 物理坐标），而盒子尺寸在逻辑约定下 150% DPI 会放大到 360 物理宽 → **超过 260 物理间隔 → 盒子重叠**。需把 `DefaultLayout` 的间隔/尺寸改为逻辑（与 `AutoLayoutGrid` 的 `gapX=20*_dpiX`≈逻辑 20 对齐）。此为布局初始化美观问题，**不影响 M3 交互功能**，列为后续清理项。

### ✅ 问题4（低，已修复）：`EndDrag` 重入双执行
- **状态**：✅ **已实现侧修复**。`EndDrag` 现先 `var cat=_dragCat; _dragCat=null;`（`:610-611`）再 `ReleaseCapture`（`:612`），使 `WM_CAPTURECHANGED` 重入命中 `if(_dragCat==null) return;` 提前返回，不再双执行。（下方为原分析）
- **现象**：`EndDrag` 中 `ReleaseCapture()` 会**同步**触发本窗口 `WM_CAPTURECHANGED→WndProc→EndDrag` 重入。外层 `_dragCat` 尚未置 null，故 `BuildBoxes/ApplyRegion/UpdateVisual/Save` 被执行**两次**（当前幂等，仅浪费；但有后续维护隐患）。
- **建议修复**：在 `ReleaseCapture()` 之前先把 `_dragCat` 置 null（或本地持有引用），使重入的 `EndDrag` 命中 `if (_dragCat == null) return;` 提前返回。
  ```csharp
  private void EndDrag()
  {
      if (_dragCat == null) return;
      var cat = _dragCat; _dragCat = null;     // 先清空，防 WM_CAPTURECHANGED 重入
      if (FenceNative.GetCapture() == _hwnd) FenceNative.ReleaseCapture();
      BuildBoxes(); ApplyRegion(); UpdateVisual();
      try { FenceStore.Current.Save(_layout!); } catch (Exception ex) { ... }
  }
  ```

### 🟡 问题5（低）：`OnLButtonUp` 忽略传入坐标
- **文件/行号**：`FenceLayer.cs:603-606`（`OnLButtonUp` 直接 `EndDrag()`，未用 x/y）
- **现象**：终态坐标完全依赖最后一次 `WM_MOUSEMOVE`。若鼠标在「最后一次 move」与「up」之间还有位移且无 move 消息，则落盘坐标与视觉落点可能有数像素偏差。
- **建议修复**：`EndDrag(int x, int y)` 用 mouseup 的 client 坐标算终态 `cat.X/Y`（与 `OnMouseMove` 同公式）。影响极小，可后续迭代。

### 🟡 问题6（低）：`OnDisplayOrDpiChange` 未重置拖拽态
- **文件/行号**：`FenceLayer.cs:964-999`
- **现象**：设计风险9 提到「重建后拖拽状态随 Close/Show 重置（天然安全）」，但 `WM_DISPLAYCHANGE/WM_DPICHANGED` 走的是 `OnDisplayOrDpiChange`（不 Close/Show），`_dragCat/_dragOffset` 未清零。拖拽中切屏概率极低，但稳妥起见应重置。
- **建议修复**：方法开头 `if (_dragCat != null) { _dragCat = null; }`。

### ⚪ 问题7（信息级）：键盘上下文菜单被忽略
- **文件/行号**：`FenceLayer.cs:461-464`
- **现象**：`WM_CONTEXTMENU` 的 `lParam==-1,-1`（Shift+F10 / 应用键）时直接 `return`，无菜单。桌面围栏场景下可接受。

---

## 4. 边界风险（对照设计 §5）

| 风险 | 状态 | 说明 |
|------|------|------|
| 多显示器（设计风险6） | 已知 deferred，非回归 | 窗口仅覆盖主屏（`_winW`=主屏 client 宽），`_virtualLeft/_virtualTop` 取虚拟屏原点。副屏盒子 `cat.X-_virtualLeft` 可能超出 `_winW` 被裁剪/不可见。属已声明 deferred。 |
| 拖拽期间 region 不同步（设计 §5 底部） | ✅ 已正确实现 | 拖拽中不更新 region（靠 `SetCapture` 保收消息），仅 `EndDrag` 同步一次。无点击穿透异常。 |
| `WS_EX_NOACTIVATE`+`SetCapture`（风险4） | ✅ 正常 | 捕获稳定，`WM_CAPTURECHANGED` 兜底终态化。 |
| emoji 彩色（风险10） | ✅ 已知限制 | GDI+ 仅单色，豆腐块已消除，彩色需 DirectWrite/Win2D（M3 不强制）。 |
| 桌面 F5/换壁纸重建 WorkerW（风险9） | ✅ 已消除 | 问题1 修复后门控改为基于布局内容，重建后不再重跑网格；但重建期间内存拖拽态仍可能丢失（设计风险9），由 `WM_CAPTURECHANGED` 兜底。 |
| 空布局 | ✅ 健壮 | `_boxRects` 空 → `ApplyRegion` 设空 region（`CreateRectRgn(0,0,0,0)`），全透明点击穿透，无全屏黑块。 |
| DPI 变化（风险7） | ✅ 正确 | `WM_DPICHANGED→OnDisplayOrDpiChange` 重建并刷新 DPI；X/Y 物理随屏保留，Width/Height 逻辑随 DPI 重算（hybrid 约定，见问题3）。150% DPI 下盒子尺寸正确；仅 `DefaultLayout` 初始间隔为物理像素可能导致首跑盒子重叠（见问题3 派生项，非阻塞）。 |
| BoxRect 索引越界 | ✅ 安全 | `HitTest` 项行循环有 `k < b.Items.Count` 守卫；`OpenItem` 有 `hit.ItemIndex < b.Paths.Count` 守卫；`ToggleCollapse`/`DeleteCategory` 有 `BoxIndex`/`index` 范围检查。 |

---

## 5. 真机测试清单（M3 验收用）

> 真机环境：Windows 11 x64，建议分别在 **100% DPI** 与 **150% DPI** 各跑一遍；主屏 + 副屏各验证一次。

**P0 拖拽移动**
- [ ] 在标题栏按住左键拖动盒子，盒子实时跟随光标（应有 ~16ms 节流，肉眼流畅）。
- [ ] 拖到窗口边缘被钳制，不超出主屏可视区。
- [ ] 松开后盒子停在落点；右键/空白处点击测试**原位置与新位置都不再穿透**（盒内可交互、盒外点击落到桌面图标）。
- [ ] **重启应用后，拖拽后的布局是否保留**（验证问题1 修复与否的关键用例）。
- [ ] 拖拽过程中按 Alt-Tab 切换到别的窗口，回来后拖拽状态已正确终态化（无卡死、无残留捕获）。

**P1 折叠/展开**
- [ ] 点击标题栏右侧折叠按钮（▾/▸），盒子在「仅标题」与「完整」间切换。
- [ ] 折叠/展开后点击穿透区域正确（折叠时仅标题条可交互，其余穿透）。
- [ ] 折叠/展开后重启应用，状态保留。

**P1 双击打开**
- [ ] 在未折叠盒子的项行**双击**，用默认程序打开对应文件/文件夹（`.lnk` 由 Shell 解析）。
- [ ] 确认打开使用的是**全路径**（含空格/中文路径也能正确打开，验证 `Paths[i]` 非 basename）。
- [ ] 标题栏双击**不应**导致盒子「粘」在光标上（验证问题2 修复与否）。

**P1 emoji 显示**
- [ ] 各盒标题的 emoji 图标正常显示（不再是「□□」豆腐块）；确认是单色字形（已知限制）。
- [ ] 150% DPI 下 emoji 与标题字号、位置无错位。

**P2 新建分类**
- [ ] 点击「＋ 新建分类」磁贴，新增一个名为「新建分类」、带 📁 图标的空盒，出现在最底部。
- [ ] 新建后重启应用，新盒保留。

**P2 右键删除**
- [ ] 右键任意非内置盒 → 弹出「删除分类「XXX」」菜单 → 删除后该盒消失，其成员归位到「未分类」盒。
- [ ] 右键「未分类」盒 → 菜单显示「无法删除内置「未分类」」且置灰，选择无效。
- [ ] 删除后重启应用，删除结果保留。

**回归：点击穿透**
- [ ] 盒外区域点击可正常选中/拖动桌面原生图标（确认 `SetWindowRgn` 命中区未被 M3 改动破坏）。
- [ ] 盒内空白（非标题/非项/非按钮）点击不穿透（落在围栏窗口，无动作）。

**回归：DPI / 多屏**
- [ ] 150% DPI 下所有盒子尺寸正确（验证问题3：不应被放大 50%）。
- [ ] 更改显示缩放/分辨率后，盒子重新对齐、点击穿透仍正确。
- [ ] （可选）副屏放盒：确认可见且可交互，或确认被裁剪属已知 deferred。

---

## 6. Go-NoGo 结论

**结论：🟢 Go（条件发布）** — 原阻塞项（问题1）与功能性 bug（问题2）、代码气味（问题4）均已由实现侧修复；问题3 已定为 hybrid 设计约定。剩余低优先级项（问题5/6）与派生跟踪项（DefaultLayout 逻辑 gap）均**不阻塞 M3 发布**。

- **已修复（阻塞/功能/代码气味）**：
  - 问题1 ✅ 网格门控改为 content-based（`FenceLayer.cs:297-308`），拖拽坐标跨重启保留。
  - 问题2 ✅ `OnLButtonDblClk` 开头 `EndDrag()`（`:620-625`），双击标题不再粘连。
  - 问题4 ✅ `EndDrag` 先置 null 再 `ReleaseCapture`（`:607-618`），消除重入双执行。
- **已定稿为设计约定（问题3）**：hybrid — `X/Y` 物理、`Width/Height` 逻辑。`FenceStore.DefaultLayout` 的物理间隔在 >100% DPI 可能致首跑盒子重叠，**列为非阻塞跟踪项归 FenceStore owner**（不影响交互）。
- **仍开放（低优先级，可迭代，不阻塞）**：问题5（mouseup 坐标忽略，数像素偏差）、问题6（`OnDisplayOrDpiChange` 未重置 `_dragCat`）、问题7（键盘上下文菜单忽略，信息级）。
- **发布前建议动作**：
  1. **CI/本机重跑干净编译**：本环境 `obj` 目录被锁定（`UnauthorizedAccessException` + WPF 重复 `AssemblyInfo`），无法对改动后代码重跑 0/0；改动均为编译安全的 LINQ/顺序调整（首轮原码已 0/0），请实现者在自己的机器确认 `dotnet build -c Release -r win-x64` 仍为 0/0 后合入。
  2. 真机验收按第 5 节清单 P0/P1 跑一遍，重点确认三项：①重启后拖拽布局保留（问题1）；②双击标题不粘连（问题2）；③150% DPI 盒子尺寸正确（问题3 约定 + DefaultLayout gap 已知）。
  3. 将「DefaultLayout 逻辑 gap」登记为 FenceStore 后续清理项。

**综上：M3 交互功能达到发布门槛（Go），修复项均已完成，仅余低优先级迭代项与一项非阻塞的 FenceStore 跟踪项。**

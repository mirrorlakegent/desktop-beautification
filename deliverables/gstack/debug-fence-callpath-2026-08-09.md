# 桌面整理（Fences）调用链排查 · 2026-08-09（第四轮）

**日期**：2026-08-09
**角色**：排障手（gstack-investigator）
**结论**：🟢 调用链在源码中已正确（Show 必被调用）；真因是**围栏激活路径成功分支从未打日志**，导致诊断日志出现"壁纸有记录、围栏零记录"的假象。已补齐与壁纸同级的日志并重新编译。

---

## 1. 真正的断点：不在调用链，在日志盲区

通读 `MainWindow.xaml.cs` 与 `FenceLayer.cs`，结论与预设假设**相反**：当前源码 `EnableFences()`（MainWindow.xaml.cs:661）**确实调用**了 `_fenceLayer = new FenceLayer(); _fenceLayer.Show(items.ToArray(), layout);`。`_fenceLayer` 是字段（:27），经 `BtnToggleFences_Click → ToggleFences → Dispatcher → EnableFences` 唯一触发（XAML:63 已绑定）。**没有任何分支"只隐藏图标不 Show"**。

`FenceLayer.Show`（:89）**没有** `if(_source!=null) return` 之类的跳过逻辑；`CreateDesktopWindow` 的早退（:107 `if(_source!=null) return;`）仅用于同实例重入防护，首次点击 `_source` 必为 null，不会跳过。

值得注意的是，前两轮复盘报告（debug-fences-refix-2026-08-09.md、debug-fences-zorder-2026-08-09.md）自己就写明"EnableFences 必然走到 `new FenceLayer()+Show()`（窗口一定创建）"。因此"Show 没被调用"这一前提**不成立**。

那为何诊断日志"完全没有 fence 记录"？因为**整条围栏激活路径的成功分支从未打 `HostLog`**，而对照已验证可用的壁纸路径 `WallpaperChildWindow.Create`（:102）会打 `Child window created: 0x…`。壁纸有日志、围栏没日志——这**不是**"Show 没被调用"的证据，而是"围栏路径全程静默"的证据。日志系统本身正常（壁纸能打），只是代码没让它打。

**真因判定**：用户真机的"第四次失败"要么跑了旧部署二进制（不含 HwndSource 重写 / Show 调用），要么窗口已建但因可见性/挂载落在另一层（属团队指示保留的"另一层面"，本次不动）。无论哪种，**缺口都是缺少观测日志**，使我们无法区分"没调用"与"调了但没显示"。本次把日志补齐后，下一次真机运行即可一锤定音。

---

## 2. 本次改动（仅补日志 + 编译，未动 z-order / 分层 / HwndSource 挂载方式）

**`MainWindow.xaml.cs`**
- `ToggleFences`（:616 忙跳过、:621 入口 enable/字段状态、:643/:650 `_shuttingDown` 跳过——原均静默）
- `EnableFences`（:676 入口打印 items/categories 数 + 旧 FencesEnabled；:679 Show 已调用；:683 完成）
- `DisableFences`（:689 入口）
- 启动重显路径 `ApplyFencesWithRetryIfEnabled`（:721 图标隐藏成功后打印）

**`FenceLayer.cs`**
- `Show`（:91 入口 items/categories；:95 返回含 `_source` 是否创建及 hwnd）
- `CreateDesktopWindow`（:111 重入跳过并打 hwnd；:115 开始；:153 成功打 host+hwnd+尺寸；失败仍走原有 :202）
- `ResolveDesktopHost`（:219/:226 成功打印解析到的宿主 HWND；:234 兜底失败）

原失败分支的 `HostLog.Write` 全部保留，仅新增成功/跳过路径的可见性。

---

## 3. 编译结果

`dotnet build src/DesktopSuite/DesktopSuite.csproj -c Release`（全路径 `C:/Program Files/dotnet/dotnet.exe`）：
**0 错误、0 警告**。
产物：`src/DesktopSuite/bin/Release/net8.0-windows/DesktopSuite.dll`。
编译日志落盘：`D:\WorkBuddy\桌面美化\_build_fencecall.txt`。

---

## 4. 用户下次验收：看这几行

让用户点「启用桌面整理」→ 跑诊断 → 打开 `%LocalAppData%\DesktopSuite\logs\wallpaper.log`：

1. 出现 `EnableFences：入口 items=N categories=M` + `FenceLayer.Show：called` + `CreateDesktopWindow：ok host=0x… hwnd=0x…`
   ⇒ **窗口已创建**，问题在可见性/挂载层（按团队指示属另一层面，本次不动）。
2. 有 `ToggleFences：入口` 但**无** `EnableFences：入口`
   ⇒ `EnableFences` 未执行（多为 `_shuttingDown` 跳过或**二进制仍是旧的**）。
3. 连 `ToggleFences：入口` 都没有
   ⇒ 按钮未触发，二进制确定是旧的，需重部署本次构建。
4. 出现 `FenceLayer.CreateDesktopWindow 失败`
   ⇒ 创建抛异常，按堆栈修复。

> 备注：建议将本次 `dotnet build -c Release` 产物重新打包自包含 exe 并交付用户复验；沙箱无交互桌面，无法 GUI 真机验收，层级/可见性效果须由用户确认。

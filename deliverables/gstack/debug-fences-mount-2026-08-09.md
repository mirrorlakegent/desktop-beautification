# Fences 收口修复：挂载父窗口 Progman → WorkerW（2026-08-09）

## 改动文件与行号
1. `src/DesktopSuite/Wallpaper/WorkerWHost.cs`
   - L52 新增 `public static bool TryFindWallpaperSurface(out IntPtr handle)`：复用现有 `TryResolve`（未改），解析壁纸使用的 WorkerW，无可用时回退 Progman。
2. `src/DesktopSuite/Desktop/Organizer/FenceLayer.cs`
   - L231 `ResolveDesktopHost()` 改为优先 `WorkerWHost.TryFindWallpaperSurface`（复用壁纸同源 WorkerW），仅在该路径失败才回退 Progman。
   - L479 `ApplyRegion()` 的 `combined==null` 安全网：原先 `SetWindowRgn(_hwnd, IntPtr.Zero)`（整窗全透明、整层消失）改为创建 `CreateRectRgn(0,0,_winW,_winH)` 全屏可见区域并写日志 `无命中区，回退全屏可见`。
   - L84 `_diagFullWindow` 由 `true` 改为 `false`：关闭全屏诊断覆盖层，让真实盒子区域生效（验收真实围栏盒而非全屏深色）。

注：`FenceLayer.cs` 顶部已有 `using DesktopSuite.Wallpaper;`（L8），无需新增。

## 构建结果
- 命令：`C:/Program Files/dotnet/dotnet.exe build src/DesktopSuite/DesktopSuite.csproj -c Release`
- 日志：`D:\WorkBuddy\桌面美化\_build_fencemount.txt`
- 结果：**生成成功，0 个警告，0 个错误**，耗时 00:00:04.35。

## 判断：本次用户应能看到真实深色分类盒子
铁证显示窗口本身建得正确（`CreateDesktopWindow：ok host=0xF05E4 size=1920x1080` + 整窗区域已设），但挂在 Progman 本体（0x101CA），而 DWM 在 WorkerW 形态下不会合成 Progman 的直接子窗口——这正是"建了却不画"的根因。现挂载点改为与壁纸同源的 WorkerW（0x2049A），绘制通道打通。叠加两点：(a) `DefaultLayout()` 产出 5 个非空分类（未分类/工作/娱乐/工具/临时），均含有效 X/Y 与 240×280，盒子坐标有效；(b) `_diagFullWindow=false` 使真实逐盒区域生效，盒子应显示在正确坐标处。因此本次真机验收，用户应能看到 5 个深色分类盒子叠加在隐藏图标之上。若仍空白，则属更深的 WorkerW 解析/壳层变体问题，需查 `CreateDesktopWindow：ok host=…` 返回的 host 是否为 WorkerW 而非 Progman。

（不自行 repackage，由主理人发布自包含包。）

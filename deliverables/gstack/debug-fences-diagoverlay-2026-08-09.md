# Fences 全屏诊断覆盖层 + 默认分类验证报告（2026-08-09）

## 1. 全屏深色诊断覆盖层（新增，仅调试构建）
文件：`src/DesktopSuite/Desktop/Organizer/FenceLayer.cs`

- L83–84 新增字段：
  - `private bool _diagFullWindow = true;`
  - `private int _winW, _winH;`
- `CreateDesktopWindow()` 解析出最终窗口尺寸后缓存（L147 `_winW = w;` + L149 日志 `FenceLayer.CreateDesktopWindow：缓存窗口尺寸 _winW=… _winH=…`）。
- `ApplyRegion()` 开头插入诊断分支（L405 方法体，L413 `if (_diagFullWindow)`）：开启时跳过逐盒裁剪，直接
  `SetWindowRgn(_hwnd, CreateRectRgn(0,0,_winW,_winH), true)` 让整窗可见，写日志（L421）
  `FenceLayer.ApplyRegion DIAG: full-window visible region set`；`_hwnd` 为 0 或尺寸无效则回落正常 region。

此前 4 次失败根因并非调用链断裂（round1 已确认 `EnableFences`→`new FenceLayer().Show()` 执行正确），而是成功路径无日志、且无法区分"渲染但被 region 裁透明"与"从未绘制"。本覆盖层即决定性测试。

## 2. 默认分类验证结论
文件：`src/DesktopSuite/Desktop/Organizer/FenceStore.cs`

- `Load()` 在 `fences.json` 缺失/损坏时回退 `DefaultLayout()`（L48）；`EnsureBuiltInCategories` 保证"未分类"兜底盒存在。
- `DefaultLayout()`（L87）产出 **5 个非空分类**，每个均有有效 X/Y 与 Width=240/Height=280：
  - 未分类（UncategorizedId，置于 col0,row2）、工作（col0,row0）、娱乐（col1,row0）、工具（col0,row1）、临时（col1,row1），位置由 `PrimaryWorkArea()`（MONITORINFO，虚拟屏兜底）计算。
- 结论：空白屏**不能**用"无分类"或"盒尺寸为 0"解释；分类与坐标完全有效，问题在渲染/挂载层。

## 3. 构建结果
- 命令：`C:/Program Files/dotnet/dotnet.exe build src/DesktopSuite/DesktopSuite.csproj -c Release`
- 日志：`D:\WorkBuddy\桌面美化\_build_fencecall2.txt`
- 结果：**生成成功，0 个警告，0 个错误**，耗时 00:00:05.91。

## 4. 下一步（真机验证，不要重新打包）
本构建为调试诊断用途，由维护者自行发布自包含包。真机点击"启用桌面整理"后：

- 看到**全屏深色/黑屏** → 窗口已渲染且挂载成功（排除挂载层问题）；下轮将 `_diagFullWindow=false` 恢复逐盒 region，再排查 box 坐标/区域对齐。
- **仍无任何画面** → 窗口从未绘制（WS/父窗口/HwndSource 挂载问题，另一层）；查日志是否出现
  `CreateDesktopWindow：ok host=… hwnd=…`、`ResolveDesktopHost` 返回值，以及 `ApplyRegion DIAG: full-window visible region set` 是否写入。

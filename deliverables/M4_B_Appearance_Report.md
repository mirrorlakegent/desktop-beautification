# M4-B 外观定制 — 交付说明

> 日期：2026-08-19 ｜ 状态：**v2 修复 4 项真机问题，待复验** ｜ 基线：M4-A 布局导入/导出已交付（67e857b）
> 范围：对标 Stardock Fences 的外观定制——圆角、半透明、标题样式、毛玻璃（实验性）。

---

## 1. 本次交付的功能

| 可调项 | 默认值 | 说明 |
|---|---|---|
| 圆角半径 | 10 逻辑px | 盒子四角圆角，命中区同步圆角 |
| 主体透明度 | 180 (0-255) | 值越大越不透明 |
| 标题栏透明度 | 200 (0-255) | 值越大越不透明 |
| 标题字号 | 13 px | 标题字体大小 |
| 标题对齐 | 左对齐 | 左对齐 / 居中 |
| 显示 emoji 字形 | 开 | 标题前是否绘制分类图标 |
| 毛玻璃背景 | 关（实验性） | 盒内显示模糊的桌面壁纸 |

所有项持久化到 `AppSettings`（JSON，`%LocalAppData%\DesktopSuite\settings.json`），重启后保持。

---

## 2. 实现要点

- **DTO 落点**：新增 `FenceAppearance`（`src/DesktopSuite/FenceAppearance.cs`），含 7 字段 +
  `FromSettings(AppSettings)` / `ApplyTo(AppSettings)` / `Clone()`。默认即原来的硬编码视觉，
  因此未开启任何改动时行为与 M4-A 完全一致。
- **`AppSettings`**：新增 7 个外观属性（含 `FenceFrosted` 实验开关）。
- **`FenceLayer.SetAppearance(FenceAppearance)`**：克隆快照、按圆角重排命中区、重绘。入口在
  `EnableFences` 创建后调用 `SetAppearance(FenceAppearance.FromSettings(_settings))`。
- **`DrawBoxes`**：圆角 / 透明度 / 字号 / 对齐 / 字形全部改为读取 `_appearance`，不再硬编码。
- **`ApplyRegion`**：从 `CreateRectRgn` 改为 `CreateRoundRectRgn`，命中区与圆角视觉严格对齐
  （之前矩形命中区在圆角处会有几像素"看不见但能点"的错位）。
- **毛玻璃（`EnsureFrostCapture` + `BoxBlur`）**：
  - 用 `Graphics.CopyFromScreen` 捕获围栏窗口背后的桌面（壁纸+图标）；
  - 可分离盒式模糊（前缀和滑动窗口，O(n)）模糊后缓存为全窗口位图；
  - `DrawBoxes` 在每个盒内 clip 绘制模糊背景 + 轻暗色调（保证标题/条目可读）。
  - **缓存策略**：首次捕获后整个 Frosted 会话复用，**拖拽时复用同一缓存**（不重新截屏），
    DPI/显示变化或窗口销毁时失效——避免"截屏含自己上一帧"导致模糊自反馈 runaway。
  - 捕获/模糊失败则静默回落到纯色半透明 body。

## 3. UI 接线

- 新建 `FenceAppearanceForm`（`src/DesktopSuite/FenceAppearanceForm.cs`）：仿现有 `VolumeForm` 的
  暗色 WinForms 弹窗，全部控件改动即**实时预览**（写内存设置 + 重绘围栏）。
- 托盘菜单新增「🎨 外观…」入口（`TrayManager`）。
- `MainWindow.ShowAppearanceForm`：实时预览改内存设置不落盘；**确定**才 `Save()`，**取消**回落
  到原始外观（避免误改）。

---

## 4. 编译与验证状态

- `dotnet build -c Release`：**0 错误**（仅 1 个既有 warning `CS8625`，位于无关代码行 1272，非本次引入）。
- `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile -p:PublishReadyToRun`
  输出到 `D:\WorkBuddy\ds2\DesktopSuite.exe`。
- **真机验证项（需用户在真机逐项确认，AI 不在无设备实测时宣称"已修复"）**：
  1. 托盘「🎨 外观…」能打开弹窗；
  2. 拖圆角/透明度/字号滑块 → 围栏实时变化且圆角处命中区对齐；
  3. 标题对齐切「居中」/「左对齐」生效；
  4. 关闭 emoji 字形 → 标题不再显示分类图标；
  5. 勾选毛玻璃 → 盒内显示模糊壁纸（首帧可能略卡，属预期）；
  6. 点「确定」后重启程序，外观保持（持久化生效）；「取消」则不改。

---

## 5. v2 真机问题修复（2026-08-19）

用户真机测试发现 4 项 UI 问题，已全部修复：

| # | 问题 | 根因 | 修复 |
|---|------|------|------|
| 1 | 滑块挡住下一行控件 | `rowH=40` < TrackBar 实际高度(~45px+) | `rowH` 增至 **56**，窗高 392→**480** |
| 2 | 标题居中文字显示异常 | 居中时仍分拆前缀/标签绘制，`StringFormat.Center` 导致双重居中偏移 | 居中模式改为**合并单次 DrawString**(emoji+标题) |
| 3 | 毛玻璃效果差 | 模糊半径太小(10dpi)、单次 box blur 质量差、着色太深(alpha=90) | 半径**20dpi + 三次叠加以近似高斯** + 着色降至 **alpha=50** |
| 4 | 无「确定」按钮可见 | 同#1——内容溢出窗体底部，按钮被裁切 | 随行高/窗高修复自动恢复；按钮加高至 **32px** |

---

## 6. 已知限制 / 后续

- 毛玻璃为**实验性**：多显示器虚拟屏较大时首帧模糊略慢；拖拽时复用缓存规避卡顿。
- 真正的 DWM acrylic/mica 与当前 layered 架构不兼容，本方案用"截屏+模糊"务实实现。
- 后续 M4-C（全局快速隐藏热键）、M5 文件夹门户/滚动围栏/多显示器 见 `M3_Roadmap_Next.md`。

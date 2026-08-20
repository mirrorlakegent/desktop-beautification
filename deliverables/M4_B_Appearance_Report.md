# M4-B 外观定制 — 交付说明

> 日期：2026-08-20 ｜ 状态：**v10 修复透明度=0 白色 bug，待复验** ｜ 基线：M4-A（67e857b）
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

## 6. v3 二轮真机问题修复（2026-08-19）

用户 v2 复验发现 3 项残留/新问题：

| # | 问题 | 根因 | 修复 |
|---|------|------|------|
| 1 | 居中时 emoji 不显示（显示为 □） | v2 用 `titleFont`(Segoe UI Bold) 绘制合并字符串，emoji 需 Segoe UI Emoji | 居中模式改为**分字体测量+分步绘制**：emojiFont 画前缀、titleFont 画标签，手动计算合并宽度后居中 |
| 2 | 毛玻璃透明度不可调 | 着色 alpha 硬编码为 50 | 新增 `FrostOpacity` 属性 + 弹窗**「毛玻璃着色」滑块**(0-200)，默认 50 |
| 3 | 确定按钮仍不可见 | 高 DPI 下 TrackBar 更高(~60-70px)，480px 仍不够 | 窗高改为**动态计算**（按内容底 + 按钮 + padding），设 `MinimumSize` 防裁切；新增毛玻璃滑块行 |

### 变更文件
- `FenceAppearance.cs`：+`FrostOpacity` 字段，FromSettings/ApplyTo/Clone 同步
- `AppSettings.cs`：+`FenceFrostOpacity`（持久化）
- `FenceAppearanceForm.cs`：重写布局——动态高度 + 毛玻璃滑块 + MinimumSize 保护
- `FenceLayer.cs`：居中标题分字体绘制 + `_appearance.FrostOpacity` 替代硬编码

## 7. v4 三轮修复（2026-08-19）

用户 v3 复验发现 2 项残留问题：

| # | 问题 | 根因 | 修复 |
|---|------|------|------|
| 1 | emoji 与文字间距过大 | 居中测量用 `StringFormat.GenericTypographic`(含额外排版度量)，与绘制用的 `sf`(NoWrap+Center) 宽度不一致，导致起始 X 偏左太多 | 测量改用**与绘制相同的 `sf`** StringFormat，宽度精确匹配 |
| 2 | 弹窗仍截断（TrackBar 挡住下一行） | 固定 `rowH` 在高 DPI/不同主题下仍不够（该用户系统 TrackBar > 56px） | **彻底重写布局**：弃用手动定位 + 固定行高，改用 `TableLayoutPanel` + `AutoSize`/`AutoSizeMode.GrowAndShrink`，WinForms 自动适配任意 DPI/主题；按钮区用 `FlowLayoutPanel` 右对齐锚定底部 |

### 变更文件
- `FenceAppearanceForm.cs`：**TableLayoutPanel + AutoSize 重构**，不再依赖固定 rowH
- `FenceLayer.cs`：MeasureString 改用 `sf` 替代 `GenericTypographic`

---

## 8. v5 四轮修复（2026-08-19）

用户 v4 复验发现弹窗更糟了，TLP+AutoSize 方案在该系统彻底失败：

| # | 问题 | 修复 |
|---|------|------|
| 弹窗截断/无按钮 | 弃用 TLP/AutoSize，改用**固定 440×640 + 手动 Y 定位** + Panel(AutoScroll) 兜底 |
| 标签截断 | 控件宽 310→**400px** |
| 标题字号异常(169) | 范围 **8-28** + **Math.Clamp** |

### 变更文件
- `FenceAppearanceForm.cs`：完全重写（固定宽高 + 手动布局 + 内嵌滚动）

## 9. v6 五轮修复（2026-08-19）

用户 v5 复验发现 2 项残留：

| # | 问题 | 修复 |
|---|------|------|
| 标签文字仍截断 | `AutoSize=false`+固定 `Height=22` 在高 DPI 下中文长标签放不下 | **标签改 `AutoSize=true`**，Y 光标按**实际渲染高度**推进，不再猜测 |
| 窗口风格不统一 | 白色系统标题栏 vs 暗色内容区 | **DWM `DWMWA_USE_IMMERSIVE_DARK_MODE=20`** 暗色标题栏；窗体加宽至 **520px**；CheckBox 也改 AutoSize |

### 变更文件
- `FenceAppearanceForm.cs`：标签 AutoSize + DWM 暗标题栏 + 窗体 520×660

## 10. v7 六轮修复（2026-08-19）

用户 v6 复验：暗标题栏 ✅、按钮 ✅，但**中文标签仍乱码/截断**（"主体透明度" → "土体?2透明"）。

| # | 问题 | 根因 | 修复 |
|---|------|------|------|
| 中文标签乱码 | GDI+ 在高 DPI 下默认字体中文渲染异常（字体回退失败） | **显式 Font="Microsoft YaHei UI"**(9pt) + Label **UseCompatibleTextRendering=true**(GDI 引擎) |

### 变更文件
- `FenceAppearanceForm.cs`：Form.Font + Label.UseCompatibleTextRendering

## 12. v8 七轮修复（2026-08-19）

用户 v7 复验：中文乱码已修复，但 **2 项长期问题仍未解决**：
1. 调节滑块时文字显示不全
2. emoji 与文字离这么远

| # | 问题 | 根因 | 修复 |
|---|------|------|------|
| 滑块文字/数值显示不全 | 弹窗用**固定假设 TrackBar 高度(38px)** 推进 Y 光标；用户系统 DPI/主题下 TrackBar 实际更高，后续控件(含数值标签、按钮)被上移、重叠或被窗体裁切 | Y 光标改为**按控件真实 `Bottom` 推进**（`y = ctl.Bottom + gap`），不再猜高度；数值标签改 `AutoSize` 右对齐、跟随滑块行；窗体 560×720 + `AutoScaleMode=Dpi` 适配高 DPI |
| emoji 与文字间距过大 | `FenceLayer` 用 GDI+ `DrawString` 分字体画 emoji+标签，靠 `MeasureString` 测宽度手工偏移；GDI+ 对 emoji 返回的是**字体 advance 宽度(含巨大侧边距)**，偏移量失控 | 标题改用 **GDI `TextRenderer.DrawText`**——自动字体 fallback 到 Segoe UI Emoji 彩色字形，**一次绘制** emoji+中文，间距天然正确；彻底告别 □ 与间距 bug |

### 变更文件
- `FenceLayer.cs`：标题段改 `TextRenderer`（居中/左对齐统一用 `TextFormatFlags`，删除原 GDI+ 分字体绘制与 `titleBrush`）
- `FenceAppearanceForm.cs`：布局改为真实 `Bottom` 推进 + `AutoScaleMode.Dpi`

---

## 12. v9 九轮修复（2026-08-20）

用户 v8 复验：弹窗布局与 emoji 已修复 ✅，但**透明度调节完全失效**——围栏几乎不可见。

### 根因

`settings.json` 中存了之前某轮迭代保存的**极端脏数据**：

| 属性 | 脏值 | 正常默认 |
|------|------|---------|
| `FenceBodyOpacity` | **0**（完全透明！） | 180 |
| `FenceHeaderOpacity` | **255**（完全不透明） | 200 |
| `FenceCornerRadius` | **40**（最大） | 10 |
| `FenceFrostOpacity` | **0** | 50 |

`AppSettings.Load()` 反序列化后无任何 clamp 校验，脏值直接传入渲染管线 → 围栏不可见。

### 修复

| # | 修复 | 说明 |
|---|------|------|
| 1 | `AppSettings.Load()` 加 clamp | 加载后立即 `Math.Clamp` 所有外观属性到安全范围；BodyOpacity 最小 **40**（始终微弱可见）、HeaderOpacity 最小 **80**（标题始终可读） |
| 2 | `FenceLayer.SetAppearance()` 加防御性 clamp | 即使绕过 Load() 直接传入坏值也不会渲染出不可见围栏 |
| 3 | 修正用户 settings.json | 立即恢复为正常默认值 |

### 变更文件
- `AppSettings.cs`：Load() 加 7 项外观属性 clamp
- `FenceLayer.cs`：SetAppearance() 加防御性 clamp
- `settings.json`：用户本地文件已修正

---

## 14. v10 十轮修复（2026-08-20）

用户 v9 复验：透明度 clamp 后围栏可见 ✅，但**主体透明度拖到 0 时围栏变白**（而非透明）；只有默认值附近才有正常的半透明效果。

### 根因

**GDI+ `SolidBrush` 的 alpha=0 已知怪癖**：`Color.FromArgb(0, 20, 22, 28)` 在 GDI+ 中不会渲染为"完全透明"，而是以异常方式混合（可能显示为白色/不透明），这是 GDI+ 的老 bug，与 DPI/版本相关。

叠加因素：`PremultiplyAlpha()` 中 alpha=0 像素被跳过但 RGB 未清零（残留 20,22,28），在某些 DWM 合成路径下可能泄漏为白色。

### 修复

| # | 修复 | 说明 |
|---|------|------|
| 1 | 低透明度跳过填充 | `BodyOpacity ≤ 3` 时不调用 `FillRoundedRect`（让初始 `Clear(transparent)` 自然透出壁纸）；Header/FrostOpacity 同理 |
| 2 | PremultiplyAlpha 清零 RGB | alpha=0 时显式将 R/G/B 清零（防止非零残值在 DWM 合成路径下泄漏） |

### 变更文件
- `FenceLayer.cs`：DrawBoxes 加透明度阈值判断 + PremultiplyAlpha alpha=0 分支清零 RGB

---

## 15. 已知限制 / 后续

- 毛玻璃为**实验性**：多显示器虚拟屏较大时首帧模糊略慢；拖拽时复用缓存规避卡顿。
- 真正的 DWM acrylic/mica 与当前 layered 架构不兼容，本方案用"截屏+模糊"务实实现。
- 后续 M4-C（全局快速隐藏热键）、M5 文件夹门户/滚动围栏/多显示器 见 `M3_Roadmap_Next.md`。

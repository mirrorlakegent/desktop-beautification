# M4-B 外观定制 — 交付说明

> 最后更新：2026-08-31 ｜ 状态：**v25h（功能稳定，毛玻璃性能优化已发布，待真机复验）** ｜ 基线：M4-A（67e857b）
> 范围：对标 Stardock Fences 的外观定制——圆角、半透明、标题样式、**毛玻璃背景（已转正，非实验性）**。

---

## 1. 当前交付的功能（8 项可调）

| 可调项 | 默认值 | 范围 | 说明 |
|---|---|---|---|
| 圆角半径 | 10 逻辑px | 0–40 | 盒子四角圆角，命中区同步圆角 |
| 主体透明度 | 180 (0-255) | 0–255 | 值越大越不透明；0 = 真正透出壁纸（可点穿到桌面图标） |
| 标题栏透明度 | 200 (0-255) | 0–255 | 值越大越不透明 |
| 标题字号 | 13 px | 8–28 | 标题字体大小 |
| 标题对齐 | 左对齐 | 左/居中 | 左对齐 / 居中 |
| 显示 emoji 字形 | 开 | 开/关 | 标题前是否绘制分类图标 |
| 毛玻璃背景 | 关 | 开/关 | 盒内显示**模糊的当前桌面壁纸** |
| 毛玻璃着色 | 50 (0-200) | 0–200 | 越小越透，越大越暗（保证标题/条目可读） |

所有项持久化到 `AppSettings`（`%LocalAppData%\DesktopSuite\settings.json`），重启后保持。

代码落点：
- `FenceAppearance`（`src/DesktopSuite/FenceAppearance.cs`）：8 字段 + `FromSettings` / `ApplyTo` / `Clone`。
- `AppSettings`（`AppSettings.cs`）：8 个外观属性，`Load()` 内 `Math.Clamp` 防御脏数据。
- `FenceLayer.SetAppearance(FenceAppearance)`：克隆快照、重排命中区、重绘。
- `FenceAppearanceForm`（`FenceAppearanceForm.cs`）：暗色 WinForms 弹窗，**实时预览**。

---

## 2. 毛玻璃背景：当前真实实现（v25d+，已转正）

> ⚠️ 旧的"截屏+模糊"方案已**彻底废弃**，请勿再引用。本段为权威描述。

### 2.1 取图方式：直接读壁纸文件（非截屏）
`EnsureFrostCapture()` 通过 `LoadWallpaperFrost()` 直接加载壁纸**文件**，优先级：
1. `AppSettings.LastMedia` —— DesktopSuite 自身当前壁纸（覆盖 static + dynamic 轮换，由 `WallpaperRotator` 写入）；
2. `IDesktopWallpaper` COM（`GetWallpaper()`）——系统级兜底；
3. 以上都取不到 → 返回 `null`，降级为普通半透明 body。

**视频壁纸**（`.mp4/.mkv/.mov/.webm/.avi`）无法 `Image.FromFile`，直接返回 `null` → 干净降级（不崩、不白屏）。

### 2.2 为什么放弃截屏（关键教训）
v21–v25c 用 `Graphics.CopyFromScreen` 截屏自家窗口背后的桌面。但本窗口是**桌面 WorkerW 的子窗口**——DWM 会缓存窗口上一帧的 `UpdateLayeredWindow` 合成结果。任何"隐藏自己再截屏"的方案都在与这个缓存搏斗，产生反馈循环（模糊套模糊 / 全白 / 多盒不一致）。SW_HIDE、PushTransparentFrame、移出屏幕等绕过手段在本环境均不可靠。**结论：不截屏，直接从数据源取。**

### 2.3 模糊与缓存
- 可分离盒式模糊（`BoxBlur`，前缀和滑动窗口 O(n)），**三遍叠加**近似高斯质量。
- `DrawBoxes` 在每个盒内 clip 绘制模糊背景 + 轻暗色调（`FillRoundedRectWithAlpha`，Opaque brush + ColorMatrix，避开 GDI+ 低 alpha 缺陷）。
- `_frostBmp` **整个毛玻璃会话复用**，拖拽时也复用同一缓存（不重新取图），避免任何反馈循环。
- 失效时机：`WM_DISPLAYCHANGE` / `WM_DPICHANGED`、毛玻璃被关闭、或壁纸变化时 `InvalidateFrost()` 清空缓存。

### 2.4 壁纸变化联动刷新（v25e / v25f / v25g）
- 事件链：`WallpaperRotator.WallpaperApplied` → `FenceLayer.RequestFrostRefresh()`（`PostMessage WM_FROST_REFRESH`，线程安全）→ `InvalidateFrost()` + `UpdateVisual()` 重取壁纸。
- **启动竞态修复（v25g）**：`WallpaperRotator` 的线程池 `Tick()` 可能在 `EnableFences()` 创建 `_fenceLayer` 之前就完成并触发 `WallpaperApplied`。新增 `_pendingFrostRefresh` 闩锁，在 `EnableFences()` 创建层后消费，确保启动期积压的刷新不丢。
- **视频壁纸 + 延迟（v25g）**：视频扩展名提前跳过 `Image.FromFile` 异常；并**彻底移除截屏 fallback**（v22–v25c 全系列失败），降级为半透明 body。

### 2.5 性能优化（v25h，本轮发布）
将模糊从**全分辨率**改为先缩到 **1/3 分辨率**模糊、再放大绘制：
```csharp
const float FROST_BLUR_SCALE = 1.0f / 3.0f;
int workW  = Math.Max(1, (int)Math.Round(_winW * FROST_BLUR_SCALE));
int workH  = Math.Max(1, (int)Math.Round(_winH * FROST_BLUR_SCALE));
int workRadius = Math.Max(1, (int)Math.Round(radius * FROST_BLUR_SCALE));
// raw → work（HighQualityBicubic 缩图）→ BoxBlur ×3 → _frostBmp（1/3 分辨率）
// DrawBoxes 用 HighQualityBicubic 把 _frostBmp 放大铺满全窗
```
计算量降至约 **1/9**（每遍处理 1/9 像素 × 3 遍）；模糊本身掩盖放大插值痕迹，观感不变。
**单行回退开关**：`FROST_BLUR_SCALE = 1.0f` 即恢复全分辨率模糊。

---

## 3. 透明度"=0 变白"根因（v17 真机根因修复）
旧报告（v10）将"主体透明度拖到 0 变白"归因于 GDI+ `SolidBrush` alpha=0 怪癖，那是**妥协方案**（最小 alpha 1→20 绕过）。真机根因实为：

> `FenceLayer.UpdateVisual` 用 `Bitmap.GetHbitmap()` 的**无参重载**合成到 DWM，该重载**不保留 alpha 通道**（合成到背景、丢 alpha），导致极低 alpha 渲染成白色。

**根治（v17，✅ 真机验证通过）**：`NativeMethods` 新增 `CreateDIBSection` + 40 字节 `BITMAPINFO`；`FenceLayer` 新增 `CreateAlphaDib` / `CopyPremultipliedToDib` 两个 static helper，用 `CreateDIBSection` + 拷贝 **premultiplied scan0** 替代 `GetHbitmap`，精确保留 alpha。随后 `FillBodyPixels` 允许真 0（写 `(0,0,0,0)` 真透明）。用户复验：滑块=0 主体**真正透出壁纸（可点穿到桌面图标）**、中段/255 正常、图标文字不被压暗 → **彻底结案**。
v18 审计其余外观属性（`HeaderOpacity`/`FrostOpacity` 走 ColorMatrix 安全路径，无需改代码；边框阈值加固），用户 5 张截图真机复验全部通过，**8 个外观属性零回归**。

---

## 4. UI 接线
- `FenceAppearanceForm`：仿 `VolumeForm` 的暗色 WinForms 弹窗，Y 光标按控件真实 `Bottom` 推进（高 DPI 不复盖），标签 `AutoSize` + `UseCompatibleTextRendering`（GDI 引擎，中文稳定），DWM 暗色标题栏。
- **实时预览**：每个滑块 `ValueChanged` → `Fire()` → `_onPreview(Clone())` → `MainWindow.ShowAppearanceForm` 回调 `applied.ApplyTo(_settings)` + `_fenceLayer.SetAppearance(applied)` 即时重绘。**勾选/取消毛玻璃**也触发 `Fire()`，联动 `SetAppearance`→`UpdateVisual`→`EnsureFrostCapture` 重新取图。
- 托盘菜单「🎨 外观…」（`TrayManager`）。**确定**才 `Save()`；**取消**回落到打开时的原始快照（`original.ApplyTo(_settings)` + `SetAppearance(original)`），避免误改。

---

## 5. 编译与验证状态
- `dotnet publish -c Release -r win-x64 -p:SelfContained=true -p:PublishSingleFile=true -p:PublishReadyToRun=true -o D:/WorkBuddy/ds2`：**成功**（仅预存 warning，无 error）。
- 提交 `d607516`（v25h），已推送 gitee + github 双远程（`git ls-remote` 双方均指向 `d607516`）。
- **真机验证清单（v25h 待用户逐项确认）**：
  1. 托盘「🎨 外观…」打开弹窗；
  2. 拖圆角/透明度/字号滑块 → 围栏实时变化且圆角命中区对齐；
  3. 标题对齐切「居中」/「左对齐」生效；
  4. 关闭 emoji 字形 → 标题不再显示分类图标；
  5. 勾选毛玻璃 → 盒内显示**模糊当前壁纸**（非 Windows 默认 Bloom）；点托盘「立即轮换壁纸」后毛玻璃背景**跟随变化**；
  6. **本轮重点**：毛玻璃刷新/首次取图是否**明显变快**（1/3 分辨率模糊）；
  7. 点「确定」重启程序外观保持；「取消」则不改。

---

## 6. 迭代简史与关键教训（浓缩）

| 阶段 | 关键结论 |
|---|---|
| v1–v8 | 弹窗布局与中文渲染：固定行高/TLP 失败 → 最终 **Y 按控件真实 Bottom 推进 + AutoScaleMode=Dpi + GDI 引擎标签**（固化 skill `winforms-hidpi-layout`）。标题 emoji 间距问题最终用 **GDI `TextRenderer`** 一次绘制解决。 |
| v9 | `settings.json` 脏数据（BodyOpacity=0 等）致围栏不可见 → `Load()`/`SetAppearance` 加 `Math.Clamp` 防御。 |
| v10 | 透明度=0 变白 → 妥协方案（最小 alpha 1→20 绕过）；**非根治**。 |
| v11–v16 | GDI+ 低 alpha 后处理（ColorMatrix / FillBodyPixels / ClearBodyPixels）反复失败，**v14 ClearBodyPixels 设计错误**（无条件覆盖正确 alpha → 全白）已删除。 |
| **v17** | **真机根因**：`GetHbitmap()` 无参重载丢 alpha → 改 `CreateDIBSection` + premultiplied 拷贝。**真机验证通过，彻底结案**。 |
| v18 | 其余 8 属性零回归真机复验通过。 |
| v19–v25c | 毛玻璃截屏路线（SystemEvents / HWND_MESSAGE / PushTransparentFrame / 移出屏幕）**全部失败**——WorkerW 子窗口与 DWM 缓存搏斗不可逆。 |
| **v25d** | 转向**直接读壁纸文件**（`LastMedia` → IDesktopWallpaper → null）。 |
| v25e | 取图优先级修正，毛玻璃显示 DesktopSuite 自身轮换壁纸（非 Bloom）。 |
| v25f | 内部轮换壁纸后毛玻璃自动刷新（新增 `WallpaperApplied` 事件链）。 |
| v25g | 修复启动竞态（`_pendingFrostRefresh` 闩锁）+ 视频壁纸跳过 + **移除截屏 fallback**。用户复验"有点慢但可接受"。 |
| **v25h** | 毛玻璃 1/3 分辨率模糊+放大，性能优化（本轮发布，待真机复验）。 |

> 核心教训：① 窗口是桌面 WorkerW 子窗口时，"隐藏自己再截屏"必与 DWM 缓存搏斗且不可靠——**直接取数据源**；② 后处理清理**不得无条件覆盖**前置步骤已写入的正确数据（v14 教训）；③ 任何"已修复"结论必须**真机复验**后方可结案。

---

## 7. Route B（v26）外观深化 + 外观预设 — 已落地（待真机复验）

> 提交 `c563de0`，已发布 ds2 并推送 gitee+github 双远程（`git ls-remote` 双方一致）。

M4-B 外观属性由 8 项扩展为 **15 项**。新增能力：

| 能力 | 属性 | 默认 | 说明 |
|---|---|---|---|
| 盒阴影 | BoxShadowEnabled / ShadowOffset / ShadowBlur / ShadowOpacity | 关 / 6 / 12 / 90 | 盒外柔和投影，加性绘制 |
| 边框色 | BorderColorR/G/B / BorderOpacity | 64,70,86 / 0 | 0=沿用随主体透明的默认灰边；≥16=自定义色 |
| 标题字体 | TitleFontFamily | Segoe UI | 白名单，加载时非法值回落 Segoe UI |
| 外观预设 | — | — | 具名 JSON 预设，主题引擎种子 |

实现要点：
- **盒阴影** `DrawBoxShadow`：白遮罩 → `BoxBlur`（强制 alpha=255，模糊强度存于 RGB 通道）→ `ColorMatrix` 按强度着色（阴影色复用边框色）。**完全隔离于主体 alpha 管线**，不触碰 v17 根治的 `CreateDIBSection` 路径，故不会重现白 alpha bug。默认关闭，无性能影响。
- **边框**：`BorderOpacity>=16` 时用自定义色描边；否则维持原有"随主体透明度"的灰边（外观不变）。
- **字体**：标题改读 `TitleFontFamily`，复用 v8 的 `TextRenderer` + emoji 回退；白名单防 `new Font` 抛异常。
- **预设** `FencePresetStore`：序列化 `FenceAppearance` 到 `%LocalAppData%\DesktopSuite\appearance-presets\`；托盘「🎨 外观预设」子菜单 + 弹窗下拉可存可取；内置 **默认 / 玻璃拟态 / 极简线框** 三预设。
- **Phase 0 安全网**：`src/publish-ds2.ps1` 固化「解锁 DLL → 归档旧 exe → 单文件发布 → 双远程 ls-remote 校验」。

**真机复验清单（v26 待用户确认）**：① 勾选盒阴影→盒外出现柔和投影且不影响主体透明；② 自定义边框色生效且低不透明度不出现白边；③ 切换标题字体（如微软雅黑）标题正常、emoji 不丢失；④ 选预设/存预设即时生效；⑤ 跑透明度回归矩阵（BodyOpacity=0/15/255、HeaderOpacity=0/10/30、Frosted 开）无回归。

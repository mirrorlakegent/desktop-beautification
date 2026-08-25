# M4-B 外观定制 — 透明度根治(v17·已验证) + 回归加固(v18) 概述

## 真正的根因（v9→v16 七轮变白的源头）
`FenceLayer.cs:UpdateVisual` 用 `bmp.GetHbitmap()`（参数less 重载）把 32bppARGB 位图转成
GDI 位图交给 `UpdateLayeredWindow`。`GetHbitmap()` 会把位图**合成到默认背景色、丢弃 alpha 通道**，
导致 `FillBodyPixels` / `PremultiplyAlpha` 写入的低 alpha 像素在 DWM 中渲染为白色。
v16 的"最小 alpha=20"只是绕过，未实现"滑块=0 透出壁纸"。

## v17 根治改动
1. **`NativeMethods.cs`**：新增 `CreateDIBSection` P/Invoke 与 40 字节 `BITMAPINFO` 结构体（32bpp BI_RGB）。
2. **`FenceLayer.cs` 新增两个 static helper**：
   - `CreateAlphaDib(hdc, src, out ppvBits)`：`CreateDIBSection` 建顶向下 DIB（`biHeight=-h`，
     与 GDI+ 顶向下 scan0 对齐），返回 HBITMAP 与像素指针。**精确保留 alpha 通道**。
   - `CopyPremultipliedToDib(src, ppvBits)`：LockBits 读 premultiplied scan0，逐行拷贝到 DIB 像素缓冲
     （处理 stride 差），随后 UnlockBits；像素由 HBITMAP 拥有。
3. **`UpdateVisual`**：用 `CreateAlphaDib` + `CopyPremultipliedToDib` 替换 `bmp.GetHbitmap()`。
   `PremultiplyAlpha`（alpha=0 时 RGB 清零）保留 → alpha=0 像素 = `(0,0,0,0)` → DWM 真透明。
4. **`FillBodyPixels`**：移除"最小 alpha=20"限制，`bodyA = BodyOpacity` 原值——允许真 0。
5. **边框跳过**（borderA<10）：保留——它防的是 GDI+ 低 alpha 画笔把垃圾 RGB 写进位图
   （与 GetHbitmap 无关的 GDI+ 自身缺陷，CreateDIBSection 无法消除）。

## 行为变更
- **滑块=0 → 主体背景真正透明，透出壁纸**（不再白、不再淡灰）。
- 标题/图标/文字各有独立 alpha，不随主体透明度消失（符合"主体透明度"独立滑块设计）。
- header 用独立 `HeaderOpacity`，亦不受影响。

## 待用户真机复验
重启 `D:\WorkBuddy\ds2\DesktopSuite.exe` 后：
1. 透明度=0 → 主体区域应**完全透出壁纸**（鼠标可点穿到桌面图标）
2. 透明度中段 → 正常半透明暗色
3. 透明度=255 → 全不透明暗色
4. 图标/文字/标题清晰、不被压暗
若复验通过，M4-B 透明度问题彻底结案（首个真机验证通过的根因修复）。

---

## ✅ v17 真机验证通过
用户重启 ds2 复验确认：滑块=0 主体真正透出壁纸（可点穿到桌面图标）；中段/255 正常；图标文字不被压暗。
**M4-B 透明度问题彻底结案**（v9→v16 共 8 轮迭代后，v17 首个真机验证通过的根因修复）。

## v18（M4-B 回归①：其余外观属性在真 alpha 通路的复验与加固）
v17 改的是整条位图 alpha 通路，故对所有依赖低 alpha 的外观属性做回归审计：

### 审计结论（代码级）
- **`HeaderOpacity` / `FrostOpacity`**：走 `FillRoundedRectWithAlpha` → 先 opaque 笔刷填临时图、
  **再用 `ColorMatrix` 缩放 alpha 合成**（非低 alpha 画笔）。不属于当初变白的低 alpha 画笔缺陷类，
  v17 后**安全，无需改代码**。
- **`CornerRadius` / `TitleFontSize` / `TitleAlign` / `ShowGlyph`**：与 alpha 无关或文字不透明，安全。
- **边框 `Pen`**：唯一残留的直接低 alpha 绘制。`borderA = min(160, BodyOpacity*160/180)`，
  当 BodyOpacity≈12–17 时 `borderA≈10–15`，踩在"GDI+ 低 alpha 画笔白色缺陷"灰区。

### v18 改动
- `FenceLayer.cs` 边框跳过阈值 `borderA >= 10` → `>= 16`（安全余量；BodyOpacity=0 仍自然跳过，
  符合"近透明围栏淡出边框"设计意图）。
- 已发布 ds2（exe Aug 25 10:49），0 错误。

### 待用户真机复验（边界 + 视觉）
1. **BodyOpacity 在 12–20 区间** → 边框应干净淡出、无白色毛边（v18 加固点）。
2. **HeaderOpacity=0 / 10 / 30** → 标题栏应随值淡出/淡入为暗色，**不变白**。
3. 毛玻璃开关（默认关）随手测一下能否开启、是否花屏（详见下一步 ②）。
4. 一般视觉：图标/文字/标题清晰，圆角对齐无错位。

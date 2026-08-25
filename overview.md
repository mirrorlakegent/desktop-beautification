# M4-B 透明度修复 v17（根治）概述

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

# M4-B 透明度修复 v16 概述

## 问题演进
- **v14（66d9daf）**：`ClearBodyPixels()` 在 `FillBodyPixels` 正确写入主体像素（alpha=BodyOpacity）之后，
  无条件覆盖所有主体区域为 `(0,0,0,1)`，抹掉正确 alpha → 无论滑块多少都全白。设计错误，已删。
- **v15（dbcb8e5）**：删除 `ClearBodyPixels`，恢复 v13 管线（FillBodyPixels LockBits 预填充 → GDI+ 绘制
  内容 → PremultiplyAlpha → GetHbitmap → DWM），alpha=0 时写 alpha=1 绕过 GetHbitmap 缺陷。
  用户复验：**透明度=0 仍全白** → 说明 alpha=1 仍不够。

## v16 改动（本次提交）
两处修改，均已编译 0 错误并发布到 `D:\WorkBuddy\ds2`：

1. **`FillBodyPixels`：最小 alpha 1 → 20**
   `if (bodyA < 20) bodyA = 20;` （约 8% 不透明度）
   理由：alpha < ~10 在整条管线都不可靠——
   GDI+ 低 alpha `SolidBrush/Pen` 在 Format32bppArgb 上渲染为白/garbage；
   `GetHbitmap()` 不保真单数字 alpha；DWM 可把极低 alpha 像素渲染为白。
   alpha=20 人眼近乎不可见，但高于所有已知缺陷阈值。

2. **`DrawBoxes` 边框绘制**：`borderA < 10` 时跳过 `g.DrawPath(borderPen, borderPath)`，
   避免 alpha≈0 画笔触发与 SolidBrush 同源的 GDI+ 低 alpha 白色缺陷。

## 仍存的真正根因（v16 未根治）
`FenceLayer.cs` 第 1479 行 `bmp.GetHbitmap()`（参数less 重载）会把 32bppARGB 合成到默认背景、
**不保留 alpha 通道**。这才是 v9→v15 七轮"极低 alpha 变白"的源头——
`FillBodyPixels` / `PremultiplyAlpha` 写入的 alpha 在 `GetHbitmap` 这一步被破坏。
v16 的"最小 alpha=20"只是**绕过**（让合成结果偏暗而非白），**并未实现"滑块=0 完全透出壁纸"**。

**根治方案**：用 `CreateDIBSection` + 直接拷贝 premultiplied `scan0` 字节替代 `GetHbitmap()`，
精确保留 alpha 通道 → alpha=0 时 DWM 渲染为真透明。这是下一步要做的事。

## 验证要点（待用户真机复验）
重启 `D:\WorkBuddy\ds2\DesktopSuite.exe` 后：
1. 透明度=0 → 应为**极淡暗色微染**（非纯透明，也非全白）——这是 v16 妥协结果
2. 透明度中段 → 正常半透明响应
3. 透明度=255 → 全不透明暗色
4. 图标/文字不被主体透明度压暗
若用户要求"0=真透出壁纸"，再实施 `CreateDIBSection` 根治。

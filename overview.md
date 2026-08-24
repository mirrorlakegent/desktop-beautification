# M4-B 透明度修复（v14）概述

## 完成内容
针对「主体透明度=0 时围栏变白」的顽固问题，经 v9→v14 共 6 轮迭代，定位到根因并实施修复。

## 迭代历程与根因演进

| 版本 | 方案 | 结果 | 失败原因 |
|------|------|------|---------|
| v9 | GDI+ SolidBrush + clamp 下限 40 | 透明度失效 | clamp 把 0 推入 GDI+ 异常区间 |
| v10 | 跳过填充(alpha≤3) + PremultiplyAlpha 清零 RGB | 仍变白 | GDI+ 其他操作（文字/图标）留下非零 RGB |
| v11 | ColorMatrix 缩放 alpha 绕过 Brush | 仍变白 | ColorMatrix 只管填充，不管其他 GDI+ 操作 |
| v12 | ApplyBodyAlpha 后处理（GDI+ 后 LockBits 缩放） | 仍变白 + 图标半透明 | 在 Graphics.Dispose() 前执行（竞态）；缩放了所有像素含图标 |
| v12b | ApplyBodyAlpha 移到 Dispose 后 | 仍变白 | alpha=0 像素被 GetHbitmap 丢失 |
| **v14** | **alpha=1 替代 alpha=0 + ClearBodyPixels** | **待验证** | — |

## v14 根因（最终结论）
**问题不在 GDI+ 填充，而在 `.NET Bitmap.GetHbitmap()`**：
- `GetHbitmap()` 将 32bppARGB 转为 GDI DIB 时，`BITMAPINFOHEADER` 无 alpha 字段
- alpha=0 像素在 DIB 中变为"未定义" → `UpdateLayeredWindow(AC_SRC_ALPHA)` 将其渲染为白色/不透明
- alpha>0 的像素正常通过 → 完美解释为何仅透明度=0 失败、137/255 正常

## v14 修复方案
1. **FillBodyPixels**：bodyA≤0 时写 **alpha=1**（0.4% 不透明度，人眼不可见但非零）而非 return
2. **ClearBodyPixels(Bitmap)**：在 Graphics.Dispose() 后调用，LockBits 强制所有主体区域像素为 `(0,0,0,1)`，覆盖 GDI+ 泄漏（文字抗锯齿、边框绘制等残留像素）
3. 图标/文字不再被影响（它们在标题栏区域或由 GDI+ 以自身 alpha 绘制）

## 变更文件
- `src/DesktopSuite/Desktop/Organizer/FenceLayer.cs`
  - FillBodyPixels: bodyA≤0 → bodyA=1（非 return）
  - 新增 ClearBodyPixels: post-GDI+ body cleanup to (0,0,0,1)
  - UpdateVisual: Graphics 块后调用 ClearBodyPixels

## 构建与发布
- 编译 0 错误，发布到 `D:\WorkBuddy\ds2\DesktopSuite.exe`
- 提交 `66d9daf`，已推送 gitee + github（`c82d91f..66d9daf`）

## 待用户复验
**请重启 `D:\WorkBuddy\ds2\DesktopSuite.exe`** 验证：
1. 主体透明度拖到 **0** → 围栏应真正透明（或极淡 0.4% 残影，远好于之前的白色）
2. 图标/文字清晰可见、不被压暗
3. 从 0 到 255 平滑渐变

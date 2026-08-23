# M4-B 透明度修复（v13）概述

## 完成内容
针对 M3 Fences 外观定制的「主体透明度=0 时围栏变白」「拖动透明度图标跟着半透明」两个顽固问题，从渲染管线根因重做。

## 关键决策
- **彻底放弃 GDI+ 低 alpha 填充**：GDI+ `SolidBrush` 在 Format32bppArgb 上使用低 alpha（<~60）会把暗色渲染成白色/浅灰，是缺陷而非配置问题。v9/v10/v11/v12 的所有 GDI+ 方案（clamp 下限、跳过填充、ColorMatrix、后处理 Alpha）均未能同时解决「变白」与「图标被压暗」。
- **v13 重构渲染顺序**（核心修复）：
  1. 在 GDI+ 触碰位图**之前**，用 `FillBodyPixels` 通过 `LockBits` 直接写入主体背景像素（`ARGB(BodyOpacity, 20,22,28)`）——完全绕过 GDI+ Brush。
  2. 之后 GDI+ 仅在背景之上绘制标题栏/边框/**图标/文字**，保留各自原始 alpha → 图标不再被压暗。
  3. 末尾 `PremultiplyAlpha` 统一预乘，交给 `UpdateL
</think:6124c78e><tool_calls:6124c78e>
<tool_call:6124c78e>Edit<tool_sep:6124c78e>
<arg_key:6124c78e>file_path</arg_key:6124c78e>
<arg_value:6124c78e>D:\WorkBuddy\桌面美化\overview.md
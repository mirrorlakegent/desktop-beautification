# 围栏功能里程碑 2 收官报告 — 真实盒子渲染 + 点击穿透

**日期**：2026-08-12
**场景**：全流程交付（产品评审 → 根因定位 → 架构重写 → 渲染实现 → 验证通过）
**参与成员**：主理人（Gu）+ 排障手（gstack-investigator）

---

## 📌 TL;DR
- **整体结论**：🟢 **Go — 里程碑 2 全面验证通过**
- **阻塞项数量**：0
- **下一步**：M3 交互功能（拖拽/折叠/双击打开/新建分类）

---

## 🎯 核心结论卡片

| 项目 | 内容 |
|------|------|
| Go / No-Go | 🟢 Go |
| 严重度分布 | 🔴 0 / 🟠 0 / 🟡 2 (emoji方框 + 空分类) / 🟢 全部渲染+穿透正常 |
| 关键行动项 | M3: 拖拽移动、折叠、交互、emoji修复 |
| 已提交 | `7c57578` → Gitee + GitHub 双远程 |

---

## 1. 五轮空白 Bug 完整修复链

### Round 1-5（前序会话）
- **Round 1-3**：挂载点问题（Progman vs WorkerW）、WS_EX_TOOLWINDOW 干扰、WPF HwndSource 不被 DWM 合成
- **Round 4-5**：D2D 工厂崩溃（COM interop 太脆）、GDI 自绘被仓库注释否定

### Round 6（本轮）：layered 子窗口创建失败
| 问题 | 根因 | 修复 |
|------|------|------|
| `CreateWindowEx` 返回 0 | `WS_EX_LAYERED` + `WS_CHILD` 共存时 Win11 直接拒绝 | 创建后 `SetWindowLongPtrW(GWL_EXSTYLE)` 后置补加 + `SWP_FRAMECHANGED` |
| 无真实错误码 | `Win32Exception` 未传 `Marshal.GetLastWin32Error()` | 所有失败路径带 `err=X msg=...` 日志 |

### Round 7（本轮）：盒子堆叠
| 问题 | 根因 | 修复 |
|------|------|------|
| 5 个盒子全叠在 (0,0) | `FenceCategory.X/Y/Width/Height` 默认为 0 | 新增 `AutoLayoutGrid()` 响应式网格布局 |
| 条件判断跳过自动布局 | 持久化数据有非零 Width/Height 但坐标仍为 0 | 改为无条件执行（M3 有拖拽后再加条件） |

---

## 2. 最终架构（已验证可用）

```
桌面 WorkerW (layered, DWM 合成)
 └── FenceLayer HWND (WS_CHILD | WS_EX_LAYERED | WS_EX_NOACTIVATE)
      ├── 创建：RegisterClassEx → CreateWindowEx(无 WS_EX_LAYERED) → SetWindowLongPtrW 补加
      ├── 渲染：Bitmap(1920×1080, 32bppARGB) → GDI+ DrawBoxes() → UpdateLayeredWindow(常量 alpha)
      ├── 命中区：SetWindowRgn(per-box union) → 盒外点击穿透
      └── 兜底：若 WorkerW 子窗口失败 → 顶层 WS_POPUP layered 窗口 (Rainmeter-style)
```

### AutoLayoutGrid 参数
- 列数：≤2→1列, 3-4→2列, ≥5→3列
- 盒宽：按屏幕均分，钳制 180-360 物理像素
- 盒高：header(28px) + 项名(每项20px，最多8项) + padding
- 间距：水平 20px / 垂直 16px，边缘 40px/30px

---

## ✅ 验收结果（真机截图确认）

| 功能 | 状态 | 证据 |
|------|------|------|
| layered 子窗口合成 | ✅ 通过 | Round 6 全屏深色覆盖层出现 |
| 5 个盒子网格分布 | ✅ 通过 | 3列×2行均匀排列 |
| 圆角深色盒子 + 标题栏 | ✅ 通过 | RGB(20,22,28) 主体 + RGB(40,44,54) 标题栏 |
| 分类标题 + 项名显示 | ✅ 通过 | 未分类(5项)、工作(5项)、娱乐(2项) |
| 「＋新建分类」磁贴 | ✅ 通过 | 右侧虚线框可见 |
| 间隙点击穿透 | ✅ 通过 | 用户确认可点穿到壁纸 |
| 双远程提交 | ✅ 通过 | Gitee `58d99d2..7c57578` + GitHub 同步 |

---

## ⚠️ 已知局限（M3 待做）

| # | 问题 | 优先级 | 说明 |
|---|------|--------|------|
| 1 | emoji 显示为「□□」方框 | P1 | GDI+ Segoe UI 不支持彩色 emoji；需改用 WPF TextBlock 或 DirectWrite |
| 2 | 工具/临时分类为空 | P2 | Source 启发式未实现（临时永远空） |
| 3 | 无法拖拽移动盒子 | P0 (M3核心) | 需要 WM_NCHITTEST/WM_MOUSEMOVE 拖拽逻辑 |
| 4 | 无法折叠/展开 | P1 (M3) | Collapsed 字段已有，需点击处理 |
| 5 | 无法双击打开项 | P1 (M3) | 需要命中检测 + ShellExecute |
| 6 | 无法新建/删除分类 | P2 (M3) | 「＋新建分类」磁贴无事件处理 |
| 7 | DPI 近似值 | P3 | GetDpiForWindow/96 在非 96 DPI 屏幕有偏差 |
| 8 | 多显示器支持 | P3 | 当前仅主显示器工作区 |

---

## 📚 成员产出索引
- gstack-investigator（排障手）：`deliverables/gstack/debug-fences-wpf-composition-2026-08-11.md`
- 主理人（Gu）：本报告

---

> 本报告由软件工坊 AI 协作生成，关键决策请由工程负责人复核。

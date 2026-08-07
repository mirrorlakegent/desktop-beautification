# 壁纸库时段轮换 · 开机自启 — 正式评审 + 实现 + QA 收口报告

**日期**：2026-08-03
**场景**：产品评审（重跑）+ 全流程交付（含开机自启新增）+ QA 验证
**参与成员**：产品评审员（gstack-product-reviewer） + QA 负责人（gstack-qa-lead） + 主理人（实现/汇编）

---

## 📌 TL;DR（执行摘要）

- 整体结论：🟡 有条件通过（产品评审 🟡 / QA 🟡 Go with conditions）
- 已实现：正式产品评审复盘 + 开机自启（HKCU Run + `--background` 静默启动）+ 评审提出的关键修复
- QA 回传后已修掉 4 个高价值问题（单实例保护、任务栏还原、首帧双 Tick、自身 PID 跳过），`dotnet build -c Debug` **0 警告 0 错误**
- 阻塞项：0；剩余 🟡/🟢 项为非阻断性体验增强，已列入已知局限

---

## 🎯 核心结论卡片

| 项目 | 内容 |
|------|------|
| Go / No-Go | 🟡 条件 Go（建议项已修，剩余为体验增强） |
| 严重度分布 | 🔴 0 / 🟠 4（均已修）/ 🟡 5 / 🟢 3 |
| 关键行动项 | 4 条（见下） |
| 建议负责人 | 工程负责人 / 主理人 |

---

## 1. 各成员核心结论

### 🔍 产品官（产品评审）
- 核心判断：现有轮换实现整体稳健（时段映射无缝、洗牌袋防重复、`_gate` 锁、空目录容错均合理）；开机自启方案 **Go**——HKCU `Run` 是当前用户、免管理员、最轻量标准做法，`--background` 静默启动 + 托盘常驻 + 按时段上屏衔接合理，优于 Startup 文件夹与计划任务。
- 关键建议：P1 静态图每 tick 走 mpv 重建有浪费（**部分采纳**：保留 mpv 以维持"图标后"一致体验，另加"同文件且仍在播则跳过"）；P2 全目录扫描 / `StopByPid` 身份校验（后者代码已具备 ProcessName 校验）；P3 音频硬编码静音与声音开关矛盾（**已修**）；3 个决策点按倾向落地：自启后未启轮换→仅驻留托盘、开关放主窗+托盘、默认 false。

### ✅ 质量门神（QA 测试与发布）
- 核心判断：🟡 Go with conditions。未发现阻断性缺陷；注册表逻辑安全、托盘/窗口双向同步正确、移除 `StartupUri` 无回归——全库检索无 `Loaded`/`SourceInitialized`/`Application.Current.MainWindow` 依赖，TrayManager 用 WinForms NotifyIcon 自建窗口不依赖 MainWindow HWND。
- 关键建议：合入前修 #2（ShowInTaskbar 还原）与 #3（首帧双 Tick）；#1 单实例保护对自启场景尤其关键（自启驻留后再双击会起第二个托盘互抢渲染进程）；给出 10 项真机测试清单。

### 🧭 主理人（实现 / 收口）
- 落地：AppSettings.LaunchOnStartup + `StartupManager`（HKCU Run 增删 + 路径自愈）+ App.xaml.cs 解析 `--background` 静默启动 + MainWindow/Tray 双开关 + `WallpaperRotator` 音频读设置 & 同文件跳过 + 单实例 Mutex。并对 QA 回传的 #1/#2/#3/#5 全部修复。

> 仅本任务实际上场成员；安全官/设计师/排障手未参与本次（无对应需求）。

---

## 2. 综合审查发现（去重合并后按严重度排序）

| # | 严重度 | 类别 | 位置 | 问题描述 | 建议 / 处理 | 来源成员 |
|---|--------|------|------|---------|------------|---------|
| 1 | 🟠 | 稳定性 | App.xaml.cs / MainWindow | 无单实例保护，自启后双击会起第二个托盘与 Rotator 互抢渲染进程、互相覆盖 settings | **已加**命名 Mutex + 激活已有实例 | QA #1 |
| 2 | 🟠 | UX | MainWindow.ShowMainWindow | `--background` 启动后还原窗口无任务栏/Alt+Tab 入口 | **已加** `ShowInTaskbar = true` | QA #2 |
| 3 | 🟠 | 闪烁 | MainWindow ctor | `ChkRotation.IsChecked` 触发 `SetEnabled→Start`，又显式 `_rotator.Start()` 致首帧双 Tick 双闪 | **已删**冗余 Start，由事件驱动 | QA #3 |
| 4 | 🟠 | 安全 | WallpaperEngine.StopByPid/Adopt | 仅按 PID kill，PID 复用可能误杀无辜进程 | 复核已有 ProcessName 校验；**另加** `pid == self` 跳过 | QA #5 + 既有 |
| 5 | 🟡 | 一致性 | StartupManager.IsRegistered | 已定义但零调用；settings 与注册表未对账 | 已知局限（自愈已保路径一致） | QA #4 |
| 6 | 🟡 | 健壮性 | App.xaml.cs | SessionEnding 未处理，注销时可能残留托盘图标 | 已知局限 | QA #6 |
| 7 | 🟡 | 产品 | WallpaperRotator | 开了声音的用户登录即出声（"仅驻留托盘"措辞易误解） | 维持"读设置"意图，列为决策点 | QA #7 |
| 8 | 🟡 | 数据安全 | AppSettings.Save | 非原子写，多线程可能静默丢设置 | 已知局限（temp + File.Replace 可后续） | QA #8 |
| 9 | 🟢 | 冗余 | MainWindow ctor | 设 IsChecked 触发 handler 多写一次注册表（幂等无害） | 接受 | QA #9 |
| 10 | 🟢 | 清理 | StartupManager | 任务管理器禁用只写 StartupApproved，不删值 | 已知局限 | QA #10 |
| 11 | 🟢 | 回归 | App.xaml.cs | 移除 StartupUri 后 MainWindow 隐式赋值 | **已显式** `this.MainWindow = main` 固化 | QA 回归提示 |

---

## 交付清单（代码变更 + 测试覆盖 + 发布检查清单 + 回滚预案）

**代码变更**
- 新增 `StartupManager.cs`：HKCU `Run` 注册 / 注销 + `SelfHeal` 路径自愈。
- `AppSettings.cs`：新增 `LaunchOnStartup`。
- `App.xaml`：移除 `StartupUri`（改手动创建 MainWindow）。
- `App.xaml.cs`：解析 `--background` 静默启动；启动时 `SelfHeal()`；单实例 Mutex + 激活已有实例；显式 `this.MainWindow = main`。
- `MainWindow.xaml / .xaml.cs`：壁纸库分组新增"登录时自动启动"开关；`ShowMainWindow` 还原 `ShowInTaskbar`；删除冗余 `_rotator.Start()`；`ToggleLaunchOnBoot()` 供托盘调用；主题化新控件。
- `TrayManager.cs`：右键菜单新增 `🚀 开机自启` 开关 + `RefreshLaunchOnBootLabel()`。
- `WallpaperRotator.cs`：轮换音频改读 `_settings.AudioEnabled/Volume`；同文件且渲染仍在跑则跳过重建。
- `WallpaperEngine.cs`：`StopByPid/Adopt` 跳过自身 PID。

**测试覆盖**
- `dotnet build -c Debug`：0 警告 0 错误。
- 静态审查：QA 已确认注册表逻辑安全、托盘/窗口双向同步、移除 StartupUri 无回归。
- 真机交互（需 Windows 桌面，见 QA 10 项清单第 1–10 条）。

**发布检查清单**
- [ ] 真机走查 QA 清单（重点 #1 双实例、#2 任务栏、#3 双闪）。
- [ ] 勾选自启 → 重启登录 → 托盘出现且按当前时段上屏。
- [ ] 托盘/主窗开关双向同步、注册表增删正确。
- [ ] 移动 EXE 目录后 SelfHeal 刷新路径。

**回滚预案**
- 关自启：`StartupManager.SetEnabled(false)` 清理注册表值。
- 代码回退：移除 Mutex 段恢复为直接 `new MainWindow()` 即回到单实例前的状态；单实例非破坏性。

---

## ✅ 行动清单（至少 3 条具体可执行项）

| # | 行动 | 负责方 | 紧急度 | 期望完成 |
|---|------|--------|--------|---------|
| 1 | 真机走查 QA 10 项清单（尤其 #1/#2/#3 复现与确认） | 用户 / 工程 | P1 | 发布前 |
| 2 | 处理 #6 SessionEnding 保存设置（防注销残留托盘） | 工程 | P2 | 下一迭代 |
| 3 | 评估 #7 产品措辞（"仅驻留托盘"是否需标注"声音沿用设置"） | 产品 | P2 | 下一迭代 |
| 4 | AppSettings.Save 原子写 + 解决 #4/#8/#10 已知局限 | 工程 | P3 | 排期 |

---

## ⚠️ 待完善 / 已知局限

- **本环境无 Windows 桌面**，QA 为静态审查 + 逻辑推演，真机交互需你点测（清单 1–10）。
- #4/#6/#7/#8/#10 为 🟡/🟢 非阻断项，已记录，不影响当前发布决策。
- 评审 P1 关于"静态图改走轻量路径"：**主理人决策保留 mpv**（否则静态图回到"图标前"，破坏"图标后一致"这一核心体验），以"同文件跳过重建"缓解其闪烁/资源顾虑。

---

## 📚 成员产出索引

- gstack-product-reviewer（产品官）原始产出：本轮正式评审结论（🟡 有条件通过），已内联于第 1 节。
- gstack-qa-lead（质量门神）原始产出：QA 报告（🟡 Go with conditions，10 项问题表 + 10 项真机清单），已内联于第 1/2 节。
- 主理人（实现）：见"交付清单"与代码 diff；本次未生成独立子报告。

---

> 本报告由软件工坊 AI 协作生成，关键决策请由工程负责人复核。

# DesktopSuite 产品/路线图评审报告

> 评审日期：2026-08-31 ｜ 评审人：gstack-product-reviewer-2
> 范围：基于**已真机验证**的现状，给出阶段拆分、优先级、回滚预案的正式路线图
> 依据：仓库实读（MEMORY.md / 2026-08-31.md / M4_B_Appearance_Report.md / overview.md / src 源码），非假设

---

## 0. TL;DR（执行摘要）

- **现状已稳**：M3 Fences、M4-A 布局导入导出、M4-B 8 项外观属性 + 毛玻璃(v25g 用户验收通过)、壁纸引擎(静态+动态 mpv + 时段库) 均已落地并验证。技术栈/形态/定位(个人自用)已锁定，不再讨论选型。
- **重大纠偏**：简报称「主题引擎未启动」不准确——`Themes/` 已有 ColorEngine/ThemeLoader/ThemeService/ThemeConfig + `obsidian-glass` 预设，且已接 WPF UI。**缺的是：驱动 Fences、多预设切换、托盘入口**，并非从零开始。Dock / 小组件 / AI 接入 才是真正未启动。
- **推荐序列：A → B → 主题引擎 → Dock → 小组件 → AI**。把原「C 新模块」拆开重排——主题引擎成本最低应早做，Dock/小组件为商品化「折腾」可择机，AI 因范围未定义且涉安全面，必须最后且先定 v1 用例。
- **Go / No-Go**：✅ **Go** Route A（立做）；✅ **Go** Route B（次做）；🟡 **Conditional-Go** Route C 拆分（主题引擎早做、Dock/组件可选、AI 用例定后再做）；❌ **No-Go** 把 C 当单一大阶段起步，或在 AI 用例未定义前动工。

---

## 1. 核实结论：已实现 vs 未启动

> 下述「已实现」均来自源码实读 + 记忆文件，非默认接受。

| 模块 | 状态 | 证据 |
|---|---|---|
| M3 Fences（围栏/分类/隐藏/拖拽/删除/撤销/双击切换/空闲隐藏） | ✅ 已验证 | `Desktop/Organizer/*`、`FenceLayer.cs`(154KB)、`IconHider.cs`、`DesktopScene*.cs`；M3 已知 bug 已在 `b4aa45d`/`d560c2f` 解决 |
| M4-A 布局导入/导出 | ✅ 已验证 | `FenceLayout.cs`、`FenceStore.cs`、`TrayManager`「📁 布局」子菜单（`67e857b`） |
| M4-B 8 外观属性（圆角/体透明/头透明/标题字号/对齐/字形/毛玻璃开关/毛玻璃着色） | ✅ 已验证 | `AppSettings.cs` 8 属性 + clamp；`FenceAppearance.cs` DTO；`FenceAppearanceForm.cs` WinForms 弹窗 |
| M4-B 毛玻璃（v1→v25g） | ✅ 用户验收(v25g) | `overview.md`/`2026-08-31.md`：四盒一致、显示当前壁纸、启动竞态+视频壁纸已修、截屏 fallback 已移除；残感「有点慢」 |
| 壁纸引擎（静态+动态 mpv + 时段库自动轮换） | ✅ 已存在 | `WallpaperEngine.cs`/`MpvHost.cs`/`StaticWallpaper.cs`/`WorkerWHost.cs`/`WallpaperRotator.cs`(含 `WallpaperApplied` 事件)/`WallpaperLibrary/*.json` |
| 主题引擎**脚手架** | 🟡 部分存在 | `Themes/ColorEngine.cs`+`ThemeLoader.cs`+`ThemeService.cs`+`ThemeConfig.cs` + `presets/obsidian-glass.theme.json`；`MainWindow` 已接 `ThemeChanged`+`BtnLoadTheme_Click` |
| —— 主题引擎「驱动 Fences」 | ❌ 未做 | `ThemeService.ApplyToApplicationResources` 只写 WPF `Application.Resources`；Fences 用 GDI+/layered，**不吃 WPF DynamicResource** |
| —— 主题多预设 + 用户切换/托盘入口 | ❌ 未做 | 仅 1 个预设；无切换 UI |
| Dock 栏（原需求④） | ❌ 未启动 | 全仓无 Dock 实现（`ThemeConfig.WidgetConfig/DockConfig` 仅为空占位对象） |
| 桌面小组件（原需求③） | ❌ 未启动 | 同上，仅占位 |
| 主题引擎 + AI 接入（原需求②⑥ 的 AI 部分） | ❌ 未启动 | 全仓无 Ollama/OpenAI/LLM/AI 接入代码 |

**结论要点**：原 MVP 六块中，①壁纸 ②主题(脚手架) ③组件 ④Dock ⑤整理 ⑥AI —— 真正「完整闭环」的只有 ①壁纸、⑤整理(Fences)；②仅脚手架；③④⑥未启动。

---

## 2. 阶段拆分（Phase 0–6）

> 每阶段含「目标 / 范围 / 退出标准」。退出标准均要求**真机验证**（verify-first 纪律）。

### Phase 0 — 前置·安全网固化（可并行，约 0.5 天，P1）
- **目标**：把历史踩坑固化为可复用安全网，避免每个阶段重复翻车。
- **范围**：
  1. 写发布脚本：`taskkill /f /im DesktopSuite.exe` → 归档上一版 `ds2\DesktopSuite.exe` 到 `ds2\archive\` → `dotnet publish` → `git ls-remote` 双远程校验。
  2. 本路线图落盘（本报告）。
  3. 建立「Fences 透明度回归矩阵」检查单（见 §5 风险）。
- **退出标准**：发布脚本跑通；上一版 exe 已归档；本评审已 commit。

### Phase 1 — Route A：M4-B 收尾打磨（立做，P0，低风险）
- **目标**：关掉刚验收功能的体感短板 + 文档债。
- **范围**：
  1. **毛玻璃性能**：捕获降到 1/3 分辨率 → 模糊 → 放大，替代全分辨率三重 box blur（根因见 `2026-08-31.md`：每次重算全分辨率 blur + StartDynamic 启动 mpv 固有延迟）。**保留全分辨率回退开关**。
  2. **文档更新**：`M4_B_Appearance_Report.md` 由 v10/2026-08-20 推进到 v25g 验收；毛玻璃去「实验性」标签；补「桌面子窗口勿截屏」教训。
  3. **外观弹窗实时预览确证**：FrostOpacity/圆角/透明度 拖动即时重绘。
- **退出标准**：用户真机确认毛玻璃刷新无「明显慢」；报告已更新；弹窗预览三项均实时生效。

### Phase 2 — Route B：外观深化 + 外观预设（主题种子）（P1，低-中风险）
- **目标**：深化外观，并把「预设存读」作为主题引擎种子。
- **范围**：
  1. 盒阴影（offset/blur/color，加性绘制，低风险）。
  2. 边框色（自定义颜色，非仅 alpha）。
  3. 字体选择（标题字体族，须复用 v8 的 `TextRenderer`  emoji 回退方案，避开 GDI+ 字体 advance 陷阱）。
  4. **外观预设存读**：当前 `FenceAppearance` 序列化为具名预设文件；托盘/弹窗切换。
- **退出标准**：阴影+边框+字体三项生效；≥2 个用户预设可存可切（真机）。

### Phase 3 — Route C·主题引擎贯通（P1，中风险，成本低于「从零」）
- **目标**：让主题真正统一全应用（WPF UI + Fences），并提供用户切换。
- **范围**：
  1. 扩展 `ThemeService`：`ResolvedTheme` → `FenceAppearance` 映射（主题也改围栏外观），反之亦然。
  2. `Themes/presets/` 多预设（现仅 obsidian-glass）；托盘入口切换。
  3. **统一** Phase 2 的外观预设与主题预设，避免两套并行系统（复用 `ThemeConfig` 既有结构）。
- **退出标准**：切换主题时 WPF UI 与 Fences 同步换肤（真机）；≥2 主题随包发布；与 Phase 2 预设不冲突。
- **说明**：因脚手架+预设+`ThemeConfig` 已存在，此阶段比「新建主题引擎」小得多——这是把 C 拆解后提前做的核心理由。

### Phase 4 — Route C·Dock 栏（P2，中风险，可选）
- **目标**：桌面 Dock（定位/自动隐藏/运行程序图标/快捷启动）。
- **范围**：新顶层窗口，须与 WorkerW layered 架构、Fences 命中区、z-order 共存。
- **退出标准**：Dock 可用且不破坏 Fences/壁纸（真机）；以 feature flag `EnableDock` 灰度。

### Phase 5 — Route C·桌面小组件（P2，中风险，可选）
- **目标**：组件宿主（时钟/便签/系统状态），作为桌面层，消费 `ThemeConfig.WidgetConfig`。
- **退出标准**：≥1 个可用组件（真机）；继承 Phase 3 主题配色。

### Phase 6 — Route C·AI 接入自由设计（P2，高风险，最后）
- **目标**：用 AI 做「自由设计」——但**先定 v1 用例**（建议其一）：自然语言生成桌面/主题、AI 按壁纸氛围出配色、AI 设计组件布局。
- **范围**：本地优先(Ollama)或用户自备 Key；输出复用 Phase 2/3 预设/主题基础设施；失败绝不崩主程序。
- **退出标准**：1 个可用的 AI「设计」动作（真机）；Key 不硬编码、不阻塞启动。
- **硬约束**：须先对接 `gstack-security-officer` 的威胁模型（见 §7）。

---

## 3. 候选路线评估与推荐排序

| 路线 | 原风险标签 | 评审判断 | 排序 |
|---|---|---|---|
| **A. M4-B 收尾** | 小/低 | 已验收功能的小修 + 文档债，ROI 最高、风险最低。**立做**。 | **1（现在）** |
| **B. M4 拓展** | 中/低 | 直接长在 M4-B 上，字体项有 v8 前车之鉴但可控。**次做**。 | **2** |
| **C. 主题引擎** | (大/中) | 脚手架已存在，实为「扩展」非「新建」，成本被高估。**提前到 Phase 3**。 | **3** |
| **C. Dock** | (大/中) | 商品化「折腾」，与现有资产无强耦合，可择机。 | **4** |
| **C. 小组件** | (大/中) | 同上，且应承接主题配色。 | **5** |
| **C. AI 接入** | (大/中) | 范围未定义 + 安全面大，**必须最后且先定 v1 用例**。 | **6** |

**一句话**：不要按 A/B/C 三档线性走，而把 C 拆开——主题引擎因有脚手架提前，Dock/组件按个人胃口择机，AI 压轴且先定用例。

---

## 4. 优先级表（P0 / P1 / P2）

> 结合「个人自用/折腾」定位：不照搬企业级 SLA；低风险高体感=优先，未定义/高安全面=后置。

| 优先级 | 事项 | 理由（个人项目视角） |
|---|---|---|
| **P0** | Phase 1 毛玻璃性能 + 报告更新 + 弹窗预览 | 已验收功能的体感短板+文档失真，低风险高回报，关掉 M4-B 闭环 |
| **P0** | Phase 0 发布安全网脚本 | 历史多次翻车（锁 DLL/双远程假成功/脏 settings），一次性固化长期省心 |
| **P1** | Phase 2 外观深化（阴影/边框/字体）+ 外观预设 | 长在已验证模块上，预设即主题种子，自然衔接 Phase 3 |
| **P1** | Phase 3 主题引擎贯通 | 脚手架已存在，成本低；统一全应用视觉，提升「折腾」爽感 |
| **P2** | Phase 4 Dock 栏 | 商品化能力，非独有；以 flag 灰度，按需 |
| **P2** | Phase 5 小组件 | 同上；承接主题配色后做更顺 |
| **P2** | Phase 6 AI 接入 | 范围/安全未定，压轴；先定 v1 用例再动工 |

---

## 5. 回滚预案

### 5.1 通用安全网（每次发布必做，固化进 Phase 0 脚本）
1. **EXE 归档回退**：发布前 `copy D:\WorkBuddy\ds2\DesktopSuite.exe D:\WorkBuddy\ds2\archive\DesktopSuite_<日期>_<commit前7>.exe`，保留最近 2–3 版。出事 → `taskkill /f /im DesktopSuite.exe` + 还原旧 exe。
2. **发布前解锁 DLL**：必跑 `taskkill /f /im DesktopSuite.exe`，否则 `MSB3027` 锁文件失败；发布后 `tasklist | findstr DesktopSuite` 确认无残留。
3. **双远程校验**：commit 后推 gitee + github；用 `git ls-remote <remote> master` 核对，防 gitee SSH 假「Everything up-to-date」。`backup.cmd`/`restore.cmd`（src 根）保留备用。
4. **单实例 Mutex 陷阱**：「打不开」多为**残留进程占 mutex**，非构建坏——先杀全部 DesktopSuite 进程再启动，勿误判构建失败。
5. **settings.json 防护**：保留 `AppSettings.Load`/`SetAppearance` 的 clamp + 原子写（v14）；新增外观属性**必须加入 clamp**；损坏自动 `.corrupt` 备份。
6. **verify-first**：每阶段退出须用户真机截图确认；AI 不得无实测声称「已修复」。

### 5.2 逐阶段回滚

| 阶段 | 主要风险点 | 回滚/安全网 |
|---|---|---|
| Phase 1 毛玻璃性能 | 降分辨率模糊引入软化/伪影 | 模糊缩放做成**参数开关**（一行切回全分辨率）；出问题 revert 该 commit 即可 |
| Phase 2 字体选择 | 重蹈 v8 GDI+ emoji 间距/乱码 | 锁定 `TextRenderer` 路径；字体族白名单；emoji 异常即 revert 字体提交 |
| Phase 2 阴影/边框 | 加性绘制，几乎不回退现有 | 低风险；新绘制调用，出问题局部 Disable |
| Phase 3 主题→Fences | 改 `FenceLayer` 可能触碰 v9–v17 白 alpha 区 | 映射层独立封装；Fences 失败则回落 `FenceAppearance` 默认；WPF 资源路径不动（已稳）；必跑透明度回归矩阵 |
| Phase 4/5 Dock/组件 | 新顶层窗 z-order/焦点抢/破坏 Fences 命中 | **feature flag 灰度**（`EnableDock`/`EnableWidgets` 默认 false）；坏则翻 flag + 重发，免代码回退 |
| Phase 6 AI | 外部依赖/Key/启动阻塞 | flag 灰度；AI 初始化失败绝不崩主程序；try/catch + 手动兜底；不阻塞启动 |

**Fences 透明度回归矩阵（每次碰 FenceLayer 必跑）**：BodyOpacity=0/15/255、HeaderOpacity=0/10/30、Frosted 开。依据 `overview.md` v17/v18 验收项。

---

## 6. 行动清单（≥3 条，含负责方/紧急度/期望）

| # | 行动 | 负责方 | 紧急度 | 期望完成 |
|---|---|---|---|---|
| 1 | Route A 毛玻璃性能：1/3 分辨率捕获→模糊→放大，保留全分辨率回退开关，真机对比「有点慢」 | 主理人 + AI | 🔴 本周 | Phase 1 退出 |
| 2 | 更新 `M4_B_Appearance_Report.md` 至 v25g 验收，毛玻璃去「实验性」，补「勿截屏桌面子窗口」教训 | AI | 🔴 本周 | 随 Phase 1 |
| 3 | 固化发布安全网脚本（taskkill+归档+双远程 ls-remote 校验），落 `src` 可复用 | AI | 🟠 高 | Phase 0 |
| 4 | Route B：盒阴影+边框色+字体选择+外观预设存读，建立与 `ThemeService` 衔接 | 主理人 + AI | 🟡 中 | Phase 2 |
| 5 | Phase 3 主题引擎：映射 `ResolvedTheme`→`FenceAppearance`、多预设、托盘切换，统一 Phase 2 预设 | 主理人 + AI + gstack-designer | 🟡 中 | Phase 3 |
| 6 | Dock/组件/AI 以 feature flag 灰度；AI 动工前先与主理人定 v1 用例并对接安全官 | 主理人拍板 | 🟢 按需 | Phase 4–6 |

---

## 7. 风险与已知局限

- **毛玻璃性能权衡**：降分辨率模糊在高 DPI 可能轻微软化，须真机确认观感可接受。
- **字体选择 GDI+ emoji 陷阱**：v8 已踩，须复用 `TextRenderer`（见 §5.2）。
- **主题引擎当前不驱动 Fences**：Fences 是 GDI+/layered，不吃 WPF `DynamicResource`；映射若动到 `FenceLayer` alpha 管线，有重现白 alpha bug 风险——必跑回归矩阵。
- **单实例 Mutex**：「打不开」常是残留进程，非构建坏（§5.1-4）。
- **Bash 无法常驻 GUI**：所有验证依赖用户在桌面真机操作（verify-first 纪律的硬约束）。
- **AI 接入**：范围未定义 + 安全面（Key 硬编码、外部进程、提示注入）——须先定 v1 用例，且对接 `gstack-security-officer` 威胁模型（`security-threat-model.md`）。
- **双远程推送偶发假成功**：须 `git ls-remote` 校验。
- **settings.json 脏数据史**：已由 clamp 缓解，新属性须续接 clamp。

---

## 8. 团队协同（避免重复造轮子）

- **gstack-designer**（任务#3 主题引擎架构/视觉规范）：Phase 3 主题引擎须复用其 `ThemeConfig`/`ResolvedTheme` 设计，勿另起炉灶。
- **gstack-security-officer**（任务#4 威胁模型）：Phase 6 AI 接入的 Key/进程/注入防护须以其结论为基线。
- **gstack-qa-lead**（任务#5 打包/回滚预案）：§5 回滚安全网应与其 `validation-runbook`/`qa-packaging-plan` 对齐，发布脚本可直接引用其校验步骤。
- **gstack-investigator**（任务#1 技术栈）：已锁定，本次不重选。

---

## 9. 附录：核实证据

**已读文件**
- `D:\WorkBuddy\桌面美化\.workbuddy\memory\MEMORY.md` — 去箭头封死、双远程坑、M3 bug 状态、M4 v1→v25g 全记录、部署/启动纪律
- `D:\WorkBuddy\桌面美化\.workbuddy\memory\2026-08-31.md` — v25g 验收、下一步三候选
- `D:\WorkBuddy\桌面美化\deliverables\M4_B_Appearance_Report.md` — 停留 v10/2026-08-20（毛玻璃仍标实验性，需更新）
- `D:\WorkBuddy\桌面美化\overview.md` — v17 根治/v18 加固/v19–v25g 毛玻璃 saga
- `src/DesktopSuite/AppSettings.cs` — 8 外观属性 + clamp + 原子写
- `src/DesktopSuite/Themes/*` — 主题脚手架（ColorEngine/ThemeLoader/ThemeService/ThemeConfig + obsidian-glass 预设）
- `src/DesktopSuite/MainWindow.xaml.cs` — `ThemeChanged` 订阅 + `BtnLoadTheme_Click`（仅写 WPF 资源）

**关键提交（来自 MEMORY）**
- `67e857b` M4-A 布局导入导出 ｜ `88dd23d` 毛玻璃 v17 根治 ｜ `2a9914e` v25g 验收 ｜ `b026fc2` 双远程同步
- 历史回归参考：`66d9daf`(v14 全白)、`v22–v25c`(毛玻璃自捕获失败) —— 回滚时按 commit 粒度。

**状态判定**：简报「主题引擎未启动」★不准确★——脚手架已存在且接 WPF UI；真正未启动为 Dock / 小组件 / AI 接入，以及主题引擎的「驱动 Fences + 多预设切换」部分。

# DesktopSuite Phase 3 真机验证手册（V1–V14）

> 版本：Phase 3 验收版 ｜ 日期：2026-08-05 ｜ 执行环境：**真实 Windows 桌面**
> 编写：gstack-qa-lead ｜ 纪律：test → fix → verify（本手册只覆盖 test 与 verify 判据）

> ⚠️ **路径说明**：本手册所有 `%LocalAppData%` 已按 Windows 账户名 `USER` 展开为绝对路径 `C:\Users\USER\AppData\Local\...`。**若你真机的 Windows 用户名不是 `USER`**，请用全局替换把 `C:\Users\USER\` 换成 `C:\Users\<你的实际用户名>\`（例如 `%LocalAppData%` 实际指向 `C:\Users\<用户名>\AppData\Local\`）。其余路径（如 `HKCU\...` 注册表、`WallpaperLibrary` 相对输出目录）保持不变。

---

## 0. 这份手册怎么用

- 本手册中的每一条用例都**必须在真实 Windows 机器上手工执行**。所有涉及 Explorer 桌面图标层、真实关机/注销会话的行为，**无法在任何沙箱或 CI 中预跑**，因此本手册**未经预执行**，其中不含任何"已通过"的结论。
- 每条用例统一 7 字段：**编号 ｜ 名称 ｜ 前置条件 ｜ 操作步骤 ｜ 预期结果 ｜ 通过判据 ｜ 证据**。
- **通过判据**是二值的（PASS/FAIL），执行人不得凭"感觉差不多"判定；判据不满足即 FAIL，按 §7 模板上报。
- 建议执行顺序见 §6，**破坏性/重启类用例（V9、V11、V12、V14）放最后**。

### ⚠️ 三条最容易造成误报的真实行为（执行前必读）

| # | 事实 | 后果 |
|---|------|------|
| A | 点主窗口右上角 **× 只是最小化到托盘**（`OnClosing` 中 `e.Cancel=true`），进程不退出，**不会触发退出恢复** | 用 × "退出"后发现图标没恢复 → **不是缺陷**。V3/V4 必须用托盘菜单「退出（保留壁纸）」或「退出并停止壁纸」 |
| B | 开机自启走 `--background`，主窗口从不 `Show()`，**没有 HWND**，WPF `Application.SessionEnding` 不会到达，只有 `SystemEvents.SessionEnding` 兜底 | V11 必须分 A/B 两条支线，否则兜底路径完全没被覆盖 |
| C | 开了开机自启时，重新登录后程序会按 `DesiredIconsHidden` **立刻重新隐藏**图标 | 会掩盖"关机时已恢复"的现象。判"恢复"必须在**关闭自启**的支线做，或改用日志断言 |

---

## 1. 环境要求

| 项 | 要求 |
|---|---|
| 操作系统 | 真实 Windows 10（1909+）或 Windows 11，**物理机或完整 GUI 虚拟机**；不可用 Server Core / SSH 会话 / 无桌面容器 |
| 会话类型 | 普通交互式登录会话（**不要**用远程桌面会话做 V11/V12，RDP 断开语义与本地注销不同） |
| 权限 | **不需要管理员**。请用日常普通用户账户执行（本程序只写 HKCU 与 C:\Users\USER\AppData\Local） |
| 运行时 | .NET 8 Desktop Runtime（或自带运行时的发布包） |
| 被测产物 | 已 build 的 `DesktopSuite.exe`，运行时根目录 = `AppContext.BaseDirectory`，即 `src\DesktopSuite\bin\Debug\net8.0-windows\`（Release 同理） |
| 显示器 | V1–V12 单屏 100% 缩放即可；V13 需要 ≥2 屏 或 缩放 ≠100% |
| 外部依赖 | mpv（动态壁纸渲染）；`WallpaperLibrary` 需已随输出目录就位（见 §2.2） |

### 1.1 关键路径速查

| 用途 | 路径 |
|---|---|
| 用户设置（意图落盘） | `C:\Users\USER\AppData\Local\DesktopSuite\settings.json` |
| 场景定义 | `C:\Users\USER\AppData\Local\DesktopSuite\scenes.json` |
| 运行日志 | `C:\Users\USER\AppData\Local\DesktopSuite\logs\wallpaper.log` |
| 壁纸库（运行时） | `<输出目录>\WallpaperLibrary\` |
| 开机自启注册表 | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，值名 `DesktopSuite`，值形如 `"<exe路径>" --background` |

---

## 2. 前置准备

### 2.1 首次启动与基线归零

1. 确认没有残留进程：任务管理器 → 详细信息，结束所有 `DesktopSuite.exe`（含带 `--wallpaper-host` 参数的渲染子进程）。
2. **备份并清空历史状态**（保证基线干净）：把 `C:\Users\USER\AppData\Local\DesktopSuite\` 整个文件夹改名为 `DesktopSuite.bak-<日期>`。
3. 双击 `DesktopSuite.exe` 启动，确认：
   - 主窗口出现，标题区正常；
   - 托盘出现 `DesktopSuite — 动态壁纸` 图标；
   - 「桌面整理」分组中，`隐藏桌面图标` 未勾选、`退出时恢复桌面图标（推荐）` **已勾选**（默认 `RestoreIconsOnExit=true`）。
4. 记录基线：执行一次 §2.3 的诊断采集，存为 `evidence/00-baseline.txt`。

> 单实例保护：程序用互斥体 `DesktopSuiteSingleInstance`。重复双击 EXE 不会起第二个实例，只会唤起已有窗口。若"双击没反应"，先看托盘。

### 2.2 确认 WallpaperLibrary 随发布到位（P1-5）

1. 打开输出目录，确认存在 `WallpaperLibrary\` 且其下至少包含：
   - `WallpaperLibrary\深夜\动态壁纸\milkyway-1.mp4`（「专注」场景固定壁纸）
   - `WallpaperLibrary\晚上\动态壁纸\night-city-1.mp4`（「演示」场景固定壁纸）
2. 若缺失：说明 csproj 的 `Content Include="WallpaperLibrary\**\*"` glob 未生效或发布包漏打，**V6/V7/V8 全部阻塞**，先按缺陷上报，不要继续。

### 2.3 如何打开诊断 / 采集证据

**诊断文本（首选证据）**
主窗口 → 「诊断工具」分组 →
1. 点 **`运行壁纸诊断`** → `DiagInfo` 文本框输出壁纸诊断 + 桌面诊断；
2. 点 **`复制诊断信息与日志`** → 内容进剪贴板（含 `=== diagnostics ===` 与 `=== log tail ===` 两段）；
3. 粘贴进记事本，存为 `evidence/<用例号>-diag.txt`。

桌面诊断段的关键字段（`== desktop icons ==`）：

```
== desktop icons ==
DefView  : 0x00010A3C          ← 0 (none) 表示 shell 不可读
ListView : 0x00010A40
visible  : yes | no | unknown (shell unavailable)   ← 图标真实可见性（真值源）
strategy : DefView visible | DefView hidden
intent   : hidden | visible (DesiredIconsHidden)    ← 用户意图（落盘值）
on exit  : restore icons | leave as-is              ← RestoreIconsOnExit
scene    : 日常 | 专注 | 演示 | (none)
last op  : Applied via WM_COMMAND 0x7402 (reality=Hidden)   ← 最近一次 apply 结果

== wallpaper library ==
root     : <输出目录>\WallpaperLibrary
exists   : yes | NO — 壁纸库缺失（未随发布包分发？）
scene 「专注」: OK | MISSING <路径>
scene 「演示」: OK | MISSING <路径>
```

**其它证据来源**

| 证据 | 采集方式 |
|---|---|
| 截图 | `Win + Shift + S` 截屏后粘贴保存；命名 `<用例号>-<步骤>.png` |
| `settings.json` | 资源管理器地址栏输入 `C:\Users\USER\AppData\Local\DesktopSuite` → 记事本打开 `settings.json` |
| 日志 | 同目录 `logs\wallpaper.log`；或主窗口「诊断工具」→ `显示日志尾部（窗口内）` / `打开壁纸日志` |
| 关机/注销时间锚点 | `eventvwr.msc` → Windows 日志 → 系统；关注 **User32 事件 ID 1074**（关机/重启发起者）与 **Kernel-General 12/13**（本次会话启动/结束时间） |
| 录屏（V7 用） | `Win + G` 打开 Xbox Game Bar 录制 |

**只读 PowerShell 辅助命令**（可选，便于快速取证；均为只读）

```powershell
Get-Content "C:\Users\USER\AppData\Local\DesktopSuite\settings.json"
Get-Content "C:\Users\USER\AppData\Local\DesktopSuite\logs\wallpaper.log" -Tail 40
Get-Process DesktopSuite -ErrorAction SilentlyContinue
Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name DesktopSuite -ErrorAction SilentlyContinue
Get-WinEvent -FilterHashtable @{LogName='System'; Id=1074} -MaxEvents 3
```

> 日志会滚动截断。跨重启用例（V2/V11/V12）**执行前请先把 `wallpaper.log` 另存一份**，避免关键行被覆盖。

### 2.4 逃生通道（任何时候图标丢了都能自救）

桌面空白处右键 → **查看** → **显示桌面图标**。主窗口「桌面整理」分组底部也印了这句提示。执行任何隐藏类用例前，请先确认自己知道这条路径。

---

## 3. 术语与判据约定

| 术语 | 含义 |
|---|---|
| **意图（intent）** | `settings.DesiredIconsHidden`，用户"想不想隐藏"，持久化 |
| **现实（reality）** | `SysListView32` 的 `WS_VISIBLE`，由 `DesktopShell.AreIconsVisible()` 实时读取，**唯一真值源** |
| **Unknown** | Shell 不可读（`AreIconsVisible()` 返回 `null`）→ 不确定 → **禁止写 intent** |
| **Failed** | Shell 可读但拒绝执行 → 现实已知 → 允许写 intent |
| **真正退出** | 托盘右键 → 「退出（保留壁纸）」或「退出并停止壁纸」。**点 × 不算** |

---

## 4. 用例总表

| 编号 | 名称 | 优先级 | 是否需重启/注销 | 关联修复项 |
|---|---|---|---|---|
| V1 | 隐藏/显示桌面图标基本可用 | P0 | 否 | — |
| **V2** | **隐藏意图跨重启保留** | **P0** | **是（重启）** | **P0-1** |
| **V3** | **退出恢复且不抹掉意图** | **P0** | 否 | **P0-1 / P1-2** |
| V4 | 关闭"退出恢复"后保持隐藏 | P1 | 否 | — |
| V5 | 托盘菜单与复选框状态同步 | P1 | 否 | — |
| V6 | 托盘场景子菜单（日常/专注/演示） | P1 | 否 | — |
| V7 | 场景切换无双闪 | P1 | 否 | P1-3 |
| V8 | 固定壁纸文件就位且可播放 | P1 | 否 | P1-5 |
| V9 | Unknown 态不写 intent + 有反馈 | P1 | 否（需重启 Explorer） | P1-7 / P1-8 |
| V10 | Apply 返回值全程可见 | P1 | 否 | P1-8 |
| **V11** | **真实关机 SessionEnding 恢复** | **P0** | **是（关机/重启）** | **P1-2** |
| **V12** | **真实注销 SessionEnding 恢复** | **P0** | **是（注销）** | **P1-2** |
| V13 | 多显示器 / 高 DPI 行为记录 | P2（观察） | 否 | 待增强 |
| V14 | 进程异常退出后的下次启动 | P2 | 否 | — |

---

## 5. 用例明细

---

### V1 ｜ 隐藏/显示桌面图标基本可用

**前置条件**
- 已完成 §2.1，程序运行中，主窗口可见。
- 桌面上**至少有 3 个图标**（便于肉眼判定）。
- `隐藏桌面图标` 未勾选；诊断中 `visible : yes`。

**操作步骤**
1. 主窗口「桌面整理」→ 勾选 `隐藏桌面图标`。
2. 等待最多 3 秒，观察桌面。
3. 点 `运行壁纸诊断`，记录 `== desktop icons ==` 段。
4. 取消勾选 `隐藏桌面图标`。
5. 等待最多 3 秒，再次运行诊断。

**预期结果**
- 步骤 2：桌面所有图标消失，壁纸/背景仍在，桌面右键菜单仍可用。
- 步骤 3：`visible : no`、`intent : hidden (DesiredIconsHidden)`、`last op : Applied via WM_COMMAND 0x7402 (reality=Hidden)`；底部状态栏显示 `桌面图标：已隐藏（WM_COMMAND 0x7402）`。
- 步骤 5：图标全部恢复；`visible : yes`、`intent : visible`；状态栏 `桌面图标：已显示（…）`。

**通过判据**
- ✅ 勾选后 `visible : no` 且肉眼图标消失；取消后 `visible : yes` 且图标恢复。
- ✅ 两次 `last op` 的 Outcome 均为 `Applied` 或 `AlreadyInState`。
- ✅ 全程桌面右键菜单可用（说明走的是原生 `0x7402`，未降级到 `ShowWindow`）。
- ❌ 若 `last op` 出现 `via ShowWindow 降级` → 记为 medium 缺陷（原生命令未生效，右键菜单可能受损），但不阻塞后续。

**证据**：`V1-hidden.png`、`V1-shown.png`、`V1-diag.txt`（含两次诊断）

---

### V2 ｜ 隐藏意图跨重启保留（核心，验收 P0-1）

> 本用例验证：**用户"想隐藏"这件事，能跨越一次完整的机器重启活下来**。分 A/B 两条支线，**两条都要跑**。

#### V2-A ｜ 未配置开机自启（手动启动支线）

**前置条件**
- 已完成 §2.1；`退出时恢复桌面图标（推荐）` **保持勾选**（默认）。
- `登录 Windows 时自动启动（仅驻留托盘）` **未勾选**；确认注册表中**无** `HKCU\...\Run\DesktopSuite` 项。
- 已另存一份 `wallpaper.log` 备份。

**操作步骤**
1. 勾选 `隐藏桌面图标`，确认桌面图标消失。
2. 打开 `C:\Users\USER\AppData\Local\DesktopSuite\settings.json`，确认已含 `"DesiredIconsHidden": true`。**记录整份文件内容**。
   - 说明：apply 成功后会立刻 `Save()`，无需退出即可看到。
3. 托盘右键 → 「退出（保留壁纸）」，真正退出程序。
4. 确认任务管理器中已无 `DesktopSuite.exe` 主进程。
5. 开始菜单 → 电源 → **重启**。
6. 重新登录，**先不要启动程序**，观察桌面并截图。
7. 再次打开 `settings.json`，确认 `DesiredIconsHidden` 的值。
8. 手动双击 `DesktopSuite.exe` 启动。
9. 等待最多 15 秒（启动重试退避为 500/1000/2000/4000/8000 ms，最多 6 次），观察桌面与主窗口。
10. 运行诊断并采集。

**预期结果**
- 步骤 6：桌面图标**可见**（因为退出时执行了临时恢复），这是**正确行为**，不是缺陷。
- 步骤 7：`"DesiredIconsHidden": true` —— 意图跨重启存活。
- 步骤 9：主窗口 `隐藏桌面图标` 复选框**呈勾选态**；桌面图标在数秒内**被重新隐藏**。
- 步骤 10：`visible : no`、`intent : hidden`；`DesktopStatus` 显示 `期望：隐藏 ｜ 实际：隐藏`。
- 日志包含 `启动应用图标意图成功（第 N 次，WM_COMMAND 0x7402）。`

**通过判据**
- ✅ 重启后（启动程序前）`settings.json` 中 `DesiredIconsHidden` 仍为 `true`。
- ✅ 启动程序后复选框为勾选态，且**未经任何人工点击**图标即被隐藏。
- ✅ 诊断 `intent : hidden` 且 `visible : no`。
- ❌ 若重启后 `DesiredIconsHidden` 变成 `false` → **critical，P0-1 回归**。
- ❌ 若 `DesiredIconsHidden=true` 但启动后图标仍可见且复选框未勾选 → **critical**（启动重应用链路断裂）。
- ❌ 若 `DesiredIconsHidden=true` 但复选框显示未勾选（UI 与意图不一致）→ high。

**证据**：`V2A-settings-before-reboot.json`、`V2A-desktop-after-login.png`、`V2A-settings-after-reboot.json`、`V2A-after-launch.png`、`V2A-diag.txt`、日志片段

#### V2-B ｜ 已配置开机自启（`--background` 支线）

**前置条件**
- 在 V2-A 完成后进行；程序运行中。
- 勾选主窗口 `登录 Windows 时自动启动（仅驻留托盘）`，确认注册表出现 `HKCU\...\Run\DesktopSuite`，值形如 `"<exe路径>" --background`。

**操作步骤**
1. 勾选 `隐藏桌面图标`，确认图标消失、`settings.json` 中 `DesiredIconsHidden=true`。
2. 托盘 →「退出（保留壁纸）」。
3. 开始菜单 → 电源 → **重启**。
4. 重新登录后，**开始计时**，持续观察桌面 60 秒（登录初期 Explorer 可能尚未就绪，允许图标先短暂可见再被隐藏）。
5. 观察托盘是否出现 DesktopSuite 图标；**注意此支线主窗口不应自动弹出**。
6. 托盘右键 →「显示主窗口」，运行诊断。

**预期结果**
- 步骤 4：登录后 **60 秒内**桌面图标被隐藏（通常几秒内）。允许"先可见后隐藏"的短暂过渡。
- 步骤 5：托盘图标存在；主窗口**不自动显示**、任务栏无按钮。
- 步骤 6：`intent : hidden`、`visible : no`；复选框勾选；日志有 `启动应用图标意图成功（第 N 次，…）`。

**通过判据**
- ✅ 登录后 60 秒内图标自动隐藏，无需任何人工操作。
- ✅ 主窗口未自动弹出（`--background` 语义正确）。
- ✅ `intent : hidden` 未被改写。
- ❌ 60 秒后仍未隐藏，且日志出现 `启动应用图标意图失败：重试 6 次后仍未生效 —— …` → high（登录时序问题，记录 `Describe()` 全文）。
- ❌ 主窗口自动弹出 → medium（`--background` 未生效）。

**证据**：`V2B-run-key.txt`、`V2B-timeline.png`（登录后 0s/5s/30s 各一张）、`V2B-diag.txt`、日志片段

> 收尾：V2-B 完成后**保持开机自启开启**，V11-B 会复用；V11-A 前再关掉。

---

### V3 ｜ 退出恢复且不抹掉意图（核心）

> 本用例验证：`RestoreIconsOnExit` 是**临时的礼貌性恢复**，绝不能污染 `DesiredIconsHidden`。

**前置条件**
- 程序运行中；`退出时恢复桌面图标（推荐）` **已勾选**（诊断 `on exit : restore icons`）。
- 开机自启**关闭**（避免干扰）。
- 已备份 `wallpaper.log`。

**操作步骤**
1. 勾选 `隐藏桌面图标`，确认桌面图标消失。
2. 运行诊断，确认 `visible : no`、`intent : hidden`、`on exit : restore icons`。
3. **⚠️ 不要点窗口右上角 ×**（那只是最小化到托盘）。托盘右键 → 「退出（保留壁纸）」。
4. 等待 3 秒，观察桌面并截图。
5. 打开 `settings.json`，记录 `DesiredIconsHidden` 与 `RestoreIconsOnExit` 的值。
6. 打开 `wallpaper.log`，检索最后出现的 `退出（窗口关闭）` 行。
7. 重新双击 `DesktopSuite.exe` 启动，等待最多 15 秒。
8. 观察桌面与复选框，运行诊断。

**预期结果**
- 步骤 4：桌面图标**恢复可见**。
- 步骤 5：`"DesiredIconsHidden": true`（**意图未被抹掉**）、`"RestoreIconsOnExit": true`。
- 步骤 6：日志含 `退出（窗口关闭）：恢复桌面图标 → Applied（WM_COMMAND 0x7402）。`（或 `→ AlreadyInState（无需操作）`）。
- 步骤 7–8：图标**再次被自动隐藏**；复选框勾选；诊断 `intent : hidden`、`visible : no`。

**通过判据**
- ✅ 退出后图标可见（恢复生效）。
- ✅ **且** `DesiredIconsHidden` 仍为 `true`（临时恢复未抹意图）—— 这两条必须**同时**成立，缺一即 FAIL。
- ✅ 再次启动后自动重新隐藏，全程无需人工点击。
- ✅ 日志中存在 `退出（窗口关闭）：恢复桌面图标 → …` 记录。
- ❌ 退出后 `DesiredIconsHidden` 变为 `false` → **critical**：`_suppressIconEvents` 卫兵或意图快照回写失效（`Apply→StateChanged→ChkHideIcons_Changed` 反向改写）。
- ❌ 退出后图标仍隐藏 → high（退出恢复未执行）；先确认步骤 3 用的是**托盘退出**而不是 ×。
- ❌ 重新启动后图标未重新隐藏但 intent 为 true → high。

**证据**：`V3-hidden.png`、`V3-after-exit.png`、`V3-settings-after-exit.json`、`V3-log-exit.txt`、`V3-relaunch.png`、`V3-diag.txt`

---

### V4 ｜ 关闭"退出恢复"后保持隐藏

**前置条件**：程序运行中，图标当前可见。

**操作步骤**
1. **取消勾选** `退出时恢复桌面图标（推荐）`。
2. 运行诊断，确认 `on exit : leave as-is`；确认 `settings.json` 中 `"RestoreIconsOnExit": false`。
3. 勾选 `隐藏桌面图标`，确认图标消失。
4. 托盘 →「退出（保留壁纸）」。
5. 等待 5 秒，观察桌面并截图。
6. 检索日志。
7. 重新启动程序，观察。
8. **收尾：重新勾选 `退出时恢复桌面图标（推荐）` 恢复默认。**

**预期结果**
- 步骤 5：桌面图标**保持隐藏**。
- 步骤 6：日志含 `退出（窗口关闭）：用户已关闭「退出时恢复桌面图标」，保持当前状态。`
- 步骤 7：图标仍隐藏；复选框勾选；诊断 `last op` 为 `AlreadyInState`（已处于目标状态，未重复发命令）。

**通过判据**
- ✅ 退出后图标保持隐藏，且日志出现上述"保持当前状态"行。
- ✅ 重启程序后仍隐藏，`intent : hidden`。
- ✅ 步骤 8 收尾完成（否则会污染后续用例）。
- ❌ 关闭该选项后图标仍被恢复 → high（选项失效）。

**证据**：`V4-after-exit.png`、`V4-log.txt`、`V4-diag.txt`

---

### V5 ｜ 托盘菜单与复选框状态同步

**前置条件**：程序运行中，主窗口可见，图标当前**可见**。

**操作步骤**
1. 托盘右键，记录 `🗂️ 隐藏桌面图标：___` 当前文案，截图菜单。
2. 点击该菜单项。
3. 等待 3 秒，观察桌面；切到主窗口看复选框；再次打开托盘菜单看文案。
4. 再次点击该托盘菜单项还原。
5. 改从**主窗口复选框**勾选隐藏，然后打开托盘菜单查看文案。
6. 取消勾选还原。

**预期结果**

| 时刻 | 托盘文案 | 复选框 | 桌面 |
|---|---|---|---|
| 初始 | `🗂️ 隐藏桌面图标：关` | 未勾选 | 图标可见 |
| 步骤 3 | `🗂️ 隐藏桌面图标：开` | **已勾选** | 图标消失 |
| 步骤 4 后 | `🗂️ 隐藏桌面图标：关` | 未勾选 | 图标可见 |
| 步骤 5 | `🗂️ 隐藏桌面图标：开` | 已勾选 | 图标消失 |

**通过判据**
- ✅ 托盘触发的变更，主窗口复选框**自动**同步（无需手动刷新）；反之亦然。
- ✅ 托盘文案三态映射正确：开=已隐藏 / 关=可见 / 未知=Shell 不可读。
- ✅ 双向切换过程中，图标**不出现连续两次翻转**（说明 `SetIconCheckboxSilently` 的反入卫兵生效）。
- ❌ 出现"点一次托盘、图标闪两下"或"复选框自己弹回" → high（事件回环）。
- ❌ 托盘与复选框长期不一致 → high。

**证据**：`V5-tray-off.png`、`V5-tray-on.png`、`V5-checkbox.png`

---

### V6 ｜ 托盘场景子菜单（日常 / 专注 / 演示）

**前置条件**
- §2.2 已确认两个固定壁纸文件存在。
- 程序运行中；mpv 可用（先在「壁纸」分组手动播过一个动态壁纸确认渲染链路正常）。

**操作步骤**
1. 托盘右键 → `🎬 场景` → **专注**。等待最多 15 秒。
2. 记录：桌面图标、壁纸画面、主窗口 `Status` 文案、场景下拉框选中项、声音复选框。
3. 运行诊断，记录 `scene :` 与 `intent :`。
4. 托盘 → `🎬 场景` → **演示**，重复步骤 2–3。
5. 托盘 → `🎬 场景` → **日常**，重复步骤 2–3。
6. 检查 `settings.json` 中 `ActiveSceneName`。

**预期结果**

| 场景 | 图标 | 壁纸 | 轮换 | 声音 |
|---|---|---|---|---|
| 专注 | 隐藏 | 固定 `milkyway-1.mp4`（银河） | 关 | 关，音量 0 |
| 演示 | 隐藏 | 固定 `night-city-1.mp4`（夜景城市） | 关 | 关，音量 0 |
| 日常 | **显示** | 跟随时段轮换（按当前时段取库中壁纸） | **开** | 关，音量 80 |

- 每次应用后 `Status` 显示 `已应用场景：<名称>`；若有部分失败会带括号说明（如 `（壁纸：文件缺失「milkyway-1.mp4」，壁纸未切换）`）。
- 主窗口场景下拉框自动选中刚应用的场景；`轮换`/`声音` 复选框同步为场景值。
- 诊断 `scene : <名称>`；`settings.json` 中 `ActiveSceneName` 一致。

**通过判据**
- ✅ 三个场景的「图标可见性 + 壁纸 + 轮换 + 声音」四项全部符合上表。
- ✅ `Status` 文案为纯 `已应用场景：X`（**无**括号内的失败说明）。
- ✅ `ActiveSceneName` 与诊断 `scene :` 一致。
- ❌ `Status` 带 `壁纸：文件缺失…` → high，且直接判定 V8 FAIL（P1-5 回归）。
- ❌ `Status` 出现 `应用场景失败：… （设置已回滚）` → high；同时检查 `settings.json` 是否**完整回滚**（`RotationEnabled`/`AudioEnabled`/`Volume`/`DesiredIconsHidden`/`ActiveSceneName` 全部为应用前的值），若只回滚了一部分则升级为 critical（P1-4 原子性破损）。

**证据**：三张场景截图 + 三份 `Status` 截图 + `V6-diag.txt` + `V6-settings.json`

---

### V7 ｜ 场景切换无双闪（验收 P1-3）

> 双闪 = `SetEnabled(true)` 已触发一次 tick，若再多调一次 `RotateNow()` 会导致壁纸连切两次。

**前置条件**
- 当前处于「专注」或「演示」场景（固定壁纸播放中），以便切到「日常」时确实发生一次壁纸变更。
- **先记录当前 `wallpaper.log` 的行数**（或另存备份），以便只统计本次操作新增的日志。
- 开启 `Win + G` 录屏（推荐，便于逐帧复核）。

**操作步骤**
1. 开始录屏。
2. 托盘 → `🎬 场景` → **日常**。
3. 持续录制 20 秒，全程盯着桌面壁纸。
4. 停止录屏。
5. 打开 `wallpaper.log`，统计**本次操作新增部分**中 `--- StartDynamic ---` 出现的次数。
6. 记录主窗口 `LibStatus` 的文案。

**预期结果**
- 壁纸只发生**一次**切换：黑屏/过渡至多出现一次，随后稳定播放当前时段壁纸。
- 日志中本次新增的 `--- StartDynamic ---` **恰好 1 行**（若当前时段壁纸与正在播放的文件相同，则为 **0 行**，同时 `LibStatus` 显示 `时段「X」继续播放 <文件名>`）。
- `LibStatus` 显示 `时段「<时段>」→ <文件名>` 或 `时段「X」继续播放 …`，**不应**同一秒内出现两条切换记录。

**通过判据**
- ✅ 本次新增 `--- StartDynamic ---` 数量 ∈ {0, 1}。**≥2 即 FAIL**（P1-3 回归，medium→high）。
- ✅ 录屏逐帧确认壁纸未出现"切换 → 再切换"的二次跳变。
- ✅ `LibStatus` 未出现两条同时段切换记录。
- 备注：若当前时段目录为空，`LibStatus` 会显示 `时段「X」暂无壁纸，已跳过`，此时本用例**不成立**，请换到一个有壁纸的时段重跑，或临时往当前时段目录放一个视频。

**证据**：`V7-screen-recording.mp4`、`V7-log-delta.txt`（本次新增日志片段）、`V7-libstatus.png`

---

### V8 ｜ 固定壁纸文件就位且可播放（验收 P1-5）

**前置条件**：程序运行中。

**操作步骤**
1. 点 `运行壁纸诊断`，定位 `== wallpaper library ==` 段。
2. 记录 `root`、`exists`、`scene 「专注」`、`scene 「演示」` 四行。
3. 在资源管理器中打开 `root` 路径，确认两个 mp4 文件真实存在且大小 > 0。
4. 应用「专注」场景，肉眼确认银河视频在桌面播放（画面有动态）。
5. 应用「演示」场景，肉眼确认夜景城市视频播放。

**预期结果**
- `exists   : yes`
- `scene 「专注」: OK <…>\WallpaperLibrary\深夜\动态壁纸\milkyway-1.mp4`
- `scene 「演示」: OK <…>\WallpaperLibrary\晚上\动态壁纸\night-city-1.mp4`
- 两个场景的壁纸都能在桌面实际播放（**动态画面**，不是静止首帧、不是黑屏）。

**通过判据**
- ✅ 诊断中两条 scene 行均为 `OK`，且 `exists : yes`。
- ✅ 两个视频均实际播放。
- ❌ 出现 `NO — 壁纸库缺失（未随发布包分发？）` 或任一 `MISSING` → **high，P1-5 回归**（csproj `Content` glob 或发布打包漏项）。
- ❌ 文件存在但黑屏/不播放 → high，检查 mpv 是否就位，附 `Renderer exited early with code …` 日志行。

**证据**：`V8-diag-library.txt`、`V8-explorer-files.png`、`V8-focus-playing.png`、`V8-demo-playing.png`

---

### V9 ｜ Unknown 态不写 intent 且有反馈（验收 P1-7 / P1-8）

> 制造 Shell 不可读：结束 `explorer.exe`。此时 `Progman`/`SHELLDLL_DefView` 消失，`AreIconsVisible()` 返回 `null`。
> ⚠️ 结束 Explorer 会让**任务栏与托盘一并消失**，所以**必须提前把主窗口留在前台**，本用例只能用主窗口复选框操作。

**前置条件**
- 程序运行中，**主窗口可见且置于前台**（不要最小化到托盘）。
- `隐藏桌面图标` 当前**未勾选**，图标可见。
- **先完整抄下 `settings.json` 的内容**（尤其 `DesiredIconsHidden` 的值），这是核心对照物。
- 已知恢复方法：任务管理器 → 文件 → 运行新任务 → 输入 `explorer.exe` → 确定。

**操作步骤**
1. `Ctrl+Shift+Esc` 打开任务管理器，找到「Windows 资源管理器」→ 右键 → **结束任务**（不要选"重新启动"）。
2. 确认任务栏、桌面图标、托盘全部消失（DesktopSuite 主窗口仍在）。
3. 在主窗口中**勾选** `隐藏桌面图标`。
4. 等待最多 10 秒（降级链每步 1s 超时 + 150ms 沉降），读取底部 `Status` 文本框与 `DesktopStatus` 文本。
5. **不要重启 Explorer**，先打开 `settings.json`（用记事本，路径手输），对照 `DesiredIconsHidden` 是否被改动。
6. 任务管理器 → 文件 → 运行新任务 → `explorer.exe`，恢复桌面。
7. 回到主窗口，点 `运行壁纸诊断`，记录 `last op` 行；并检索日志。

**预期结果**
- 步骤 4：
  - `Status` 显示 `桌面图标切换未生效 —— 无法读取桌面图标层（状态未知）`
  - **并追加一行** `偏好未写入（避免用未知状态覆盖你的选择）；可稍后重试或运行诊断。`
  - `DesktopStatus` 显示 `期望：显示 ｜ 实际：未知（无法读取资源管理器）`，并附 `上次操作：无法读取桌面图标层（状态未知）`
- 步骤 5：`settings.json` 中 `DesiredIconsHidden` **与步骤前完全一致（未被改写）**。
- 步骤 7：诊断 `last op : Unknown via 无（shell 不可读） (reality=Unknown)`；日志含
  `IconHider.Apply(hidden=True): 桌面图标层不可读 (SHELLDLL_DefView/SysListView32 未找到) — 判定为 Unknown，不落盘 intent。`

**通过判据**
- ✅ `DesiredIconsHidden` 在整个 Unknown 期间**一字未改** —— 这是 P1-7 的核心判据。
- ✅ UI **明确报错**（`Status` 两行文案齐全），**不得静默**（P1-8）。
- ✅ 未触发破坏性 `ShowWindow` 降级（诊断 `last op` 的 Strategy 不应为 `ShowWindow 降级`）。
- ✅ Explorer 恢复后程序不崩溃、可继续正常切换（回到 V1 流程验证一次）。
- ❌ `DesiredIconsHidden` 被改写 → **critical，P1-7 回归**。
- ❌ `Status` 无任何提示 → high，P1-8 回归。

**补充（可选，尽力而为）**：Explorer 刚重启的 1–2 秒内点托盘 `🗂️ 隐藏桌面图标`，若捕捉到 Unknown 窗口期，应看到 `无法读取桌面图标状态（资源管理器未就绪），已取消切换。` 且托盘文案为 `🗂️ 隐藏桌面图标：未知`。捕捉不到不判 FAIL。

**证据**：`V9-settings-before.json`、`V9-status-unknown.png`、`V9-settings-after.json`、`V9-diag.txt`、`V9-log.txt`

---

### V10 ｜ Apply 返回值全程可见（验收 P1-8）

**前置条件**：程序运行中，主窗口可见。

**操作步骤**
1. 正常勾选 `隐藏桌面图标`，立刻观察 `DesktopStatus` 的**过渡文案**，再看最终 `Status`。
2. 取消勾选，同样观察。
3. 在一次切换尚未完成时（点击后 1 秒内）**再点一次**复选框，观察 `Status`。
4. 运行诊断，记录 `last op` 行。
5. （若 V9 已执行）复用 V9 的 Unknown 反馈作为失败态样本。

**预期结果**
- 步骤 1：先出现 `正在隐藏桌面图标…`，完成后 `Status` = `桌面图标：已隐藏（WM_COMMAND 0x7402）`。
- 步骤 2：先 `正在显示桌面图标…`，完成后 `桌面图标：已显示（…）`。
- 步骤 3：`Status` = `桌面操作正在进行中，请稍候…`，且复选框被**拨回真实状态**（不会停在一个骗人的位置）。
- 步骤 4：`last op : Applied via WM_COMMAND 0x7402 (reality=Hidden)` 之类，字段完整。
- `DesktopStatus` 常驻显示 `期望：X ｜ 实际：Y`；当期望≠实际时会额外显示 `上次操作：…`。

**通过判据**
- ✅ 每一次 apply（成功/失败/忙/未知）都有**可见的文字反馈**，无任何静默分支。
- ✅ 长耗时操作有 `正在…` 过渡文案（说明 P1-6 的异步化 + 反馈到位），且**主窗口在切换期间不卡死**（可拖动窗口）。
- ✅ 并发点击被拦截且复选框回正。
- ❌ 任一路径无反馈 → medium/high（按用户可感知程度）。
- ❌ 切换期间窗口无响应（转圈/白屏）超过 1 秒 → medium（P1-6 回归）。

**证据**：`V10-progress.png`、`V10-success.png`、`V10-busy.png`、`V10-diag.txt`

---

### V11 ｜ 真实关机 SessionEnding 恢复（核心，验收 P1-2）

> 关机/注销会**直接拆掉进程，不触发 `Window.OnClosed`**，只能靠 `SessionEnding` 恢复图标。
> **必须跑 A、B 两条支线**：A 验"确实恢复了"，B 验"`--background` 无 HWND 时的 `SystemEvents` 兜底"。

#### V11-A ｜ 前台运行 + 关闭开机自启（验证恢复本身）

**前置条件**
- `退出时恢复桌面图标（推荐）` **已勾选**。
- `登录 Windows 时自动启动` **已取消勾选**，且注册表 `HKCU\...\Run\DesktopSuite` **不存在**（否则登录后会被立刻重新隐藏，掩盖结果，见 §0-C）。
- **另存一份 `wallpaper.log` 备份**（关机后要对比新增行）。
- 关闭所有其它有未保存内容的程序。

**操作步骤**
1. 主窗口勾选 `隐藏桌面图标`，确认图标消失。
2. 运行诊断确认 `visible : no`、`intent : hidden`、`on exit : restore icons`。
3. **保持程序运行**（主窗口可见或最小化到托盘均可，不要退出）。
4. 开始菜单 → 电源 → **关机**（若机器不便断电，可用**重启**，语义等价）。
5. 记录关机开始时刻。**留意关机过程**：是否出现"此应用正在阻止关机"的拦截页面，是否明显变慢。
6. 开机、重新登录。
7. **先不要启动任何程序**，观察桌面并截图。
8. 打开 `wallpaper.log`，找到关机时刻附近的新增行。
9. 打开 `settings.json`，记录 `DesiredIconsHidden`。
10. 手动启动 `DesktopSuite.exe`，等待最多 15 秒，观察图标与复选框。

**预期结果**
- 步骤 5：**不出现任何阻止关机的界面**（代码明确不设 `e.Cancel`）；关机耗时与平时无明显差异。
- 步骤 7：桌面图标**可见** —— 这就是 SessionEnding 恢复生效的直接证据。
- 步骤 8：日志中包含（顺序/条数取决于哪条路径先到，两条都到也正常，有闩锁不会重复执行恢复）：
  - `SessionEnding（Shutdown）—— 尝试恢复桌面图标。` 和/或
  - `SystemEvents.SessionEnding（SystemShutdown）—— 尝试恢复桌面图标。`
  - 以及 **恰好一条** `退出（系统关机）：恢复桌面图标 → Applied（WM_COMMAND 0x7402）。`（或 `→ AlreadyInState（无需操作）`）
- 步骤 9：`"DesiredIconsHidden": true`（**意图仍在**）。
- 步骤 10：图标被重新隐藏，复选框勾选。

**通过判据**
- ✅ 重新登录后（启动程序前）桌面图标**可见**。
- ✅ 日志中至少命中一条 `SessionEnding…尝试恢复桌面图标`，且 `退出（系统关机）：恢复桌面图标 → …` **只有一条**（闩锁 `_iconsRestored` 生效，未双跑）。
- ✅ `DesiredIconsHidden` 仍为 `true`。
- ✅ 关机未被阻断、无拦截页。
- ❌ 登录后图标仍隐藏 且 日志无任何 SessionEnding 行 → **critical，P1-2 回归**（用户会拿到一个空桌面）。
- ❌ 日志有 SessionEnding 行但恢复结果为 `Failed`/`Unknown` → high，附 Strategy 全文（很可能是关机时 Explorer 已先行退出，属已知窗口期，需评估）。
- ❌ 出现"此应用正在阻止关机" → high（不得阻断关机）。
- ❌ `退出（系统关机）：恢复桌面图标` 出现 **2 条及以上** → medium（闩锁失效）。
- ❌ `DesiredIconsHidden` 变为 `false` → critical（恢复污染了意图）。

**证据**：`V11A-before-shutdown-diag.txt`、`V11A-shutdown-screen.png`（如出现拦截页务必截图）、`V11A-desktop-after-login.png`、`V11A-log-sessionending.txt`、`V11A-settings-after.json`、事件查看器 ID 1074 截图

#### V11-B ｜ `--background` 自启（验证 SystemEvents 兜底路径）

> 此支线主窗口**从未 `Show()`**，没有 HWND，WPF `Application.SessionEnding` **不会到达**。若日志里只有 `SystemEvents.SessionEnding（…）` 而没有 `SessionEnding（…）`，**这正是预期**，说明兜底订阅在真正起作用。

**前置条件**
- 勾选 `登录 Windows 时自动启动（仅驻留托盘）`，确认注册表项存在。
- `退出时恢复桌面图标（推荐）` 已勾选。
- 备份日志。

**操作步骤**
1. 勾选 `隐藏桌面图标`，确认图标消失、`DesiredIconsHidden=true`。
2. 托盘 →「退出（保留壁纸）」，然后**重启机器**（让下次登录走纯 `--background` 路径）。
3. 登录后等待图标被自动隐藏（同 V2-B，最长 60 秒）。**全程不要打开主窗口**。
4. 确认托盘有图标、主窗口未显示、桌面图标已隐藏。
5. 再次：开始菜单 → 电源 → **关机**（或重启）。
6. 重新登录，**在图标被重新隐藏之前**尽快连续截图（建议登录后 0s / 3s / 10s 各一张）。
7. 托盘 →「显示主窗口」→ 打开日志，检索本次关机时刻附近的行。

**预期结果**
- 步骤 6：登录初期桌面图标**曾经可见**（说明关机时恢复成功），随后被自启程序重新隐藏。
- 步骤 7：日志中含 `SystemEvents.SessionEnding（SystemShutdown）—— 尝试恢复桌面图标。` 与 `退出（系统关机）：恢复桌面图标 → …`；**可以没有** WPF 侧的 `SessionEnding（Shutdown）` 行。

**通过判据**
- ✅ 日志中存在 `SystemEvents.SessionEnding（…）` 行 —— 兜底订阅在无 HWND 场景下确实触发。
- ✅ 存在 `退出（系统关机）：恢复桌面图标 → Applied/AlreadyInState`。
- ✅ 登录早期截图能捕捉到"图标可见"的窗口期（若因自启太快没抓到，**改用日志断言判定**，不因抓拍失败判 FAIL）。
- ✅ 关机未被阻断。
- ❌ 日志中**两条 SessionEnding 都没有** → **critical，P1-2 兜底路径失效**（这正是 `--background` 用户最容易被坑的场景）。
- ❌ 关机变慢明显（>30 秒额外等待）或出现拦截页 → high。

**证据**：`V11B-login-0s.png` / `-3s.png` / `-10s.png`、`V11B-log-systemevents.txt`、`V11B-run-key.txt`

---

### V12 ｜ 真实注销 SessionEnding 恢复（核心，验收 P1-2）

> 与 V11 同源但走 `Logoff` 分支。**必须用"注销"，不能用"锁定"或"切换用户"** —— 锁定不结束会话，不会触发 SessionEnding。

**前置条件**
- `退出时恢复桌面图标（推荐）` 已勾选。
- 开机自启**关闭**（便于直接观察恢复结果）。
- 备份 `wallpaper.log`。
- **不要在远程桌面（RDP）会话中执行本用例**，RDP 断开/注销语义与本地不同。

**操作步骤**
1. 勾选 `隐藏桌面图标`，确认图标消失。
2. 运行诊断确认 `visible : no`、`intent : hidden`、`on exit : restore icons`。
3. 保持程序运行（不要退出）。
4. 开始菜单 → 点击用户头像 → **注销**（Sign out）。
5. 记录注销开始时刻，留意是否出现阻止注销的界面。
6. 在登录界面重新登录**同一账户**。
7. **先不要启动程序**，观察桌面并截图。
8. 打开 `wallpaper.log`，检索注销时刻附近新增行。
9. 打开 `settings.json`，记录 `DesiredIconsHidden`。
10. 启动程序，确认按意图重新隐藏。

**预期结果**
- 步骤 5：注销未被阻断，无拦截页。
- 步骤 7：桌面图标**可见**。
- 步骤 8：日志含
  - `SessionEnding（Logoff）—— 尝试恢复桌面图标。` 和/或 `SystemEvents.SessionEnding（Logoff）—— 尝试恢复桌面图标。`
  - **恰好一条** `退出（系统注销）：恢复桌面图标 → Applied（…）。`
    （注意 reason 文案为「**系统注销**」，与 V11 的「系统关机」不同 —— 这是区分两条路径的关键字段）
- 步骤 9：`"DesiredIconsHidden": true`。
- 步骤 10：图标重新隐藏。

**通过判据**
- ✅ 重新登录后（启动程序前）图标可见。
- ✅ 日志中出现 **`Logoff`** 相关的 SessionEnding 行，且恢复日志的 reason 为「**系统注销**」（证明走的是注销分支，不是被别的路径顺带救了）。
- ✅ `退出（系统注销）：恢复桌面图标` 只有一条。
- ✅ `DesiredIconsHidden` 仍为 `true`。
- ✅ 注销未被阻断。
- ❌ 登录后图标仍隐藏且无 Logoff 日志 → **critical，P1-2 注销路径回归**。
- ❌ 恢复日志 reason 显示「系统关机」而非「系统注销」→ low（reason 映射错误，不影响功能，但日志会误导排障）。

**证据**：`V12-before-logoff-diag.txt`、`V12-desktop-after-login.png`、`V12-log-logoff.txt`、`V12-settings-after.json`

> **可选加测（推荐）**：开启开机自启后重跑一次 V12，验证注销路径下的 `SystemEvents` 兜底（同 V11-B 判据，只是 Reason 换成 `Logoff`）。

---

### V13 ｜ 多显示器 / 高 DPI 行为记录（观察项）

> 本项为**行为记录**，非严格功能判定。已标注为**已知待增强**方向，除非出现崩溃/卡死/不可恢复状态，否则只记录不判 FAIL。

**前置条件**
- 至少满足其一：接入 ≥2 台显示器；或系统缩放设置为 125% / 150%（设置 → 系统 → 显示 → 缩放）。
- 记录当前配置：屏幕数量、各屏分辨率、各屏缩放比例、主屏是哪一台。

**操作步骤**
1. 记录环境配置（截图"显示设置"页）。
2. 勾选 `隐藏桌面图标`，观察**每一块屏幕**的表现并逐屏截图。
3. 取消勾选，再逐屏截图。
4. 应用「专注」场景，观察壁纸在各屏的铺展方式（是否只在主屏播放/是否拉伸/副屏是否黑屏）。
5. 应用「日常」场景，观察轮换壁纸在各屏表现。
6. 运行诊断，记录 `DefView` / `ListView` 句柄与 `visible`。
7. 在缩放≠100% 下检查主窗口自身：文字是否模糊、控件是否重叠或被裁切。

**预期结果（作为基线记录，不是硬性要求）**
- 桌面图标层是全局的：主屏图标隐藏后，副屏（本就不显示图标）无变化；`visible` 只有一个全局值。
- 动态壁纸可能只覆盖主屏或部分屏 —— **属已知局限**，如实记录即可。
- 主窗口在高 DPI 下应无明显文字模糊、无控件重叠/截断。

**通过判据**
- ✅ 隐藏/显示图标在多屏环境下**不崩溃、不卡死**，且能正常还原。
- ✅ 场景切换后仍可通过复选框/托盘把图标恢复回来（不产生不可逆状态）。
- ✅ 主窗口在高 DPI 下控件不重叠、文字不被裁切。
- 📋 其余表现（壁纸覆盖范围、副屏黑屏等）**如实记录进结果表的"备注"列**，作为下一阶段增强输入，不判 FAIL。
- ❌ 仅当出现崩溃、界面卡死、图标无法恢复时才判 FAIL（high）。

**证据**：`V13-display-settings.png`、逐屏截图（`V13-mon1-hidden.png` / `V13-mon2-hidden.png` …）、`V13-diag.txt`、`V13-mainwindow-dpi.png`

---

### V14 ｜ 进程异常退出后的下次启动

**前置条件**
- 程序运行中；`退出时恢复桌面图标（推荐）` 已勾选（用于对比：异常退出**不会**走恢复）。
- 已知逃生通道（§2.4）。

**操作步骤**
1. 勾选 `隐藏桌面图标`，确认图标消失，`DesiredIconsHidden=true`。
2. 任务管理器 → 详细信息，**注意区分两类进程**：
   - `DesktopSuite.exe`（主进程，无 `--wallpaper-host` 参数）
   - `DesktopSuite.exe --wallpaper-host …`（壁纸渲染子进程）
   记录两者 PID。
3. 对**主进程**右键 → 结束任务（模拟崩溃，不给任何清理机会）。
4. 观察桌面 10 秒并截图；观察托盘图标是否消失。
5. 打开 `settings.json`，记录 `DesiredIconsHidden` 与 `RendererPid`。
6. 检查渲染子进程是否仍存活（壁纸是否还在播放）。
7. 重新双击 `DesktopSuite.exe` 启动，等待最多 15 秒。
8. 观察：主窗口是否正常打开（无卡死）、复选框状态、桌面图标状态、托盘图标数量。
9. 运行诊断，记录 `last op`；检索日志中的 `Adopted existing renderer pid …`。

**预期结果**
- 步骤 4：桌面图标**保持隐藏**（异常退出没有 `OnClosed`、也没有 `SessionEnding`，**不会**触发恢复 —— 这是设计内行为，非缺陷）；托盘图标消失。
- 步骤 5：`DesiredIconsHidden` 仍为 `true`（apply 时已即时落盘）。
- 步骤 6：渲染子进程可能仍在播放壁纸（设计上壁纸独立于 GUI 存活）。
- 步骤 7–8：
  - 主窗口在 5 秒内正常打开，无"未响应"；
  - 复选框为勾选态；
  - 桌面图标仍隐藏（`AlreadyInState`，不重复发命令、不闪烁）；
  - 托盘**只有一个** DesktopSuite 图标（互斥体已随进程销毁释放，不残留）。
- 步骤 9：`last op : AlreadyInState via 无需操作 (reality=Hidden)`；若渲染进程存活，日志含 `Adopted existing renderer pid <PID>`。

**通过判据**
- ✅ 异常退出后 `settings.json` 未损坏（能被记事本正常打开、JSON 结构完整）。
- ✅ 重新启动**不卡死**（主窗口 5 秒内可交互）。
- ✅ 图标状态与 intent 一致且**可预期**；不出现"图标闪两下"。
- ✅ 只有一个托盘图标、只有一个主进程（互斥体无残留）。
- ✅ 用户始终有出路：即使不启动程序，桌面右键 → 查看 → 显示桌面图标可自救。
- ❌ 重启后出现两个托盘图标或两个主进程 → high（单实例互斥体泄漏）。
- ❌ `settings.json` 损坏导致设置全部丢回默认 → high。
- ❌ 启动卡死 >10 秒或白屏 → high。
- 📋 若渲染子进程变成孤儿且无法通过 UI「停止壁纸」回收 → 记为 medium，附 PID。

**证据**：`V14-after-kill.png`、`V14-settings-after-kill.json`、`V14-taskmgr.png`、`V14-relaunch.png`、`V14-diag.txt`

---

## 6. 建议执行顺序与耗时

| 阶段 | 用例 | 说明 | 预计耗时 |
|---|---|---|---|
| 准备 | §2 | 基线归零 + 壁纸库确认 + 日志备份 | 15 min |
| 第 1 轮（非破坏性） | V1 → V5 → V10 → V6 → V7 → V8 | 全部在同一次运行内完成 | 40 min |
| 第 2 轮（进程级） | V3 → V4 | 需要反复真正退出/启动；**V4 结束务必恢复默认勾选** | 20 min |
| 第 3 轮（会话级） | V2-A → V2-B | 各含一次重启 | 30 min |
| 第 4 轮（会话级） | V11-A → V11-B → V12 | 各含一次关机/注销 | 45 min |
| 第 5 轮（破坏性） | V9 → V14 | 结束 Explorer / 结束进程，放最后 | 25 min |
| 补充 | V13 | 需换显示器配置，可独立安排 | 20 min |

**每轮之间的复位动作**：确认 `退出时恢复桌面图标（推荐）` 已勾选、`隐藏桌面图标` 归零、开机自启按下一条用例的要求设置、日志已备份。

---

## 7. 结果记录与缺陷上报

### 7.1 结果记录表（执行时逐行填写）

| 编号 | 结论 (PASS/FAIL/BLOCKED/N.A.) | 执行人 | 时间 | 证据文件 | 备注 |
|---|---|---|---|---|---|
| V1 | | | | | |
| V2-A | | | | | |
| V2-B | | | | | |
| V3 | | | | | |
| V4 | | | | | |
| V5 | | | | | |
| V6 | | | | | |
| V7 | | | | | |
| V8 | | | | | |
| V9 | | | | | |
| V10 | | | | | |
| V11-A | | | | | |
| V11-B | | | | | |
| V12 | | | | | |
| V13 | | | | 观察项，填行为记录 |
| V14 | | | | | |

**放行门槛（建议）**：V1 / V2-A / V2-B / V3 / V11-A / V11-B / V12 **全部 PASS** 方可放行；任一 critical 缺陷未修复则不放行。

### 7.2 缺陷上报模板

```
标题：[V<编号>] <一句话现象>
严重度：critical / high / medium / low
分类：Functional / UX / Content / Performance / 其它

环境：
  Windows 版本：
  显示器/缩放：
  构建：Debug / Release，输出目录：
  开机自启：开 / 关
  RestoreIconsOnExit：true / false

复现步骤（精确到点击）：
  1.
  2.
  3.

预期：
实际：
复现率：N/N 次

证据：
  截图：
  诊断文本（== desktop icons == 全段）：
  settings.json（操作前 / 操作后）：
  日志关键行：
```

**严重度判定（对齐 QA taxonomy）**

| 级别 | 本项目对应场景 |
|---|---|
| critical | 用户拿到空桌面且无法自动恢复；`DesiredIconsHidden` 被 Unknown/退出恢复污染；崩溃；设置文件损坏 |
| high | 场景应用失败/回滚不完整；固定壁纸缺失；SessionEnding 某条路径不触发；托盘与 UI 长期不一致；阻断关机 |
| medium | 双闪；降级到 `ShowWindow`（右键菜单可能受损）；闩锁失效导致恢复跑两遍；孤儿渲染进程 |
| low | 日志 reason 文案错误；文案措辞问题；高 DPI 下轻微视觉瑕疵 |

---

## 8. 风险与局限（必须向执行人交代）

### 8.1 本手册未经预执行

本手册中**没有任何一条**用例在编写环境中被实际运行过。编写环境无真实 Windows 桌面（无 Explorer 桌面图标层、无真实关机/注销会话），因此：
- 手册中的所有"预期结果"均**从源码行为推导**（`IconHider` / `App.xaml.cs` / `MainWindow.xaml.cs` / `DesktopSceneManager` / `DesktopDiagnostics` / `TrayManager` / `AppSettings` / `StartupManager`），文案与日志字符串均**逐字核对自源码**；
- 但**未经真机验证**。若真机上文案与手册不符，请以真机为准并回报手册勘误。

### 8.2 必须真机/真实会话才能覆盖的项

| 用例 | 无法自动化/沙箱化的原因 |
|---|---|
| V1–V5、V9、V10 | 依赖真实 `SHELLDLL_DefView` / `SysListView32` 与 Explorer 的 `WM_COMMAND 0x7402` 响应 |
| V2、V11、V12 | 依赖真实登录会话的建立与销毁；`SessionEnding` 无法人为伪造 |
| V6–V8 | 依赖 mpv 实际渲染到桌面壁纸层（WorkerW），需要真实合成器 |
| V13 | 依赖真实多显示器硬件与 DPI 缩放 |
| V14 | 依赖真实进程强杀语义与互斥体生命周期 |

### 8.3 已知环境干扰因素

1. **快速启动（Fast Startup）**：Win10/11 默认开启，"关机"实际是混合关机。会话仍会结束，`SessionEnding` 仍会触发。若 V11 结果异常，请改用**重启**复测以排除快速启动干扰，并在缺陷单中注明。
2. **远程桌面（RDP）**：断开/注销语义与本地会话不同，**V11/V12 禁止在 RDP 下执行**。
3. **第三方桌面工具**：Fences、StarDock、Wallpaper Engine 等会争抢 DefView/WorkerW，可能导致 `Apply` 降级或 Unknown。执行前请**关闭同类软件**并在结果表中注明。
4. **组策略/域环境**：某些企业策略禁用桌面图标切换或锁定 HKCU Run 键，会导致 V2-B / V11-B 无法执行 —— 记为 **BLOCKED** 而非 FAIL。
5. **日志滚动**：`wallpaper.log` 有大小上限会截断。跨重启用例执行前请务必备份日志（§2.3）。
6. **杀毒软件**：可能拦截 `SendMessageTimeout` 跨进程消息或 HKCU Run 写入，导致 `Failed`。若怀疑，临时加白名单后复测。

### 8.4 P1-5 的发布态风险（重点）

`WallpaperLibrary` 已迁至项目根、由 csproj `Content Include="WallpaperLibrary\**\*"` + `CopyToOutputDirectory=PreserveNewest` 随构建输出。但：
- 这只保证 **build 输出目录**有；**若后续改用 `dotnet publish` / 打安装包 / 单文件发布，必须重新验证媒体是否随包分发**；
- V8 是这条链路的**唯一守门用例**，任何发布方式变更后都必须重跑 V8；
- 建议把 V8 的诊断输出（`== wallpaper library ==` 全段）作为**每次发布的强制留档**。

### 8.5 本轮设计上的已知取舍（非缺陷，勿上报）

| 现象 | 原因 |
|---|---|
| 点窗口 × 不退出程序 | 设计如此：最小化到托盘，让壁纸和声音控制持续可用 |
| 退出程序后壁纸仍在播放 | 设计如此：渲染进程与 GUI 解耦，刻意存活 |
| 异常强杀后图标保持隐藏 | 无 `OnClosed`/`SessionEnding` 可用；已提供桌面右键逃生通道 |
| 退出时恢复图标但复选框仍勾选 | 恢复是临时的，`DesiredIconsHidden` 意图刻意保留（V3 的核心命题） |
| 动态壁纸可能只覆盖主屏 | 多屏支持为已知待增强项（V13 观察项） |

---

**手册结束。执行中若发现步骤与真机行为不符，请连同证据一并回报，以便同步修订本手册。**

# DesktopSuite Phase 3「桌面整理」VMware 靶机自动化验证 Harness

本目录提供一套可在 **VMware Windows 10 靶机**上**无人值守执行**的自动化验证工具，用于回归验证
`validation-runbook-phase3-2026-08-05.md` 中的 **V1–V14** 用例。

- 宿主机侧编排器驱动 `vmrun` 开机 / 投放 / 逐用例执行 / 回收证据。
- guest 侧三个脚本在靶机内做图标断言、状态采集、UI 驱动。
- 全程**不调用任何 LLM API**；所有判定基于代码读取与 shell 命令。
- 2GB 内存靶机所有等待均走**轮询 + 超时**，绝不用固定 `sleep` 赌时间。

---

## 1. 文件清单

| 文件 | 角色 | 运行位置 |
|---|---|---|
| `Run-Validation.ps1` | 宿主机编排器：开机 → 等 Tools → 等交互会话 → 投放 → 逐用例执行 → 回收证据 → 输出 `summary.json` / `summary.md` | 宿主机（PowerShell 5.1+） |
| `guest/Assert-DesktopIcons.ps1` | 基石图标断言器。内嵌 C# `GstackShell` P/Invoke，复刻产品窗口链搜索与 `AreIconsVisible` 三态判定。退出码：`0=visible` / `1=hidden` / `2=unknown` / `3=blocked`（session 0 / 非 WinSta0） / `4=error` | 靶机（guest） |
| `guest/Collect-State.ps1` | 一站式状态采集：图标可见性、托盘菜单项、settings.json 字段、HKCU Run 自启项、进程存在性、日志片段。产物供编排器判读 | 靶机（guest） |
| `guest/Invoke-AppUi.ps1` | UIAutomation 驱动：内嵌 `GstackMouse` C# 鼠标模拟，含 `Set-CheckBox` / `Wait-Element` / `Invoke-TrayMenuItem` / `TrayExitKeepWallpaper` 等动作 | 靶机（guest） |

> 四个 PowerShell 文件**均为 UTF-8 with BOM 编码**。缺 BOM 时 PowerShell 5.1 对中文多字节会
> 产生级联语法错误（首报 `缺少 using 指令` 等），看似代码缺陷，实为缺 BOM。详见 §8。

---

## 2. 目录结构

```
vm-validation/
├── Run-Validation.ps1        # 宿主机编排器
├── README.md                 # 本文档
└── guest/
    ├── Assert-DesktopIcons.ps1
    ├── Collect-State.ps1
    └── Invoke-AppUi.ps1
```

运行时宿主机侧额外生成 `evidence/<RunId>/`（每用例一个子目录，含截图与采集快照），
以及 `summary.json` / `summary.md`。

---

## 3. 前置条件

1. **VMware Tools 已安装**且靶机已开机/可自动登录到桌面（`vmrun` 依赖 Tools 通信）。
2. 存在干净快照 `gstack-clean-before-v1-v14`（默认名，可用 `-SnapshotName` 覆盖）。
3. 已 `dotnet publish` 出 Windows x64 应用，目录通过 `-AppSource` 传入（编排器负责投到 `C:\gstack\app`）。
4. 跨会话用例（V2 / V11 / V12）无人值守跑完的**硬前提**是**自动登录已开启**；可在运行前手动配，或用 `-EnableAutoLogon` 由编排器临时开启（结束后建议还原）。
5. guest 脚本由编排器自动投放到 `C:\gstack\scripts`，无需手动预置。

---

## 4. 调用方式

### 4.1 参数

| 参数 | 默认值 | 说明 |
|---|---|---|
| `GuestUser` | （必填） | 靶机用户名。**禁止硬编码**，必须传入 |
| `GuestPass` | （必填） | 靶机密码。**禁止硬编码**，必须传入 |
| `GuestProfileName` | = `GuestUser` | guest 用户配置文件目录名（域账户时显式指定） |
| `VmrunPath` | `D:\Program Files\VMware\vmrun.exe` | vmrun 路径 |
| `VmxPath` | `E:\VMwar_xitongwenjian\win10\Windows 10 x64.vmx` | 靶机 vmx |
| `VmType` | `ws` | vmrun 的 `-T` 类型 |
| `SnapshotName` | `gstack-clean-before-v1-v14` | 用例间回滚用的快照 |
| `AppSource` | `''` | 宿主机上 `dotnet publish` 输出目录 |
| `GuestAppDir` | `C:\gstack\app` | 靶机 app 投放目录 |
| `GuestScriptDir` | `C:\gstack\scripts` | 靶机脚本投放目录 |
| `GuestEvidence` | `C:\gstack\evidence` | 靶机证据目录 |
| `EvidenceRoot` | `<脚本目录>\evidence` | 宿主机证据根 |
| `Cases` | `@()` | 只跑指定用例（如 `V2A,V3,V11A,V12`），省略则跑默认顺序全部 16 条 |
| `RevertBetweenCases` | 关 | 每用例前 `revertToSnapshot` 回到干净快照（慢但最干净） |
| `SkipDeploy` | 关 | 跳过 app/脚本投放（已投过时用） |
| `DryRun` | 关 | 只打印将要执行的 vmrun 命令，不操作靶机（凭据未到位时做流程演练） |
| `EnableAutoLogon` | 关 | 运行前由编排器临时开启 guest 自动登录（跨会话用例需要） |
| `KeepVmRunning` | 关 | 跑完后保留靶机运行 |
| `ToolsTimeoutSec` | 480 | 开机 → VMware Tools 就绪 |
| `SessionTimeoutSec` | 420 | Tools 就绪 → 交互桌面可读 |
| `ShutdownTimeoutSec` | 300 | 发出关机 → 靶机真下线 |
| `AppStartTimeoutSec` | 180 | 启动 app → 主进程出现 |
| `GuestCmdTimeoutSec` | 300 | 单条 guest 命令墙钟上限 |

### 4.2 示例

```powershell
# 流程演练（不碰靶机）
.\Run-Validation.ps1 -GuestUser x -GuestPass x -DryRun

# 只跑四条 P0 核心用例，每条前回滚快照
.\Run-Validation.ps1 -GuestUser tester -GuestPass 'pwd' `
    -AppSource 'D:\WorkBuddy\桌面美化\publish\win-x64' `
    -Cases V2A,V3,V11A,V12 -RevertBetweenCases

# 全量默认顺序，并临时开启自动登录以跑跨会话用例
.\Run-Validation.ps1 -GuestUser tester -GuestPass 'pwd' `
    -AppSource 'D:\WorkBuddy\桌面美化\publish\win-x64' -EnableAutoLogon
```

---

## 5. 用例矩阵（V1–V14）

自动化级别说明：**AUTO** = 编排器可独立跑完并给 PASS/FAIL；**SEMI** = 部分步骤需人工确认/补充，
其余自动；**MANUAL** = 必须由人工执行；**NA** = 观察项，非 FAIL 判据。

| 编号 | 名称 | runbook | 自动化 | 说明 / 自动化覆盖 |
|---|---|---|---|---|
| V1 | 隐藏/显示桌面图标基本可用 | §5 V1 | SEMI | 图标显隐由 `Assert-DesktopIcons` 断言；点托盘开关由 `Invoke-AppUi` 驱动 |
| V2A | 隐藏意图跨重启保留（手动启动支线） | §5 V2-A | AUTO | 预置 intent → 重启 → 轮询采样图标态；托盘退出不可用则直接重启（判据不依赖退出方式） |
| V2B | 隐藏意图跨重启保留（--background 自启支线） | §5 V2-B | AUTO | 硬断言 HKCU Run 带 `--background`；高频采样 60s 窗口抢「可见→隐藏」过渡 |
| V3 | 退出恢复且不抹掉意图 | §5 V3 | SEMI | **只能走托盘「退出（保留壁纸）」**；托盘路径不可自动化时转人工（不得用 taskkill 替代） |
| V4 | 关闭「退出恢复」后保持隐藏 | §5 V4 | SEMI | 同 V3 触发约束；收尾恢复默认避免污染后续 |
| V5 | 托盘菜单与复选框状态同步 | §5 V5 | MANUAL | 菜单/复选框视觉与状态同步需人工核对 |
| V6 | 托盘场景子菜单（日常/专注/演示） | §5 V6 | SEMI | 驱动子菜单点击并断言图标/轮换/声音；壁纸画面属视觉判据 |
| V7 | 场景切换无双闪 | §5 V7 | SEMI | 切换前后采样图标态，断言无重复闪烁 |
| V8 | 固定壁纸文件就位且可播放 | §5 V8 | SEMI | 校验壁纸库文件存在且可解码；诊断文本按 §8.4 留档 |
| V9 | Unknown 态不写 intent 且有反馈 | §5 V9 | SEMI | 断言 unknown 态不落 intent 且有用户反馈 |
| V10 | Apply 返回值全程可见 | §5 V10 | SEMI | 断言 Apply 各返回态对上层可见 |
| V11A | 真实关机 SessionEnding 恢复（前台 + 关闭自启） | §5 V11-A | AUTO | §0-C 守卫：自启必须关闭；保持运行直接重启触发 SessionEnding 恢复 |
| V11B | 真实关机 SessionEnding 恢复（--background 兜底） | §5 V11-B | AUTO | 只要求日志出现 `SystemEvents.SessionEnding`，WPF 侧缺席属预期（§0-B）；抓不到窗口期改日志断言 |
| V12 | 真实注销 SessionEnding 恢复 | §5 V12 | AUTO | 注销触发恢复，断言意图保留 |
| V13 | 多显示器 / 高 DPI 行为记录 | §5 V13 | NA | 截图无法机器判读，为观察项；建议物理机/多显示器 VM 人工执行 |
| V14 | 进程异常退出后的下次启动 | §5 V14 | AUTO | 模拟崩溃（taskkill）后下次启动应自愈；刻意模拟崩溃，不用于验 V3/V4 |

**默认执行顺序**（runbook §6：非破坏性 → 进程级 → 会话级 → 破坏性）：
`V1 → V5 → V10 → V8 → V6 → V7 → V3 → V4 → V2A → V2B → V11A → V11B → V12 → V9 → V14 → V13`

---

## 6. 三条防误报红线（runbook §0）

本 harness 在代码层强制继承：

- **A. 「×」只是最小化到托盘，不是退出。** 编排器**从不**用 `taskkill` / `WM_CLOSE` 冒充「真正退出」。
  需要真正退出的 V3/V4 只走托盘菜单（`Invoke-AppUi -Action TrayExitKeepWallpaper`）；该路径失败一律判
  `BLOCKED` 转人工，绝不降级成 `taskkill`。
- **B. `--background` 无 HWND，WPF `Application.SessionEnding` 不会到达。** V11-B 因此**只**要求日志里
  出现 `SystemEvents.SessionEnding`，WPF 侧那条缺席属预期，绝不因此判 FAIL。
- **C. 开机自启会在登录后立刻重新隐藏图标，掩盖恢复失败。** 所有「验恢复」用例执行前都会**硬断言 HKCU Run
  项状态**；V11-B 这类必须开自启的支线改用「登录早期连续采样 + 日志断言」，而不是只看一眼最终状态。

---

## 7. 结论语义与放行门槛

每个用例结论为 `PASS` / `FAIL` / `BLOCKED` / `NA`：

- **`BLOCKED`**：环境未就绪（session 0 / 非 WinSta0 拿不到桌面、自动登录未开导致卡登录界面、
  自启状态不满足 §0-C 前置、托盘退出路径不可用等）。**环境未就绪 ≠ 功能坏了**，一律报 `BLOCKED` 并给原因，
  绝不折叠成 `FAIL`，也绝不当成 `PASS`。
- **`FAIL`**：功能确实不达标且有完整证据链。
- **`NA`**：观察项（如 V13），非放行判据。

### 放行门槛（runbook §7.1）

要求 **V1 / V2-A / V2-B / V3 / V11-A / V11-B / V12** 全部 `PASS` 方可放行。
`summary.md` 末尾自动给出门槛达成情况；未跑或未 PASS 的门槛用例必须补齐（含人工执行部分）后才能放行。

---

## 8. 已知限制与维护笔记

1. **BOM 是硬要求**：四个 `.ps1` 必须 UTF-8 with BOM。缺 BOM 会在 PowerShell 5.1 下整体解析失败
   （中文多字节被 token 器误读，首错常报 `缺少 using 指令`）。修正只需加 BOM（CRLF 非必需）。
2. **session 0 / 非 WinSta0**：guest 断言器在 session 0 或服务会话下读不到桌面窗口链，会退化为
   `blocked`；这是预期兜底，需在真实交互会话（自动登录到桌面）下才有意义。
3. **跨会话用例依赖自动登录**：V2 / V11 / V12 的重启/注销后必须能自动回到桌面，否则会卡在登录界面
   导致 `BLOCKED`。务必在跑前确认自动登录（`-EnableAutoLogon` 或手动）。
4. **V3 / V4 的触发方式不可替换**：点 × 只最小化、taskkill 是异常退出（V14 语义），都会把恢复类用例
   验成假结果。托盘路径不可自动化时只能转人工。
5. **V5 / V13 必须人工**：菜单-复选框同步、多显示器/高 DPI 视觉行为无法机器判读。
6. **证据回收**：每用例在 `evidence/<RunId>/<CaseId>/` 留截图与状态快照；`summary.json` 与 `summary.md`
   汇总全部结论与门槛状态。
7. **靶机资源**：2GB 内存靶机很慢，所有超时默认值偏宽松；如在更高配靶机可酌情调小相关 `*TimeoutSec`。
8. **采集字段契约（裁决链路的基础）**：`Collect-State.ps1` 与 `Invoke-AppUi.ps1` 输出的 JSON **始终带齐编排器会读取的全部字段**（key 一定存在，值为 `$null` 仅当底层数据确实缺失，如 settings.json 不存在时 `DesiredIconsHidden` 为 `null`）。
   - 因此「字段缺失（key 不存在）」只可能发生在**采集整体失败**（如回收 JSON 超时/解析失败 → `Invoke-GuestCollect` 返回 `$null`，调用方已判 BLOCKED），而**不是**脚本在某分支漏输出字段。
   - 编排器侧用 `Get-Prop`/`Test-Prop` 区分二者：**key 不存在 → 该步骤 BLOCKED 并点名缺失字段与采集环节**；**key 存在但值为 `$false`/`$null` → 正常走 PASS/FAIL 裁决**（例如 `settings.exists=$false` 判「配置文件不存在」，`library.exists=$false` 判 FAIL）。这一区分杜绝了「真实缺陷被缺字段伪装成环境问题」的误报。
   - **维护护栏**：若改动的 guest 脚本在某个分支漏输出上面任一字段（导致 key 不存在），会让对应用例在该分支被误判 BLOCKED。新增字段时务必保证所有出口都带齐 key。

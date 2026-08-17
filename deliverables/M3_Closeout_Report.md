# M3 收官报告 · DesktopSuite 桌面整理（Fences）

> 日期：2026-08-17 ｜ 分支：master ｜ 最新提交：见文末
> 范围：M3 全部交互功能验收通过 + 收尾（双进程互斥锁加固、去快捷方式箭头、报告与清理）

---

## 一、M3 功能清单（已真机验收全绿 ✅）

| 编号 | 功能 | 验收 |
|---|---|---|
| M3.30 | 双击空白桌面隐藏/显示围栏；空闲 30s 自动隐藏；托盘双击切换 | ✅ |
| M3.31 | 各盒子图标可单独移除（不再仅限「未分类」） | ✅ |
| M3.32 | 分类删除二次确认 + 托盘「撤销删除分类」（最多 10 次撤销栈） | ✅ |
| M3.34 | 品牌图标统一为 Sleek（魔法棒星光），应用 + 托盘多尺寸清晰 | ✅ |
| M3.35/36 | 系统虚拟图标（此电脑 / 回收站 / 控制面板）按注册表 `DefaultIcon` 权威解析 | ✅ |

6 项交互验收清单全部通过，系统图标显示正确。

---

## 二、本轮收尾实现（2026-08-17）

### P1.5 双进程单实例锁 —— 核验 + 加固
- **核验结论**：代码库自初始提交即已存在 `Mutex` 单实例锁（`App.xaml.cs`）+ `ShowEvent` 唤醒机制，渲染子进程 `--wallpaper-host` 被显式豁免；`DesktopDoubleClickHook` 在 `MainWindow` 仅创建/释放一次。故「两个 GUI 实例各装 `WH_MOUSE_LL` 钩子互相抵消双击 toggle」的隐患在已发布代码中**已被防住**。
- **加固**：原锁用 `initiallyOwned=true`，若上一次实例崩溃留下「遗弃互斥体」，后续启动会误判为「已有实例」而拒绝启动。改为 `initiallyOwned=false` + 显式 `WaitOne`，并区分「活动实例」（`WaitOne(0)` 返回 false → 退出并唤醒）与「崩溃遗弃」（`AbandonedMutexException` → 接管并继续），崩溃后仍能正常重启。

### P1 去快捷方式箭头
- 新增 `ShellTweaks`：写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Icons` 值 `29` 指向随包发布的**全透明** `hide_arrow.ico`（32×32、32-bit BGRA、alpha=0）。走 `HKCU` 每用户覆盖，**无需管理员权限 / 无 UAC 弹窗**。
- **刷新策略（关键取舍）**：默认用 `SHChangeNotify(SHCNE_ASSOCCHANGED)` 轻量刷新图标缓存，**不杀 Explorer、不扰壁纸**。原因：本项目壁纸引擎把 mpv 挂在 Explorer 的 `WorkerW` 上，重启 Explorer 会销毁旧 `WorkerW` 导致壁纸短暂丢失。故自动重启 Explorer 故意不做。
- 托盘新增 **「🔄 重启资源管理器（刷新外壳）」** 手动项：供个别 Windows 版本 `SHChangeNotify` 未生效时，由用户主动强制刷新。
- 新增设置 `HideShortcutArrows`（持久化到 `settings.json`）；启动自动套用（仅当注册表与意图不一致时才刷新，正常重启用无感）。
- 托盘新增 **「🚫 隐藏快捷方式箭头」** 勾选项，实时反映开关状态。

---

## 三、已知问题 / 注意事项
- `SHChangeNotify` 在绝大多数 Win10/11 上即可刷新箭头；若个别机器不生效，点托盘 **重启资源管理器** 即可（会短暂重建桌面外壳，壁纸随之重挂，属预期）。
- `FenceLayer.cs(1231)` 的 `CS8625` 可空警告为历史既有，与本轮无关。
- `Shell Icons` 值 `29` 是系统级图标表覆盖，仅影响当前用户，不影响其他账户。

---

## 四、部署与验证状态
- 构建：`dotnet publish -c Release -r win-x64 --self-contained true` 成功（退出码 0）。
- 部署目录：`D:\WorkBuddy\ds2\`（含 `DesktopSuite.exe`、`hide_arrow.ico`、`tray_icon.ico`）。
- 代码已提交并推送 gitee / github（master，SSH）。
- 待用户真机验收：去箭头勾选后桌面快捷方式左下角箭头消失；如需彻底刷新点托盘「重启资源管理器」。

---

## 五、后续路线（建议）
- 围栏布局导入 / 导出、场景市场、壁纸 + 围栏联动主题。
- 如后续需要「全局」去箭头（影响所有用户），再评估 `HKLM` + 提权路径。

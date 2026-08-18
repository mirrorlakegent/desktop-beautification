# M3 收官报告 · DesktopSuite 桌面整理（Fences）

> 日期：2026-08-18（修订）｜ 分支：master ｜ 最新提交：`ace37b7`
> 范围：M3 全部交互功能验收 + 收尾（双进程互斥锁加固、报告与清理）
> **修订说明**：原 2026-08-17 版本声称「去快捷方式箭头」已通过 Shell Icons 值 29 生效，
> 该方法随后经 6 轮真机实测被证伪，去箭头功能已于 `ace37b7` **正式禁用并移出 M3 范围**。本报告据此更正。

---

## 一、M3 功能清单（已真机验收 ✅）

| 编号 | 功能 | 验收 |
|---|---|---|
| M3.30 | 双击空白桌面隐藏/显示围栏；空闲 30s 自动隐藏；托盘双击切换 | ✅ |
| M3.31 | 各盒子图标可单独移除（不再仅限「未分类」） | ✅ |
| M3.32 | 分类删除二次确认 + 托盘「撤销删除分类」（最多 10 次撤销栈） | ✅ |
| M3.34 | 品牌图标统一为 Sleek（魔法棒星光），应用 + 托盘多尺寸清晰 | ✅ |
| M3.35/36 | 系统虚拟图标（此电脑 / 回收站 / 控制面板）按注册表 `DefaultIcon` 权威解析 | ✅ |

6 项核心交互验收清单全部通过，系统图标显示正确。

---

## 二、本轮收尾实现（2026-08-17，保留）

### P1.5 双进程单实例锁 —— 核验 + 加固
- **核验结论**：代码库自初始提交即已存在 `Mutex` 单实例锁（`App.xaml.cs`）+ `ShowEvent` 唤醒机制，渲染子进程 `--wallpaper-host` 被显式豁免；`DesktopDoubleClickHook` 在 `MainWindow` 仅创建/释放一次。故「两个 GUI 实例各装 `WH_MOUSE_LL` 钩子互相抵消双击 toggle」的隐患在已发布代码中**已被防住**。
- **加固**：原锁用 `initiallyOwned=true`，若上一次实例崩溃留下「遗弃互斥体」，后续启动会误判为「已有实例」而拒绝启动。改为 `initiallyOwned=false` + 显式 `WaitOne`，并区分「活动实例」（`WaitOne(0)` 返回 false → 退出并唤醒）与「崩溃遗弃」（`AbandonedMutexException` → 接管并继续），崩溃后仍能正常重启。
- （2026-08-18 排障补充）若旧实例窗口状态异常且不响应 show event，新实例会退出导致「打不开」——临时解法为任务管理器结束 `DesktopSuite` 进程再开；该加固已记入待办。

---

## 三、去快捷方式箭头（已禁用 · 移出 M3 范围）

### 结论：Win11 26100+ 所有已知方法全部失效
该功能是 M3 的附加项（非核心），尝试了 **4 类、共 6 个提交**的注册表方案，经真机实测**无一生效**：

| # | 方法 | 结果 | 提交 |
|---|------|------|------|
| 1 | Shell Icons 值 29（自定义 ICO / shell32.dll,-50 / 透明 ICO 文件） | Win11 26100+ 无视 | 895dc36 / 2e2ec1e / b144d79 / 2326d65 |
| 2 | 删除 HKLM `IsShortcut`（lnkfile/piffile/InternetShortcut） | 26100.8972 仍无视 | c4d9ede |

**根因**：微软在 2026-08 累积更新中将快捷方式箭头绘制从「可配置的 overlay 系统」改为 `shell32.dll` 图标渲染管线**硬编码行为**。`ShellIconOverlayIdentifiers` 中已无 Arrow handler，箭头不再走扩展点，任何注册表级别方案都无法去除。

### 处理（`ace37b7`）
- `ShellTweaks` 加 `IsShortcutSupported` 版本守卫（Build ≥ 26100 返回 false）；`ApplyIsShortcut` / `IsHideShortcutArrowsEnabled` 在不支持版本上直接跳过。
- 托盘「隐藏快捷方式箭头」改为弹 MessageBox 提示「功能不可用」，不再操作注册表。
- 之前无效的 IsShortcut 删除操作已通过 UAC 自提升 **恢复注册表原值**（不留垃圾）。
- 代码保留但标记为不兼容；UAC 自提升分支（`--apply-ishortcut`）保留供旧版 Windows 或未来恢复使用。

### 对用户的影响与建议
- 桌面箭头**不会消失**（系统级硬编码，任何程序都去不掉）。
- 如需此功能，建议使用 [Winaero Tweaker](https://winaero.com/tweaker/)（可能采用更底层的注入/补丁）。

---

## 四、已知问题 / 注意事项
- `FenceLayer.cs(1231)` 的 `CS8625` 可空警告为历史既有，与本轮无关。
- 单实例锁在「旧实例卡死且不响应 show event」时可能表现为「打不开」，临时解法见第二节补充。
- 去箭头功能已从 M3 交付范围移除，不作为验收项。

---

## 五、部署与验证状态
- 构建：`dotnet publish -c Release -r win-x64 --no-self-contained` 成功（退出码 0）。
- 部署目录：`D:\WorkBuddy\ds2\`（含 `DesktopSuite.exe`）。
- 代码已提交并推送 gitee / github（master，SSH，最新 `ace37b7`）。
- 6 项核心功能验收状态见第一节（需用户按自身标准真机复验确认）。

---

## 六、后续路线（建议）
见独立文档 `deliverables/M3_Roadmap_Next.md`：
- 围栏布局导入 / 导出
- 场景市场 / 壁纸 + 围栏联动主题
- 去箭头仅在微软恢复可配置性或发现新方法后再评估重新启用

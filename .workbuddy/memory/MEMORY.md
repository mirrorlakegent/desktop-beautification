# 桌面美化项目 — 长期记忆

## 去箭头功能（HideShortcutArrows）机制结论
- **Win11 Build 26100+（含 26200 预览版）彻底封死所有已知去箭头路径**——微软在 2026-08 累积更新中
  将快捷方式箭头绘制从可配置 overlay 系统改为 shell32.dll 图标渲染管线**硬编码行为**。
  以下方法经真机实测全部无效：
  - Shell Icons 值29（自定义 ICO / shell32.dll,-50 / 透明 ICO 文件）
  - 删除 HKLM IsShortcut（lnkfile/piffile/InternetShortcut）
  - ShellIconOverlayIdentifiers 中无 Arrow handler（不是通过扩展点画的）
- **代码现状**：`ace37b7` 已加 `IsShortcutSupported` 版本守卫（Build >= 26100 返回 false），
  托盘切换项改为弹 MessageBox 提示"功能不可用"，不再操作注册表。
- **注册表已恢复**：三个 IsShortcut 值均已还原（之前无效的删除操作已清理）。
- 若未来发现新方法或微软恢复可配置性，代码保留可重新启用。否则建议用户用 Winaero Tweaker。
- **应用内 UAC 自提升模式**仍保留（`--apply-ishortcut on|off`），供旧版本 Windows 或未来恢复时使用；
  该分支在 `App.xaml.cs` 单实例 Mutex **之前**处理（绕过 Mutex），写完即 `Environment.Exit`。

## 双远程推送坑
- gitee 偶发 SSH 瞬时空跑导致 `git push` 报 "Everything up-to-date" 而实际未推；发现远程落后时
  直接重试即可（fast-forward，无需 force）。核对用 `git ls-remote <remote> master`。

## M3 Fences 已知 bug 状态（已解决，勿再当待办）
- `_iconCache` 空值缓存阻断渲染：已在 `b4aa45d`（2026-08-15）修复——GetFileIcon 失败时不缓存 null。
- 删除仅限 uncategorized：已在 `d560c2f` 修复——`OnContextCommand` 的 id>=4000 分支解码任意 boxIndex，
  `RemoveItemFromBox` 对全盒子通用。当前两个"旧已知 bug"均不存在，无需再排期。

## M3 收官报告已修正（勿再当待办）
- `deliverables/M3_Closeout_Report.md` 已于 `4e47dc0` 重写——去箭头标注为"Win11 26100+ 已知不可用、已禁用"，
  并补 08-18 单实例锁排障说明。旧的"通过 Shell Icons 值29 生效"失实声明已清除。
- 6 项核心交互功能（M3.30-36）报告标"已验收"，但用户要求真机复验为准；勿擅自宣称"已修复"。

## M4 进展
- **M4-A 布局导入/导出**：已交付（`67e857b`），托盘「📁 布局」子菜单 + FenceLayer.ApplyLayout。
- **M4-B 外观定制**：代码完成·**待真机验证**。外观设置持久化在 `AppSettings`（7 属性），经
  `FenceAppearance` DTO 映射到 `FenceLayer.SetAppearance`；圆角命中区用 `CreateRoundRectRgn` 对齐；
  毛玻璃（实验·默认关）用截屏+盒式模糊缓存。WinForms 弹窗 `FenceAppearanceForm` + 托盘「🎨 外观…」。

## 部署与启动
- **部署目标（canonical）= `D:\WorkBuddy\ds2\DesktopSuite.exe`**，自包含单文件发布：
  `dotnet publish -c Release -r win-x64 -p:SelfContained=true -p:PublishSingleFile=true -p:PublishReadyToRun=true -o "D:\WorkBuddy\ds2"`
- **启动器**：`go.bat` / `桌面整理一键启动.bat` / `启动诊断.bat` 均已指向 ds2（旧 `bin\x64\Release\self-contained`
  目录已不存在，原 bat 失效，已修正）。
- **agent 的 Git Bash 会话无法常驻 GUI 进程**：之前桌面运行实例是用户在桌面手动启动的。构建发布后如需
  重启，让用户在桌面双击 `ds2\DesktopSuite.exe` 或运行 `go.bat`（不要指望 bash 启动能 ALIVE）。
- **单实例锁**：关窗口=隐藏到托盘（进程不退出）。残留进程占 mutex 会导致"打不开"——先结束所有
  DesktopSuite 进程再开；必要时改"发 show event 超时无响应则强杀旧实例"。
- **构建解锁 DLL**：发布前先 `taskkill /f /im DesktopSuite.exe`，否则 MSB3027 锁文件失败。

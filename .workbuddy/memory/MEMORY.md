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
- **M4-B 外观定制**：代码完成·**经 15 轮真机修复（v1→v15）**。外观设置持久化在 `AppSettings`（8 属性），经
  `FenceAppearance` DTO 映射到 `FenceLayer.SetAppearance`；圆角命中区用 `CreateRoundRectRgn` 对齐；
  毛玻璃（实验·默认关）用截屏+盒式模糊缓存。WinForms 弹窗 `FenceAppearanceForm` + 托盘「🎨 外观…」。
  - **v9 关键修复**：settings.json 脏数据（BodyOpacity=0）导致围栏不可见；已在
    `AppSettings.Load()` 和 `FenceLayer.SetAppearance()` 加 `Math.Clamp` 防御（后于 v11 移除下限）。
  - **v10-v13**: 发现 GDI+ 低 alpha SolidBrush 在 Format32bppArgb 上渲染异常（暗色→白色）；
    v11 ColorMatrix/v12 ApplyBodyAlpha 后处理均失败——v12b 移到 Graphics 块外仍失败；
    **v13 FillBodyPixels（LockBits 预填充，GDI+ 前写入像素）有改善但低透明度仍有浅白残留**。
  - **v14 失败（66d9daf）**：新增 ClearBodyPixels 在 Graphics.Dispose() 后**无条件覆盖所有主体像素为 (0,0,0,1)**
    ——抹掉了 FillBodyPixels 写入的正确 alpha，导致**无论滑块多少都全白**。设计错误，已删除。
  - **v15 最终方案（dbcb8e5）**：删除 ClearBodyPixels，恢复 v13 正确管线：
    `FillBodyPixels`(LockBits 预填充，alpha=0 时写 alpha=1 绕过 GetHbitmap 缺陷)
    → GDI+ 绘制标题/边框/图标/文字 → PremultiplyAlpha → DWM。
    **关键教训：后处理清理不得无条件覆盖前置步骤已写入的正确数据**。
  - **v16（妥协方案）**：用户复验 v15 透明度=0 仍全白 → 最小 alpha 1→20 + `borderA<10` 跳过边框。
    发布 ds2（exe Aug 25 09:50），仅绕过非根治。
  - **v17（根治，✅ 真机验证通过）**：根因 = `FenceLayer.cs:UpdateVisual` 的
    `bmp.GetHbitmap()` 参数less 重载不保留 alpha 通道（合成到背景、丢 alpha）→ 极低 alpha 渲染白。
    改动：`NativeMethods` 新增 `CreateDIBSection`+40字节`BITMAPINFO`；`FenceLayer` 新增
    `CreateAlphaDib`/`CopyPremultipliedToDib` 两个 static helper，用 CreateDIBSection + 拷贝
    premultiplied scan0 替代 GetHbitmap（精确保留 alpha）；`FillBodyPixels` 去掉最小 alpha=20 限制
    （允许真 0 → `(0,0,0,0)` 真透明）；边框跳过保留（防 GDI+ 低 alpha 画笔垃圾）。
    发布 ds2（exe Aug 25 10:19），提交 `88dd23d` 推双远程。**用户复验确认：滑块=0 主体真正透出壁纸
    （可点穿到桌面图标），中段/255 正常，图标文字不被压暗** → **M4-B 透明度问题彻底结案**（首个真机验证通过的根因修复）。
    **v18 回归①（9fee49c，已验证通过）**：审计其余外观属性——HeaderOpacity/FrostOpacity 走 ColorMatrix 安全路径
    无需改代码；边框阈值 10→16 加固。用户 5 张截图真机复验全部通过（BodyOpacity 15 边界、HeaderOpacity
    0/10/30、毛玻璃开启均正常）。**M4-B 全部 8 个外观属性零回归，彻底结案**。
  - **v19-v21 毛玻璃迭代**：v19 SystemEvents 壁纸刷新（失败，Win11 不可靠）；v20 消息专用窗口
    HWND_MESSAGE 替代（待验证）；v21 FrostOpacity Math.Max(,20) 最小值（失败，三条全不通过）。
  - **v22 毛玻璃（SW_HIDE 失败）**：根因定位正确（CopyFromScreen 自捕获反馈循环），
    但修复方案 ShowWindow(SW_HIDE) 对 WS_EX_LAYERED 窗口无效——DWM 缓存最后 ULW 帧，
    SW_HIDE 不清除缓存。用户复验：FrostOpacity=0 仍全白、四盒不一致。
  - **v23 毛玻璃（PushTransparentFrame 失败）**：方案方向正确（ULW 全透明帧替代 SW_HIDE），
    但 `new Bitmap()` 未零初始化 → 垃圾 alpha 像素 → 窗口未真正透明 → 仍自捕获。
  - **v24 毛玻璃（失败）**：修复 Bitmap 零初始化（`Graphics.Clear(Transparent)`）；
    用户复验仍不行——证明 PushTransparentFrame 方向在本环境不可行（DWM 不可靠处理 ULW 透明帧）。
  - **v25 毛玻璃（✅ 不再全白，❌ 有残留）**：双管齐下：
    ① `FillBodyPixels` 在 frosted 模式填充 alpha=0（透明），消除"先填深色再盖"的 GDI+ 合成依赖；
    ② `EnsureFrostCapture` 用 `SetWindowPos(-99999,-99999)` 物理移出屏幕替代 PushTransparentFrame。
    用户复验：四盒均显示毛玻璃效果（不再全白！），但捕获到 DWM 缓存残留图像。提交 `dee9b97`。
  - **v25b 毛玻璃（失败，更差）**：Sleep 100→200ms + `RedrawWindow(RDW_UPDATENOW)`。
    用户复验"还不如上一版本"——RedrawWindow 触发额外重绘反成干扰。提交 `2678d53`。
  - **v25c（未复验即弃）**：`SW_MINIMIZE`。预判失效——本窗口是 WorkerW **子窗口**，
    无标准最小化行为，`SW_MINIMIZE` 大概率不生效。提交 `85e95cd`。
  - **v25d 毛玻璃（✅ 架构性换路线，待复验）**：**彻底放弃截屏**。新增 `LoadWallpaperFrost()`
    用 `IDesktopWallpaper` COM（项目本就有此接口）直接读壁纸文件，按 `DESKTOP_WALLPAPER_POSITION`
    （FILL/FIT/STRETCH/CENTER/TILE）换算缩放 + 虚拟屏幕位置映射裁剪，再三重盒式模糊。
    **零窗口操作 / 零 DWM 依赖 / 零时序竞争 / 零反馈循环**。
    取舍：不含桌面图标（对毛玻璃观感更贴合真实——真实毛玻璃模糊的是背景本身而非其上物体）。
    截屏路径降级为 fallback 安全网。新增虚拟屏幕常量 SM_[XY]VIRTUALSCREEN / SM_C[XY]VIRTUALSCREEN。
    **壁纸更换刷新链路已验证完整**：`WM_SETTINGCHANGE` → `PostMessage(WM_FROST_REFRESH)`
    → `InvalidateFrost()+UpdateVisual()` → 重新读新壁纸文件。提交 `2c3aa09`。
  - **关键教训（毛玻璃）**：当窗口是桌面 WorkerW 子窗口时，任何"隐藏自己再截屏"的方案
    都在与 DWM 缓存搏斗，不可能可靠。正确做法是**不截屏**——直接从数据源（壁纸文件）取内容。
  - **WinForms 高 DPI 弹窗经验**（已固化 skill `winforms-hidpi-layout`）：
    Y 光标按控件真实 `Bottom` 推进、Label 用 `TextRenderer`/GDI 引擎、`AutoScaleMode=Dpi`。

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

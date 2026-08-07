# 桌面美化套件 — 打包交付 / 测试策略 / 回滚预案

**技术栈前提**：C# .NET 8 + WPF（核心）+ WebView2（渲染层）+ mpv.exe（视频硬解外挂进程）
**架构前提**：三进程隔离 / 动态壁纸集成外部 Lively（不捆绑）/ 桌面整理为虚拟视图（不动文件）/ AppBar 默认不注册 / 不改系统主题 / 全程不提权
**定位**：个人自用，非商业分发

---

## 1. WPF 单文件 EXE 打包方案

### 1.1 发布配置（推荐基线）

```xml
<PropertyGroup>
  <OutputType>WinExe</OutputType>
  <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
  <UseWPF>true</UseWPF>
  <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  <SelfContained>true</SelfContained>
  <PublishSingleFile>true</PublishSingleFile>
  <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
  <EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
  <PublishReadyToRun>true</PublishReadyToRun>
  <InvariantGlobalization>true</InvariantGlobalization>
  <SatelliteResourceLanguages>zh-Hans</SatelliteResourceLanguages>
  <DebugType>embedded</DebugType>
  <!-- 不要开 PublishTrimmed -->
</PropertyGroup>
```

`dotnet publish -c Release -r win-x64`

### 1.2 关键取舍

| 选项 | 结论 | 理由 |
|---|---|---|
| **自包含 vs 框架依赖** | **自包含** | 框架依赖单文件仅 1–3MB，但要求目标机装 .NET 8 Desktop Runtime。自用机重装系统后第一件事不该是装运行时。自包含 = 双击即用 |
| **PublishTrimmed（裁剪）** | **不用** | .NET 8/9 官方**不支持 WPF 裁剪**（XAML 反射 + BAML 解析全靠动态类型）。强开会得到运行时 `XamlParseException`，且报 NETSDK 警告。别指望靠裁剪减体积 |
| **ReadyToRun** | **开** | 体积 +30~40%，冷启动快 40~50%。常驻软件启动体验优先 |
| **EnableCompressionInSingleFile** | **开** | 150MB → 60~70MB。代价：首次运行解压到临时目录，冷启动 +0.5~1s（仅首次） |
| **InvariantGlobalization** | **开** | 去掉 ICU，省 ~28MB。代价：不支持区域性排序/比较，本项目用不到 |
| **UPX 压缩** | **禁止** | 加剧杀软误报，收益远小于代价 |
| **DOTNET_BUNDLE_EXTRACT_BASE_DIR** | 指向程序目录 `./.cache` | 默认解压到 `%TEMP%`，清理工具会误删导致下次启动慢；指到自身目录同时提升便携性 |

### 1.3 体积预期（win-x64）

| 产物 | 体积 |
|---|---|
| 自包含 + R2R + 压缩 + InvariantGlobalization 主 EXE | **~55–70 MB** |
| mpv.exe（外挂视频解码进程） | ~45 MB |
| 内置主题/壁纸示例资源 | ~20 MB（自控） |
| **便携包 ZIP 总计** | **~110–130 MB** |
| WebView2 Runtime | 不打包（Win11 内置，Win10 随 Edge 已装；缺失时引导安装） |

### 1.4 便携版 vs 安装包 —— 推荐便携版

**推荐：便携版 ZIP，内含单文件 EXE，不做 NSIS/MSI 安装包。**

理由：① 个人自用，无需注册表卸载项、无需开始菜单快捷方式；② 安装包在无签名前提下会**同时触发** SmartScreen + UAC 提权弹窗，摩擦翻倍；③ 桌面美化软件出问题时最直接的处置是「删目录跑路」，安装包反而留残留；④ 换机/重装只需拷目录。

代价：无自动更新、无卸载器（需自建"卸载"按钮做还原 + 自删除）。可接受。

### 1.5 配置与资源存放 —— 混合策略

启动时检测程序目录是否存在 `portable.flag`：

| | 便携模式（有 flag） | 常规模式（无 flag） |
|---|---|---|
| 配置 JSON | `./data/config/` | `%APPDATA%\DeskKit\config\` |
| **还原备份** | `./data/restore/` **+ 镜像一份到 `%LOCALAPPDATA%`** | `%LOCALAPPDATA%\DeskKit\restore\` **+ 镜像一份到程序目录** |
| 主题/壁纸库 | `./data/library/` | `%LOCALAPPDATA%\DeskKit\library\` |
| 日志 | `./data/logs/` | `%LOCALAPPDATA%\DeskKit\logs\` |
| 缓存/解压 | `./.cache/` | `%LOCALAPPDATA%\DeskKit\cache\` |

**还原备份双写是硬性要求**——两个位置任一存活即可恢复。默认发**便携模式**（ZIP 内预置 `portable.flag`）。

**不要**把配置写 `Program Files`（需提权）、**不要**写注册表存业务配置（只有开机自启一个 Run 键）。

### 1.6 其他两栈（一句话）

- **Electron**：electron-builder `target: portable` 可出单 EXE，但空包 150MB 起、常驻内存 200MB+ 打底，对"常驻美化工具"是硬伤。
- **Tauri**：产物 3–10MB 最优，但强依赖 WebView2 且 Rust 学习曲线陡；本项目已排除。

---

## 2. 无签名 EXE 的现实问题

### 2.1 SmartScreen

**必然触发**「Windows 已保护你的电脑」蓝屏提示（首次运行、低流行度、无签名）。

| 手段 | 有效性 | 说明 |
|---|---|---|
| 自签名证书 | ❌ **无效** | SmartScreen 只认**证书的累积信誉**。自签名证书信誉为零，效果与不签名完全一致。除非把根证书塞进"受信任的根证书颁发机构"——自用机可行但收益仅限"属性里显示已签名"，**不解除 SmartScreen** |
| OV/EV 证书 | ❌ 不适用 | OV 需数月积累信誉；EV 立即通过但 ¥2000+/年。个人项目不划算 |
| **去除 MOTW** | ✅ **实际有效** | 拦截根因是下载文件带 Zone.Identifier 备用数据流。右键属性 → 勾选"解除锁定"，或 `Unblock-File .\DeskKit.exe`。**自己编译的产物本来就没有 MOTW，压根不弹** |
| 本地编译 | ✅ 最优 | 自用场景直接本机 `dotnet publish`，全程无 SmartScreen |

**结论**：自用无痛（本地编译无 MOTW）；分享给朋友时附一句"属性→解除锁定"即可。**不要买证书。**

### 2.2 杀软误报 —— 风险已大幅下降

架构调整后**我们自己的进程不再有窗口注入、跨进程内存写入、uxtheme 补丁**，最大误报源已消除。剩余风险点及应对：

| 残留风险行为 | 误报权重 | 应对 |
|---|---|---|
| 单文件自解压到磁盘再加载 | 中 | 设 `DOTNET_BUNDLE_EXTRACT_BASE_DIR` 到程序目录（`%TEMP%` 落 PE 更可疑）；或权衡后关闭压缩 |
| 启动子进程 mpv.exe | 低 | 用完整路径 + 显式参数，不走 `cmd /c`、不拼接命令行字符串 |
| 写 `HKCU\...\Run` 开机自启 | 中 | 改用**任务计划程序**或让用户手动放启动文件夹；至少做成默认关闭、用户主动勾选 |
| `SetWindowsHookEx` 全局钩子 | **高** | **禁止使用**。热键用 `RegisterHotKey`，窗口事件用 `SetWinEventHook`（out-of-context，不注入 DLL） |
| 无签名 + 低流行度新 PE | 高 | Defender ML 常报 `Wacatac.B!ml` / `Bearfoos`。**每次重新编译哈希变化会重复触发** |

**实操建议**：① 开发目录加 Defender 排除项（`Add-MpPreference -ExclusionPath`）；② 误报时向 Microsoft Security Intelligence 提交样本（免费，通常 1–3 天加白）；③ **不加壳、不混淆、不 UPX**；④ 用标准 SDK 发布，不用第三方 EXE 合并工具（Costura/ILMerge 是误报重灾区）。

---

## 3. 测试矩阵

> 优先级：**P0 = 必测（每次发版）** / **P1 = 应测（功能变更时）** / **P2 = 可选（有余力）**
> 所有 HWND 相关用例的通用验收前提：**程序内不缓存任何 HWND**，`RegisterWindowMessage("TaskbarCreated")` 已注册。

### 3.1 打包与首次运行（PKG）

| ID | 优先级 | 用例 | 步骤 | 通过标准 |
|---|---|---|---|---|
| PKG-01 | P0 | 纯净系统双击运行 | 全新 Win11 VM（未装 .NET / VC++），解压 ZIP，双击 EXE | 3s 内出现托盘图标，无任何缺失 DLL 弹窗 |
| PKG-02 | P0 | WebView2 缺失降级 | Win10 LTSC（无 Edge Chromium）运行 | 不崩溃；弹出引导对话框提供 WebView2 下载链接；小组件模块标记"不可用"，其余功能正常 |
| PKG-03 | P0 | 中文/空格路径 | 解压到 `D:\我的 软件\桌面美化 v1\` 运行 | 全功能正常，日志路径无乱码 |
| PKG-04 | P1 | 便携性验证 | 整目录拷到 U 盘，换台机器运行 | 配置随行，不读写原机 AppData |
| PKG-05 | P1 | 只读目录运行 | 放到只读目录运行 | 优雅报错提示，不静默崩溃 |
| PKG-06 | P1 | 双开防护 | 已运行时再次双击 | 唤起已有实例主窗口，不产生第二套子进程 |
| PKG-07 | P2 | 冷/热启动耗时 | 秒表计时 ×5 取均值 | 冷启动 <3s，热启动 <1.5s |

### 3.2 显示器与 DPI（DISP）

| ID | 优先级 | 用例 | 步骤 | 通过标准 |
|---|---|---|---|---|
| DISP-01 | P0 | 双屏不同 DPI | 主屏 2560×1440@150%，副屏 1920×1080@100% | Dock 与小组件在各自屏幕上物理尺寸一致，无模糊、无错位；字体清晰 |
| DISP-02 | P0 | **显示器热插拔** | 程序运行中拔掉副屏 HDMI，等 10s，再插回 | Dock/小组件回到**原设备**原位置；无窗口跑到屏幕外；无崩溃 |
| DISP-03 | P0 | **设备路径持久化** | 配置副屏 Dock → 退出程序 → 交换两根线缆顺序 → 启动 | 依据 `GetMonitorDevicePathAt` 匹配，Dock 仍在物理上同一台显示器（**不得**按索引 0/1 定位） |
| DISP-04 | P0 | 切换主副屏 | 设置中把副屏设为主显示器 | 壁纸/Dock/图标层全部正确重排，无残影 |
| DISP-05 | P1 | 运行中改缩放 | 从 100% 改到 200%（不注销） | 收到 `WM_DPICHANGED`，UI 重新布局，不出现半糊半清 |
| DISP-06 | P1 | 三屏混排 | 4K@200% + 2K@125% + 1080p@100% 横竖混排 | 各屏独立正确；跨屏拖拽小组件后落点准确 |
| DISP-07 | P1 | 分辨率切换 | 游戏内切 1080p 全屏再退出 | 桌面元素恢复原布局，图标位置不错乱 |
| DISP-08 | P2 | 屏幕旋转 | 副屏改纵向 | Dock 贴边逻辑正确 |
| DISP-09 | P2 | 单屏基线 | 仅 1 台 1080p@100% | 全功能通过（回归基线） |

### 3.3 系统状态（SYS）

| ID | 优先级 | 用例 | 步骤 | 通过标准 |
|---|---|---|---|---|
| SYS-01 | P0 | **锁屏 → 解锁** | Win+L，等 30s，解锁 | 所有元素正常显示；动态壁纸在锁屏期间已暂停（GPU 归零） |
| SYS-02 | P0 | **休眠/睡眠唤醒** | S3 睡眠 5 分钟后唤醒 | 无黑屏、无壁纸丢失、无进程僵死；mpv 进程正常恢复播放 |
| SYS-03 | P0 | 亮/暗模式切换 | 设置中切换应用模式 | 收到 `WM_SETTINGCHANGE`，UI 主题跟随，无需重启 |
| SYS-04 | P1 | 快速用户切换 | 切到另一账户再切回 | 两账户各自实例互不干扰；切回后功能完整 |
| SYS-05 | P1 | 远程桌面连接 | RDP 接入（会话分辨率变化） | 不崩溃；建议 RDP 会话下自动降级为静态壁纸 |
| SYS-06 | P1 | 系统更新后 | 打累积更新并重启 | 自启正常，配置未丢失 |
| SYS-07 | P2 | 多用户并发 | A/B 账户同时登录 | 无端口/命名管道冲突（IPC 名称须带会话 ID） |
| SYS-08 | P2 | 电源计划切换 | 切到"节能" | 动态壁纸自动降帧或暂停 |

### 3.4 应用交互（APP）

| ID | 优先级 | 用例 | 步骤 | 通过标准 |
|---|---|---|---|---|
| APP-01 | P0 | **全屏独占游戏** | 启动全屏游戏 | 2s 内动态壁纸暂停（GPU 占用归零）；退出游戏 3s 内恢复 |
| APP-02 | P0 | 无边框全屏视频 | 全屏播 YouTube / PotPlayer | 同上，暂停生效 |
| APP-03 | P0 | **Win+D 显示桌面** | 按 Win+D 再按一次 | 图标层与 Dock 显示/隐藏跟随，无残留浮层遮挡 |
| APP-04 | P0 | 虚拟桌面切换 | Win+Ctrl+← / → 来回切 | Dock 与小组件按配置（全桌面显示 / 仅当前）表现一致，不重影 |
| APP-05 | P1 | 任务视图 | Win+Tab | 我们的窗口不出现在任务视图缩略图中（须设 `WS_EX_TOOLWINDOW`） |
| APP-06 | P1 | Alt+Tab 排除 | Alt+Tab 循环 | Dock/图标层/壁纸窗口均不参与 Alt+Tab |
| APP-07 | P1 | 与其他美化软件共存 | 同时运行 TranslucentTB / Rainmeter | 无 Z-order 抢占死循环，无 CPU 飙升 |
| APP-08 | P2 | 窗口最大化避让 | 最大化任意窗口 | 未注册 AppBar 时窗口应覆盖 Dock 全部区域（预期行为） |

### 3.5 外部依赖失效（DEP）—— 新增类别

| ID | 优先级 | 用例 | 步骤 | 通过标准 |
|---|---|---|---|---|
| DEP-01 | P0 | **Lively 未安装** | 干净系统，开启动态壁纸功能 | 不崩溃；UI 明示"需安装 Lively Wallpaper"并给官方链接；**自动降级为静态壁纸** |
| DEP-02 | P0 | **Lively 进程被杀** | 运行中在任务管理器结束 Lively | 我方主程序存活；检测到 IPC 断连后 5s 内降级为静态壁纸并提示；不反复重启对方 |
| DEP-03 | P0 | **Lively 版本不匹配** | 装一个旧版/不兼容版本 | 版本探测失败时禁用联动而非盲调；给出明确提示 |
| DEP-04 | P1 | Lively 运行中被卸载 | 运行时卸载 Lively | 优雅降级，无异常弹窗轰炸 |
| DEP-05 | P0 | **mpv.exe 崩溃** | 结束 mpv 进程 | 壁纸渲染进程捕获子进程退出，最多重试 2 次后降级静态；主程序无感 |
| DEP-06 | P1 | mpv 文件缺失 | 删除 mpv.exe | 启动时校验依赖清单，缺失则禁用视频壁纸并提示，不崩 |
| DEP-07 | P1 | 损坏的媒体文件 | 用 0 字节 / 改名的假 mp4 做壁纸 | 报错提示，回退上一张壁纸 |
| DEP-08 | P1 | WebView2 运行时中途升级 | 小组件运行时 Edge 自动更新 | 小组件宿主自动重载，不白屏 |

### 3.6 桌面整理与图标层（ICON）

| ID | 优先级 | 用例 | 步骤 | 通过标准 |
|---|---|---|---|---|
| ICON-01 | P0 | **零文件改动验证** | 开启整理，创建分组，拖 20 个图标进去。用 `Get-ChildItem $env:USERPROFILE\Desktop` 对比前后 | 桌面**真实文件路径、名称、时间戳、数量完全一致**（虚拟视图铁律） |
| ICON-02 | P0 | **OneDrive 重定向桌面** | 桌面已重定向到 OneDrive，执行 ICON-01 | 同上；OneDrive 同步队列**无任何删除/新建事件** |
| ICON-03 | P0 | **原生图标隐藏/恢复** | 隐藏 DefView → 立即结束我方主进程 | 见 REC-01（此为最高危路径） |
| ICON-04 | P0 | 桌面文件增删同步 | 程序运行中在桌面新建/删除/重命名文件 | 自绘图标层 2s 内同步（`SHChangeNotify` 监听），无幽灵图标 |
| ICON-05 | P1 | 图标双击/右键 | 双击程序、右键调出原生上下文菜单 | 行为与原生桌面一致；右键菜单来自真实 Shell 上下文菜单 |
| ICON-06 | P1 | 拖拽外部文件 | 从资源管理器拖文件到自绘桌面 | 行为与原生一致（复制/移动语义正确） |
| ICON-07 | P1 | 图标位置持久化 | 排布图标 → 重启程序 | 位置完全恢复 |
| ICON-08 | P1 | 大量图标压测 | 桌面放 300 个文件 | 图标层渲染 <1s，滚动流畅，内存增长 <50MB |
| ICON-09 | P2 | 超长文件名/特殊字符 | emoji、260 字符路径 | 正确显示不截断异常 |

### 3.7 崩溃与恢复（REC）—— 三进程模型

| ID | 优先级 | 用例 | 步骤 | 通过标准 |
|---|---|---|---|---|
| REC-01 | **P0** | **explorer.exe 重启** | 任务管理器 → 重启"Windows 资源管理器" | 收到 `TaskbarCreated` 广播后 **5s 内**全模块重建：图标层恢复、Dock 恢复、壁纸恢复；**无任何缓存 HWND 被复用**；重复 5 次仍稳定 |
| REC-02 | **P0** | **主进程被强杀（图标层激活中）** | 隐藏原生图标后 `taskkill /F` 主进程 | 守护进程 3s 内恢复 DefView 可见；**且**用户手动重启 explorer 也必然恢复（DefView 隐藏非持久化）。绝不允许"桌面图标永久消失" |
| REC-03 | **P0** | 壁纸渲染进程崩溃 | `taskkill /F` 壁纸子进程 | 主程序存活，10s 内自动拉起（最多 3 次），失败后降级静态壁纸 |
| REC-04 | **P0** | 小组件宿主崩溃 | `taskkill /F` WebView2 宿主 | 主程序与壁纸不受影响；小组件区域显示"已停止，点击重载" |
| REC-05 | **P0** | **孤儿进程防护** | `taskkill /F` 主进程 | 所有子进程（渲染器/宿主/mpv）随之退出，**任务管理器无残留**（Job Object + `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`） |
| REC-06 | P0 | 启动即崩自愈 | 人为构造启动崩溃，连续启动 3 次 | 第 4 次自动进入安全模式（仅设置界面 + 还原按钮） |
| REC-07 | P0 | **一键还原有效性** | 应用全部美化 → 点"恢复默认" | 壁纸、图标可见性、图标位置、自启项全部回到基线；无需重启 |
| REC-08 | P0 | **离线还原脚本** | 删除/损坏主 EXE，运行 `restore.cmd` | 不依赖任何我方二进制即可完成还原 |
| REC-09 | P1 | 断电/蓝屏模拟 | VM 直接强制断电 | 重启后配置文件不损坏（原子写：临时文件 + rename） |
| REC-10 | P1 | 备份文件损坏 | 手工破坏 restore 主备份 | 自动回退到镜像备份；两者皆坏则回退到出厂基线 |
| REC-11 | P1 | AppBar 残留自愈（高级选项） | 开启工作区预留后强杀进程 | 下次启动或 `--repair` 自动 `ABM_REMOVE` 清理残留避让区 |
| REC-12 | P2 | 磁盘写满 | 填满磁盘后操作 | 明确报错，不损坏既有配置 |

### 3.8 性能与泄漏（PERF）—— 硬性阈值

> 基准机：4 核 8 线程 / 16GB / 集显（Iris Xe 或 Vega 8）/ SSD / Win11 23H2
> 测量工具：任务管理器（详细信息列：内存-专用工作集、句柄、GDI 对象、USER 对象）、Performance Monitor、PresentMon / CapFrameX（帧率）、GPU-Z（GPU 占用）

| ID | 优先级 | 指标 | **阈值（红线）** | 测量方法 |
|---|---|---|---|---|
| PERF-01 | P0 | 空闲 CPU（静态壁纸，三进程合计） | **< 0.5%**（5 分钟均值），瞬时峰值 **< 3%** | PerfMon 采样 5min |
| PERF-02 | P0 | 空闲内存（三进程专用工作集合计，静态壁纸 + 4 个小组件） | **< 350 MB**；主 WPF 进程单独 **< 120 MB** | 任务管理器 |
| PERF-03 | P0 | 动态壁纸 GPU 占用（1080p60 硬解，单屏） | 集显 **< 8%**，独显 **< 4%** | GPU-Z 3D/Video Decode 占用 |
| PERF-04 | P0 | **暂停时 GPU 占用** | **= 0%**（必须真正停止解码，不是隐藏窗口） | GPU-Z + mpv 进程 CPU 应 <0.1% |
| PERF-05 | P0 | **对游戏帧率影响** | 壁纸暂停时 FPS 下降 **< 3%**；未暂停时 **< 10%** | 同一场景跑 3 次取均值，对照无本程序基线 |
| PERF-06 | P0 | **24h 挂机内存增长** | 工作集增长 **< 5%** 且非单调上升 | PerfMon 记录 Private Bytes，每 5min 采样 |
| PERF-07 | P0 | **24h 挂机句柄泄漏** | 句柄增长 **< 50** 个；GDI 对象稳定在 **< 1500**；USER 对象 **< 1000** | 任务管理器句柄/GDI 列 |
| PERF-08 | P1 | 4K 动态壁纸 GPU | 集显 **< 20%**，独显 **< 10%** | 同 PERF-03 |
| PERF-09 | P1 | 双屏各一路 1080p60 动态壁纸 | 集显 GPU **< 15%**，内存增量 **< 150MB** | — |
| PERF-10 | P1 | 磁盘 IO（空闲态） | **≈ 0 MB/s**（不得轮询扫盘） | 资源监视器 |
| PERF-11 | P1 | 壁纸切换耗时 | **< 1.5s** 完成过渡，无黑屏闪烁 | 录屏逐帧 |
| PERF-12 | P1 | 7×24 稳定性 | 连续运行 7 天无崩溃、无功能退化 | 日志巡检 |
| PERF-13 | P2 | 低配机验证 | 双核 / 8GB / 核显：空闲 CPU < 1.5% | — |

**任一 P0 阈值超标即阻断发版。** PERF-06/07 的 24h 测试建议用 VM 快照 + 定时截图脚本自动化。

---

## 4. 「一键还原」回滚预案

### 4.1 必须备份的状态（应用任何改动**之前**）

| # | 状态项 | 来源 | 备份格式 |
|---|---|---|---|
| 1 | 壁纸路径 + 样式 | `HKCU\Control Panel\Desktop` → `Wallpaper` / `WallpaperStyle` / `TileWallpaper` | `.reg` 导出 |
| 2 | **壁纸位图实体** | `%APPDATA%\Microsoft\Windows\Themes\TranscodedWallpaper` | 原文件二进制拷贝（原图可能已被用户删除，必须存实体） |
| 3 | 桌面背景色 | `HKCU\Control Panel\Colors` → `Background` | `.reg` |
| 4 | **桌面图标位置** | `HKCU\Software\Microsoft\Windows\Shell\Bags\1\Desktop` → `ItemPos*` | `.reg`（二进制值原样导出） |
| 5 | 桌面图标可见性 | `HKCU\...\Explorer\Advanced` → `HideIcons` | `.reg` |
| 6 | 图标大小/排列 | `HKCU\...\Bags\1\Desktop` → `IconSize` / `FFlags` | `.reg` |
| 7 | 系统图标显示状态 | `HKCU\...\Explorer\HideDesktopIcons\NewStartPanel` | `.reg` |
| 8 | 开机自启项 | `HKCU\...\CurrentVersion\Run` → 我方键值 | `.reg` + 标记 |
| 9 | **桌面文件清单快照** | 枚举 `Desktop` + `Public\Desktop` | JSON：文件名 / 大小 / 修改时间 / 属性（**仅校验用，证明我们没动文件**） |
| 10 | AppBar 注册状态（仅高级选项） | 自维护 | JSON：边缘 / 尺寸 / 句柄标识 |
| 11 | 显示器拓扑 | `GetMonitorDevicePathAt` 全量 | JSON：设备路径 / 分辨率 / DPI / 主屏标记 |

> 因已砍掉系统主题改造、不注册默认 AppBar、不移动文件，**需备份的项比原方案少一半，且都是 HKCU 用户级键，无需管理员权限**。

### 4.2 备份存储设计

```
restore/
├── baseline/                    ← 首次运行创建，永不覆盖（出厂状态）
│   ├── manifest.json
│   ├── desktop-registry.reg
│   ├── icon-positions.reg
│   ├── TranscodedWallpaper.bak
│   └── desktop-files.json
├── snapshots/
│   ├── 20250731-214800/         ← 每次应用改动前自动创建
│   ├── 20250801-093012/
│   └── ...                      ← 滚动保留最近 5 份
├── restore.cmd                  ← 纯批处理，不依赖我方 EXE
├── restore.ps1                  ← PowerShell 版（功能更全）
└── 应急手册.txt                  ← 纯文本，断网可读
```

**四条保命规则**：
1. **双位置镜像**：`程序目录/data/restore/` 与 `%LOCALAPPDATA%\DeskKit\restore\` 同步双写，任一存活即可恢复。
2. **baseline 永不覆盖**：首次运行写入后设为只读，任何情况下都能退回"从未安装过"的状态。
3. **原子写入**：先写 `.tmp` → `fsync` → `MoveFileEx` 替换，杜绝断电产生半截文件。
4. **自校验**：每份快照带 SHA-256 清单，加载前校验；损坏则自动跳到上一份，全坏则用 baseline。

### 4.3 三级还原手段

**L1 — 程序内一键还原**（主路径）
设置页显眼位置放「🔄 恢复默认桌面」按钮，无二次确认迷宫。执行：停止所有子进程 → 恢复 DefView 可见 → 注销 AppBar（若有）→ 导入注册表 → 还原壁纸实体 → `SHChangeNotify(SHCNE_ASSOCCHANGED)` 刷新 → 提示是否重启 explorer。**目标 5 秒内完成，无需重启系统。**

**L2 — 命令行开关**（程序能启动但 UI 坏了）
```
DeskKit.exe --safe      安全模式启动（跳过全部美化）
DeskKit.exe --restore   静默还原到 baseline 后退出
DeskKit.exe --repair    仅清理残留（AppBar 注销 + 恢复 DefView）
DeskKit.exe --restore-snapshot 20250801-093012
```

**L3 — 独立脚本**（我方 EXE 完全打不开）
`restore.cmd` 为**备份时生成的纯文本批处理**，不依赖任何我方二进制：
```bat
@echo off
echo [1/4] 恢复注册表...
reg import "%~dp0baseline\desktop-registry.reg"
reg import "%~dp0baseline\icon-positions.reg"
echo [2/4] 恢复壁纸文件...
copy /Y "%~dp0baseline\TranscodedWallpaper.bak" "%APPDATA%\Microsoft\Windows\Themes\TranscodedWallpaper"
echo [3/4] 结束残留进程...
taskkill /F /IM DeskKit.exe /IM DeskKit.Renderer.exe /IM DeskKit.Widgets.exe 2>nul
echo [4/4] 重启资源管理器...
taskkill /F /IM explorer.exe & start explorer.exe
echo 完成。
pause
```

### 4.4 安全模式启动

三种进入方式：
1. **手动**：`--safe` 参数，或**按住 Shift 双击**。
2. **自动**：启动时写 `startup.lock`，成功进入主循环 10s 后删除。若启动时发现已存在 lock 且计数 ≥3 → **自动进入安全模式**（说明连续 3 次启动崩溃）。
3. **托盘菜单**：「重启到安全模式」。

安全模式行为：不隐藏 DefView、不创建 Dock、不加载小组件、不启动壁纸渲染进程、不连 Lively。**只提供**：设置界面 + 一键还原按钮 + 日志导出按钮 + 模块逐项启用开关（用于二分定位是哪个模块崩的）。

### 4.5 崩溃兜底机制

| 机制 | 实现 | 防护目标 |
|---|---|---|
| **Job Object** | 主进程创建 Job，`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`，所有子进程加入 | 主进程被强杀 → 子进程全部随之死亡，杜绝孤儿 mpv/WebView2 |
| **守护进程** | 极简独立 EXE（<1MB，无 WPF 依赖），`WaitForSingleObject` 主进程句柄。主进程异常退出 → 立即恢复 DefView 可见 + 注销 AppBar + 自身退出 | 「桌面图标消失」的最后一道防线 |
| **反向看门狗** | 子进程持有父进程句柄，父死则自杀（双保险，防 Job Object 失效） | 孤儿进程 |
| **TaskbarCreated 钩子** | `RegisterWindowMessage("TaskbarCreated")`，收到即全模块重建，**永不缓存 HWND** | explorer 重启（P0 头号杀手） |
| **启动自愈** | 每次启动先无条件执行一次残留清理（扫描并注销陈旧 AppBar、确保 DefView 可见） | 上次异常退出的残留 |
| **天然兜底** | DefView 的隐藏通过 `ShowWindow` 实现，**不持久化**——explorer 一重启必然恢复可见 | 即使所有机制失效，用户重启 explorer 就能救回来 |

> **关键安全论证**：因 MVP 锁死为「虚拟视图 + `ShowWindow` 隐藏」而非改注册表/移动文件，**最坏情况的破坏都是非持久化的**。这是本架构最大的安全红利——不存在"重启也回不去"的状态。

### 4.6 应急手册（写入 `应急手册.txt`，程序内可查）

| 症状 | 用户操作 |
|---|---|
| **桌面图标全没了** | ① `Ctrl+Shift+Esc` → 找到"Windows 资源管理器" → 右键**重启**。90% 情况立即恢复。<br>② 仍无 → 桌面右键 → 查看 → 勾选"显示桌面图标"。<br>③ 仍无 → 运行 `restore/restore.cmd`。 |
| **壁纸变黑屏** | ① 右键托盘图标 → 恢复默认壁纸。<br>② 程序无响应 → 运行 `DeskKit.exe --restore`。<br>③ 仍黑 → 运行 `restore.cmd`；或设置 → 个性化 → 背景，手动选一张。 |
| **屏幕边缘永久留白**（仅开过工作区预留） | 运行 `DeskKit.exe --repair`；无效则重启 explorer（同上）。 |
| **程序一启动就崩** | ① 按住 `Shift` 双击图标进安全模式。<br>② 或命令行 `DeskKit.exe --safe`。<br>③ 安全模式内逐个模块开启，定位崩溃源。<br>④ 导出日志（`data/logs/`）。 |
| **Explorer 反复崩溃** | ① 立即结束 DeskKit 全部进程（任务管理器搜 "DeskKit"）。<br>② 运行 `restore.cmd`。<br>③ 从启动项移除（`Win+R` → `shell:startup` 删快捷方式）。 |
| **Dock 消失/点不动** | 托盘 → 重载 Dock。无效则重启程序。 |
| **动态壁纸不动/游戏卡** | 检查 Lively 是否在运行；托盘 → 切换为静态壁纸。 |
| **小组件白屏** | 托盘 → 重载小组件（WebView2 宿主重启）。持续白屏检查 WebView2 运行时是否被卸载。 |
| **想彻底卸载** | ① 程序内「恢复默认桌面」；② 退出程序；③ 运行 `restore.cmd` 确保干净；④ 删除整个程序目录 + `%LOCALAPPDATA%\DeskKit`。 |
| **上述全都不行** | 注销当前用户重新登录（重建 explorer 会话）；仍不行则重启系统；再不行运行 `restore.cmd` 后重启。 |

---

## 5. 开发期调试与验证建议

### 5.1 虚拟机快照（最高性价比投入）

- **必备**：Hyper-V（Win11 Pro 自带）或 VMware，建 1 台 Win11 23H2 + 1 台 Win10 22H2 干净镜像。
- **快照策略**：`干净系统` → `装完依赖` → `首次运行前` 三个基础快照。**任何涉及注册表/DefView 的改动，先在 VM 跑，物理机只跑已验证的版本。**
- **多屏模拟**：Hyper-V 增强会话支持多显示器；DPI 混合场景物理机更真实，建议备一台带外接屏的实机做 DISP 组。
- **省时技巧**：VM 内放一个 `reset.ps1`，一键还原到快照 + 拷入最新构建，迭代循环压到 30 秒内。

### 5.2 自动化冒烟测试（可行且值得做）

个人项目不必上 UI 自动化框架，但**这三层值得自动化**：

1. **依赖与产物校验**（`smoke-build.ps1`，每次 publish 后跑）：EXE 存在且 >50MB、版本号正确、无 MOTW、依赖文件齐全、`--version` 能正常退出（退出码 0）。
2. **进程生命周期测试**（`smoke-lifecycle.ps1`，最有价值）：
   ```
   启动 → 断言 3 个进程存在 → 断言托盘窗口存在
   → taskkill 渲染子进程 → 等 10s → 断言主进程存活且渲染进程已重建
   → taskkill 主进程 → 等 5s → 断言无残留子进程（REC-05）
   → 重启 explorer → 等 10s → 断言图标层恢复（REC-01）
   → --restore → 断言注册表键值与 baseline 一致（REC-07）
   ```
   纯 PowerShell 可实现（`Get-Process` / `Get-ItemProperty` / `taskkill`），**这一个脚本覆盖了 6 个 P0 用例**，每次提交前跑一遍，收益极高。
3. **性能采样**（`smoke-perf.ps1`）：启动后挂机 10 分钟，采样 CPU/内存/句柄，超阈值报警。24h 版同脚本改参数跑。

UI 层交互（拖拽图标、右键菜单）建议**手工测**，自动化投入产出比太低。

### 5.3 日志设计

- **库**：Serilog + `RollingFile`（按天滚动，保留 7 天，单文件上限 10MB）。
- **分级**：默认 `Information`；`--verbose` 开 `Debug`；崩溃日志单独写 `crash-{timestamp}.log`。
- **每个进程独立日志文件**：`main.log` / `renderer.log` / `widgets.log`，日志行统一带 `[PID]` 和**同一个 correlation-id**，便于跨进程串联一次操作。
- **必记的关键事件**（出问题时能定位的最小集）：
  - 启动/退出：版本号、命令行参数、OS 版本、DPI 配置、显示器拓扑全量 dump
  - **每次系统级改动前后**：改了什么键、旧值、新值、快照 ID（这是还原的审计线索）
  - `TaskbarCreated` 收到 / 各模块重建耗时
  - 显示器拓扑变化（插拔前后完整对比）
  - 子进程启动/退出/重试次数/退出码
  - Lively IPC 连接状态变化
  - 全局异常 `AppDomain.UnhandledException` + `DispatcherUnhandledException` + `TaskScheduler.UnobservedTaskException`（三个都要挂）
- **禁止记录**：文件内容、完整用户路径（脱敏为 `%USERPROFILE%\...`）。
- **UI 支持**：设置页放「导出诊断包」按钮，一键打包最近 7 天日志 + 当前配置 + baseline 清单为 ZIP，方便自己事后排查。

---

## 6. 一句话交付结论

**打包形态**：.NET 8 WPF **自包含单文件 EXE**（开 ReadyToRun + 压缩 + InvariantGlobalization，**绝不开裁剪、绝不 UPX**，约 60MB），以**便携版 ZIP** 交付、默认便携模式、配置与还原备份双写程序目录与 LocalAppData——个人自用不做安装包、不买签名证书（自签名对 SmartScreen 零作用，本地编译产物无 MOTW 压根不弹）。

**最容易翻车的场景**：**explorer.exe 重启**（REC-01）——它会一次性废掉 DefView 句柄、自绘图标层和 Dock，唯一正确解法是注册 `TaskbarCreated` 广播消息后全模块重建、**所有 HWND 一律不缓存**；紧随其后的是**显示器热插拔 + 混合 DPI**（DISP-02/03），必须用 `GetMonitorDevicePathAt` 设备路径而非索引做持久化。

**必须最先做的一个安全机制**：**在写下第一行美化代码之前，先做完 baseline 备份 + `restore.cmd` 离线还原脚本**——即备份链路（永不覆盖的出厂快照、双位置镜像、原子写）与不依赖任何我方二进制的纯批处理还原脚本，它是后续所有高危改动的安全网，也是唯一能在"程序彻底打不开"时救回桌面的手段。

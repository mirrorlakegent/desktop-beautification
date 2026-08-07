# DeskKit Design System

> Windows 桌面美化套件设计系统 —— 一个主题包描述整个桌面，AI 只填意图，引擎保证一致。

本文档是本项目视觉设计的唯一源文件。所有 UI 实现以此为准。技术栈无关：WPF / Electron / Tauri 任选，只要能消费 `theme-schema.json` 即可。

---

## 0. 核心架构

项目的设计矛盾是：需求要「AI 自由设计，形成独特风格」，但 AI 若能自由到每个模块各写各的，产出必然是四个互不相干的东西拼在一起。

解法是**三层收敛**：

```
                   ┌──────────────────────────────┐
   用户一句话  ──▶ │  AI  →  ThemeIntent (35 字段) │  只表达审美意图
                   └──────────────┬───────────────┘
                                  ▼
                   ┌──────────────────────────────┐
                   │  Expander 编译器（确定性）    │  展开色阶/数值/布局
                   └──────────────┬───────────────┘
                                  ▼
                   ┌──────────────────────────────┐
                   │  Validator 六道闸（确定性）   │  对比度/性能/和谐度
                   └──────────────┬───────────────┘
                                  ▼
                        theme.json（完整主题包）
                                  ▼
        ┌───────────┬────────────┬──────────┬────────────┐
        │  壁纸层   │  桌面整理  │  小组件  │   Dock     │
        └───────────┴────────────┴──────────┴────────────┘
              全部只引用同一组 Token，不得内联字面值
```

关键点：**四个模块不是平级的四个配置，而是同一组 Token 的四个投影**。一致性不靠约定，靠「模块无权定义自己的颜色/圆角/材质」这一硬机制。

---

## 1. 主题包文件组织

### 1.1 目录结构

```
aurora-glass.dktheme          # 实为 zip，双击由本套件接管安装
├── theme.json                # 主题定义，符合 theme-schema.json
├── intent.json               # 可选。AI 生成时的原始 Intent，供二次迭代
├── preview/
│   ├── cover.png             # 1920×1080 主预览图
│   ├── thumb.png             # 480×270 列表缩略图
│   └── dock.png              # Dock 特写
├── assets/
│   ├── wallpapers/
│   ├── icons/                # 图标包，按 exe 名或 AppUserModelID 匹配
│   ├── fonts/                # 进程内加载，不写系统字体目录
│   ├── shaders/              # .frag，Shadertoy 兼容 uniform 约定
│   └── textures/             # 噪点、纸纹、扫描线
├── widgets/
│   └── <widget-id>/
│       ├── manifest.json
│       └── index.html
├── LICENSE
└── CREDITS.md                # 壁纸/字体/图标的来源与授权，规避版权风险
```

### 1.2 资源引用规则

| 规则 | 说明 |
|------|------|
| 只允许包内相对路径 | `assets/...`、`widgets/...` |
| 内置资源用 scheme | `builtin://noise-1`、`builtin://shader/aurora` |
| 远程资源显式声明 | `https://` 开头，安装时提示用户 |
| **禁止绝对本地路径** | `C:\Users\...` 会让主题包不可分发，安装器直接拒绝 |
| 路径穿越检测 | 解压前校验，拒绝 `..` 与符号链接 |

### 1.3 版本与兼容

- `formatVersion`：Schema 大版本。major 不同直接拒绝加载。
- `meta.version`：主题自身版本，semver。
- `capabilities.requires`：声明依赖（`gpu-shader` / `video-decode` / `webview`）。不满足时按 `capabilities.degradeTo` 回退到降级主题，**不报错**。
- `extends`：继承预置主题，只写差异。AI 生成的包应始终 `extends`，体积小且不易出错。

---

## 2. 跨模块视觉一致性规则

以下为**硬性规则**，由加载器在渲染前强制校验（`consistency.profile: strict`）。违反即拒绝加载或自动修正。

### 2.1 十二条硬规则

| # | 规则 | 强制方式 |
|---|------|---------|
| **R1** | 模块层（wallpaper/dock/widgets/desktop）**禁止内联字面色值、圆角、模糊值**，只能写 Token key 或 `$ref` | Schema 中这些字段类型为 `string`（Token key），非数值 |
| **R2** | 所有面板材质派生自 `tokens.material` 的具名条目，不允许就地定义 | 同上 |
| **R3** | 实际使用的不同圆角值 ≤ `maxDistinctRadii`（默认 3）；且 `r(dock) ≥ r(widget) ≥ r(zone)` 或三者全等 | 校验器统计后拒绝 |
| **R4** | **单光源**：所有 `elevation` 阴影共用同一 `y/x` 方向比与同一阴影色相 | 校验器检查 shadow 方向一致性 |
| **R5** | 所有 1px 边框取自同一材质的 `border`，颜色随亮暗极性自动翻转（暗色白 12%，浅色黑 8%） | 由 `$contrast` 原语求值 |
| **R6** | 任何前景文字对**实际背景（含壁纸）** APCA Lc ≥ 75（正文）/ 60（大字）/ 45（UI 元素） | 运行时 adaptiveScrim 保障，非设计时假设 |
| **R7** | 跨模块同类动作共用同一 duration token（Dock hover 与小组件 hover 都用 `motion.duration.fast`） | `motion.energy` 单值统一缩放 |
| **R8** | 视差系数随 z 层单调递增，最大位移 ≤ 12px | 校验器钳制 |
| **R9** | 所有间距必须落在 `spacing.scale` 白名单内（4 的倍数） | 校验器拒绝白名单外取值 |
| **R10** | 一套主题最多 2 个字体族 + 1 个等宽；字号取自 `typography.scale` | 校验器统计 |
| **R11** | 面板**合成后**明度不得落进对比度死区 `L ∈ [0.58, 0.82]` | 校验器按最坏壁纸推演合成色，越界则抬 `tintOpacity` |
| **R12** | 任何**承载文字的填充底色**（按钮、Dock 指示器、徽标）承文上限必须 ≥ 该文字角色的门槛 | 校验器算 ceiling，不足则要求提供 `.700` 深档 |

### 2.1b 对比度死区（实测结论，务必遵守）

这是本次设计中通过 APCA 实测扫描发现的**反直觉约束**，直接影响调色板取值。

「承文上限（ceiling）」= 在某底色上，纯白字与纯黑字所能达到的 Lc 的较大者。它是该底色**理论最好**的文字对比度。实测灰阶扫描：

| 底色 OKLCh L | 示例 | 白字 Lc | 黑字 Lc | 承文上限 |
|---|---|---|---|---|
| 0.52 | `#696969` | 82.7 | 26.6 | **82.7** |
| 0.60 | `#808080` | 72.4 | 37.2 | 72.4 ❌ |
| 0.72 | `#A4A4A4` | 54.0 | 55.0 | **55.0** ❌ 最劣点 |
| 0.80 | `#BEBEBE` | 39.3 | 68.8 | 68.8 ❌ |
| 0.84 | `#CACACA` | 32.0 | 75.4 | **75.4** |

**结论：`L ∈ [0.58, 0.82]` 是死区 —— 无论文字取纯黑还是纯白都够不到正文门槛 Lc 75，最劣点仅 55。**

两条直接推论：

1. **面板层（R11）**：玻璃面板在亮壁纸上合成后会往中间调漂。曜岩玻璃 `tintOpacity=0.62` 在纯白壁纸下合成为 `#6D7177`（L=0.54），刚好压在死区下沿外，余量仅 0.04 —— 很薄但成立。若降到 0.50 则合成 L=0.63 落入死区，**此时调文字色完全无效，必须回到面板层加不透明度**。实测：0.62→0.70 可让白字 Lc 从 68.1 抬到 75.9。
2. **调色板层（R12）**：中等明度的高饱和品牌色普遍落在死区里。实测四套预设：

| 角色色 | L | 承文上限 | 能扛正文 75 | 能扛大字 60 |
|---|---|---|---|---|
| 曜岩 primary `#4C8DFF` | 0.66 | 64.2 | ❌ | ✅ |
| 霓虹 primary `#FF2E97` | 0.67 | 65.6 | ❌ | ✅ |
| 曜岩 secondary `#7C9CBF` | 0.68 | 59.9 | ❌ | ❌ |
| 曜岩 accent `#38E1C6` | 0.82 | 75.6 | ⚠️ 勉强 | ✅ |
| 霓虹 accent `#00F0FF` | 0.87 | 84.7 | ✅ | ✅ |

**所以「用 primary 做填充按钮底、上面放正文字号的文字」在多数主题里根本不可能达标。** 处理办法不是改文字色，而是：填充承文场景改用 `primary.700`（曜岩 `#1C5BC9`，上限 85.4），或把标签降级为粗体大字（门槛 60）。

注意死区是**一条带**不是阈值：`accent.600 #00947C`（L=0.58）反而又掉回死区，`accent.700 #007B65`（L=0.50）才重新达标。生成色阶时必须逐档校验，不能假设「越深越安全」。

### 2.2 对比度保障机制（重点）

壁纸可以是任意图片，这是整个设计系统最脆弱的地方。**三层防御**：

**第一层 —— 源头治理（设计时）**

壁纸提示词强制包含构图约束：底部 15%（Dock 带）与小组件区保持低对比、避免高频细节。程序化壁纸（gradient / shader）天然满足，因为色值直接取自 palette。

**第二层 —— 极性自适应（`consistency.polarityFlip`）**

引擎实时采样每个前景元素背后的壁纸区域，计算 OKLCh 平均 L：

```
L_bg > 0.55  →  该元素切换到「深色前景」Token 组
L_bg < 0.55  →  切换到「浅色前景」Token 组
hysteresis = 0.06   # 防止动态壁纸下反复横跳
scope = per-element # Dock 和小组件可各自处于不同极性
```

**第三层 —— 自适应遮罩（`consistency.adaptiveScrim`）**

极性切换后仍不达标时，按代价从低到高逐级施加：

| 级 | 手段 | 上限 |
|----|------|------|
| 1 | 提高材质 `tintOpacity` | +0.05/步，累计 ≤ 0.3 |
| 2 | 提高 `blurRadius` | ≤ `maxBlur` 40px |
| 3 | 注入局部 scrim（面板形状/径向衰减） | `maxOpacity` 0.55 |
| 4 | 文字描边 / 投影（专用于桌面图标文字） | 半径 ≤ 8px |
| 5 | 强制极性翻转 | 兜底 |

**这条链路永不失败，只会逐级降级。** 每次修正写入 `meta.generator.repairs`，设置界面可展示「本主题自动修正了 3 处可读性问题」。

### 2.3 动态壁纸下的稳定性

视频/Shader 壁纸下，亮度每帧都变。三个措施防止 UI 闪烁：

1. **采样降频**：`sampleIntervalMs: 400`，先降采样到 32×32 再算，CPU 开销可忽略。
2. **迟滞（hysteresis）**：变化超过阈值才响应。
3. **过渡动画**：`transitionMs: 600`，遮罩强度平滑插值，绝不跳变。

### 2.4 层级与纵深

| z | 层 | elevation | 视差系数 | 说明 |
|---|-----|-----------|---------|------|
| 0 | wallpaper | — | 0.00 | 底 |
| 1 | scrim | — | 0.00 | 自适应遮罩，贴合壁纸 |
| 2 | desktopIcons | level0 | 0.01 | 只有文字保护，无底板 |
| 3 | zones | level1 | 0.02 | 分组区，最"沉" |
| 4 | widgets | level2 | 0.04 | 浮于分区之上 |
| 5 | dock | level3 | 0.06 | 最高常驻层 |
| 6 | overlay | level4 | — | 弹出层、拖拽预览 |

纵深表达三要素同步递增：**阴影强度 ↑、边缘高光 ↑、视差位移 ↑**。分组区比小组件"沉"是刻意的 —— 它承载图标，应该像桌面上的托盘而非浮空卡片。

---

## 3. 预置主题（4 套）

四套同时承担三个角色：开箱即用成品、AI 的 `basedOn` 风格锚点、few-shot 示例。

### 3.1 Obsidian Glass · 曜岩玻璃

**设计意图**：深空冷调的玻璃拟态。默认主题，最百搭，适合任何壁纸。克制的蓝 + 一点青绿提神。

| 角色 | Hex | 用途 |
|------|-----|------|
| bg | `#0B0F16` | 壁纸兜底 |
| surface.base | `#131A24` @62% | 面板底 |
| surface.raised | `#1B2431` | 悬浮态 |
| border | `#FFFFFF` @12% | 发丝边 |
| primary | `#4C8DFF` | 选中、指示器 |
| secondary | `#7C9CBF` | 次级信息 |
| accent | `#38E1C6` | 强调点缀（<5% 面积）|
| text.primary | `#E6EDF6` | 正文 |
| text.secondary | `#9AAABF` | 辅助 |
| text.tertiary | `#6B7C93` | 弱化 |
| success / warning / danger / info | `#3FCF8E` / `#F5B544` / `#FF5C68` / `#4C8DFF` | 语义 |

- **材质**：acrylic，blur 32、tintOpacity .62、saturation 1.3、noise .03、顶部渐变高光边
- **形状**：round，sm 8 / md 14 / lg 22
- **动效**：standard，spring(420, 32)
- **壁纸**：深蓝紫极光 shader，或任意深色摄影图
- **Dock**：统一底板 pill，magnify 1.45 波及 2 邻居，圆点指示器，原色图标
- **小组件**：玻璃卡片，comfortable 密度，标题 12px/500/+0.04em 大写

### 3.2 Paper Light · 素纸

**设计意图**：白天工作用。近乎无材质，靠留白和一根朱红提神。字大、间距松、几乎无阴影。反玻璃拟态。

| 角色 | Hex | 用途 |
|------|-----|------|
| bg | `#F5F3EF` | 暖白 |
| surface.base | `#FFFFFF` @86% | 面板 |
| surface.sunken | `#EAE7E1` | 凹陷 |
| border | `#14181C` @8% | 发丝边 |
| primary | `#1F2933` | 墨色主色 |
| secondary | `#5B6670` | 次级 |
| accent | `#C8452F` | 朱红，唯一亮色 |
| text.primary | `#14181C` | 正文 |
| text.secondary | `#5B6670` | 辅助 |
| text.tertiary | `#8A939E` | 弱化 |
| success / warning / danger / info | `#2E7D5B` / `#B07A1B` / `#C0392B` / `#2C6BAA` | 语义 |

- **材质**：frosted，blur 12、tintOpacity .86、saturation 1.0、paper 纹理 .02
- **形状**：round，sm 6 / md 10 / lg 14
- **动效**：calm，时长 ×1.2，easeOut 为主，无弹性
- **壁纸**：浅色纯色/极淡渐变/留白摄影。**注意**：浅色壁纸下 Dock 需切到深色前景，靠 polarityFlip 自动完成
- **Dock**：分离式底板（每图标独立），lift 8px，下划线指示器，标签 always
- **小组件**：spacious 密度，chromeless 倾向，hero 巨型时钟（细体、大字号、负字距）

### 3.3 Neon Grid · 霓虹栅格

**设计意图**：赛博朋克。发光取代投影，锐角取代圆角，高饱和洋红/青对撞。视觉冲击力最强，也最容易做丑 —— 靠严格的饱和度预算控制。

| 角色 | Hex | 用途 |
|------|-----|------|
| bg | `#08040F` | 近黑紫 |
| surface.base | `#170C29` @55% | 面板 |
| surface.raised | `#22103A` | 悬浮 |
| border | `#FF2E97` @35% | 霓虹边 |
| primary | `#FF2E97` | 洋红 |
| secondary | `#7B2FFF` | 紫 |
| accent | `#00F0FF` | 青，对撞色 |
| text.primary | `#F4E9FF` | 正文 |
| text.secondary | `#B79BDD` | 辅助 |
| text.tertiary | `#7E63A8` | 弱化 |
| success / warning / danger / info | `#39FF88` / `#FFC531` / `#FF3B5C` / `#00F0FF` | 语义 |

- **材质**：glass，blur 20、saturation 1.6、glow 边框（primary @45%，radius 24）
- **形状**：sharp，sm 4 / md 6 / lg 10
- **动效**：playful，时长 ×0.8，overshoot cubic-bezier(.34,1.56,.64,1)
- **壁纸**：gridTunnel / plasma shader，或霓虹城市夜景
- **Dock**：统一底板，glow hover（青色光晕），ring 指示器，图标 duotone 染色统一到洋红/青
- **小组件**：compact 密度，扫描线纹理，数字用等宽，边框发光
- **饱和度预算**：高饱和色（C > 0.15）总覆盖面积 ≤ 屏幕 12%，超出由 Harmony 闸降饱和

### 3.4 Phosphor · 磷光终端

**设计意图**：CRT 绿色单色终端。全等宽字体、直角、零圆角、扫描线。极端风格，但对折腾党杀伤力最大。

| 角色 | Hex | 用途 |
|------|-----|------|
| bg | `#050A07` | 近黑 |
| surface.base | `#0A1410` @90% | 面板（几乎不透明）|
| surface.raised | `#0F1F16` | 悬浮 |
| border | `#3DF07C` @20% | 荧光边 |
| primary | `#3DF07C` | 磷光绿 |
| secondary | `#1F8A4C` | 暗绿 |
| accent | `#A8FF60` | 亮绿高光 |
| text.primary | `#C9FFD9` | 正文 |
| text.secondary | `#63B37F` | 辅助 |
| text.tertiary | `#3E7355` | 弱化 |
| success / warning / danger / info | `#3DF07C` / `#FFD166` / `#FF6B4A` / `#6FE3FF` | 语义 |

- **材质**：solid（不透明）+ 扫描线纹理 .06 + 荧光外辉光
- **形状**：sharp，全部 0，cornerShape `cut`（切角 4px）
- **字体**：display / body / mono 全部等宽（JetBrains Mono → Cascadia Code → Consolas → 等宽回退）
- **动效**：standard，linear 为主（终端感），无弹性；光标闪烁 ambient
- **壁纸**：纯色 + noiseClouds shader 微噪，或 ASCII art
- **Dock**：无底板（`plateMode: none`），图标单色化为磷光绿剪影，方括号 `[ ]` 包裹当前项，bar 指示器
- **小组件**：compact，全文本化（CPU 用 `████░░░░` 条），无圆角，标题带 `>` 前缀
- **注意**：`chromeless` + 无底板意味着完全依赖 adaptiveScrim 的 outline 策略保证可读

---

## 4. AI 设计接口规范

### 4.1 输出契约 —— 对 team-lead 判断的修正

team-lead 的判断是「AI 输出符合 Schema 的主题包 JSON」。**方向正确，但直接让 AI 写完整 `theme.json` 不可行**，三个理由：

1. **规模**：完整 theme.json 有 200+ 字段。LLM 一次性输出这个规模的结构化 JSON，字段缺失、枚举越界、括号不闭合的概率高到无法用于产品。
2. **数学**：色阶 50→900 需在 OKLCh 空间按明度曲线均匀分布，对比度需算 APCA。LLM 做这类数值计算的结果肉眼可见地不均匀。让它算，等于把确定性问题交给概率模型。
3. **重复**：大部分字段是从少数几个决策机械派生的。让 AI 逐个填，既浪费 token 又在派生环节引入不一致。

**修正后的契约：AI 输出 `ThemeIntent`（约 35 字段），编译器展开为完整 theme.json。**

见 `theme-intent-schema.json`。AI 只回答审美问题（用什么色、什么材质性格、什么氛围、壁纸画什么），机械与数学部分全交给代码。同时保留 `overrides` 逃生口（JSON Pointer 直接改 theme.json 片段），但逃生口仍须过完整校验管线。

这样做的收益是可量化的：需要 AI 正确填写的字段从 200+ 降到 ~35，且其中 28 个是枚举（可用受约束解码强制合法）。剩下真正自由的只有 3 个 hex 色 + 2 段提示词文本。

### 4.2 AI / 规则引擎职责边界

| 环节 | AI | 规则引擎 | 理由 |
|------|:--:|:--------:|------|
| 配色种子（2~3 个 hex） | ✅ | | 审美判断 |
| 色阶 50→900 展开 | | ✅ | OKLCh 数学 |
| 氛围命名、设计说明 | ✅ | | 语言任务 |
| 材质/形状/动效**性格**选择 | ✅ | | 审美判断 |
| 材质/形状/动效**具体数值** | | ✅ | 性格 → 数值查表 |
| 壁纸提示词 | ✅ | | 创意生成 |
| 壁纸像素 | | 图像模型 | 非 LLM 职责 |
| Shader 代码 | | ✅ 内置库 | 编译/性能/安全不可控 |
| 小组件集合 + 分布倾向 | ✅ | | 审美判断 |
| 小组件精确栅格坐标 | | ✅ 布局求解器 | 让 AI 摆坐标必然重叠 |
| 分区语义命名与归类 | ✅ | | 需要语义理解 |
| 文件实际移动 | | ✅ + dryRun | 破坏性操作 |
| **对比度保障** | ❌ | ✅ | 硬约束，不可协商 |
| **性能预算** | ❌ | ✅ | 硬约束 |
| **z 序、边界钳制** | ❌ | ✅ | 硬约束 |

原则一句话：**AI 决定"像什么"，引擎决定"是多少"。**

### 4.3 提示词架构

四段式，前三段固定缓存，只有第四段随用户变化：

```
┌─ Block 1  系统契约（固定，可缓存）
│  • 角色：桌面主题设计师
│  • ThemeIntent Schema 精简版（枚举值全列出）
│  • 硬要求：只输出纯 JSON，无 markdown 围栏，无解释文字
│  • basedOn 必填，必须从 4 个锚点中选
│
├─ Block 2  设计法则（固定，可缓存）
│  • 暗色主题 surface 明度 L ∈ [0.10, 0.22]；浅色 L ∈ [0.92, 1.0]
│  • accent 与 primary 的色相差 Δh 应 ≥ 60° 或 = 0°（同色系）
│    —— 避开 20°~50° 脏区间，那是配色显廉价的主因
│  • 强调色全屏面积 < 5%
│  • 中性色必须带 primary 的微量色偏（C ≈ 0.01~0.02），纯灰显脏
│  • 材质 + 形状 + 动效必须同性格：玻璃配圆角配弹性，
│    平面配锐角配线性。混搭是 AI 生成主题最典型的失败模式
│  • 壁纸提示词必须含构图约束：底部 15% 低对比、避免高频细节
│
├─ Block 3  Few-shot（固定，可缓存）
│  • 3 组「用户描述 → ThemeIntent JSON」样例
│  • 取自四套预置主题，覆盖 暗玻璃 / 浅极简 / 高饱和 三个方向
│
└─ Block 4  用户输入（变化）
   • 用户一句话
   • 可选上下文：当前壁纸取色摘要、屏幕分辨率、已装字体清单、
     用户历史偏好（选过哪些主题）
```

**输出强制**：优先用模型的 structured output / JSON Schema 受约束解码。不支持时走 JSON 修复（剥 markdown 围栏、补引号、去尾逗号）。

**重试策略**：校验失败 → 把具体错误回灌给模型重试，**最多 2 次**。仍失败 → 回退到 `basedOn` 指向的预置主题，只应用能解析出的颜色偏好。**永不向用户报错。**

### 4.4 校验与兜底管线（六道闸）

```
AI 输出
   │
   ├─ G1 语法闸    JSON 解析 / 自动修复（剥围栏、补引号、去尾逗号）
   │               失败 → 重试
   │
   ├─ G2 Schema 闸 JSON Schema 校验
   │               缺失字段 → 从 basedOn 预置主题继承
   │               未知字段 → 丢弃并记录
   │               枚举越界 → 取最近合法值
   │
   ├─ G3 范围闸    数值钳制到合法区间
   │               blur ∈ [0,64]  iconSize ∈ [24,96]
   │               opacity ∈ [0,1]  duration ∈ [60,2000]
   │               magnifyScale ∈ [1,2.5]  widgets ≤ 8
   │
   ├─ G4 对比度闸  ★ 核心。对全部前景/背景组合算 APCA
   │               不达标时逐级修复（见下）
   │               R11 死区闸：按最坏壁纸推演面板合成色，
   │                   L∈[0.58,0.82] → 抬 tintOpacity 压出死区
   │               R12 承文闸：填充底色 ceiling < 文字门槛
   │                   → 要求补 .700 深档（见 §2.1b）
   │
   ├─ G5 和谐闸    • 色相脏区检测：Δh ∈ [20°,50°] 且双方 C > 0.1
   │                 → 把 accent 推到 Δh = 60° 或归零
   │               • 暗色主题 surface 色度 C ≤ 0.04
   │               • 饱和度总量预算：C > 0.15 的色彩覆盖 ≤ 12% 面积
   │               • 圆角种类 ≤ 3、字体族 ≤ 2
   │
   └─ G6 性能闸    • 模糊预算：Σ(面板面积 × blurRadius) ≤ B
                   • 视频：≤ 1 路 4K30 或 2 路 1080p30
                   • WebView 小组件 ≤ 3
                   降级顺序（固定）：
                   ambientAnimation 关 → parallax 关 → blur 减半
                   → shader quality 降级 → fps 30→20
                   ▼
              theme.json  +  meta.generator.repairs[]
```

**G4 对比度修复算法**（逐级降级，永不失败）：

```python
def text_ceiling(bg):        # 该背景上文字层的理论最好成绩
    return max(apca('#FFF', bg), apca('#000', bg))

for (fg, bg) in contrast_checklist:          # 文字/图标/边框 × 各自背景
    if apca(fg, bg) >= floor: continue

    # 0) 先判死区：文字层是否根本无解？（见 §2.1b）
    #    bg 落在 L∈[0.58,0.82] 时黑白字都够不到门槛，
    #    此时步骤 1/2 再怎么迭代都是白费，直接跳到面板层。
    if text_ceiling(bg) < floor:
        goto_panel_layer(); continue

    # 1) 保色相、保色度，调明度
    #    ★ 两个方向都试，取先达标者。
    #    不能用 "bg 亮就把字调暗" —— 中间调背景上这个假设会反向降低对比度。
    for direction in (+1, -1):
        for i in range(1, 26):
            cand = oklch(fg.L + direction*0.02*i, fg.C, fg.H)
            if apca(cand, bg) >= floor: return cand

    # 2) 明度到头 → 降色度（≤ maxChromaLoss 0.06）再试两个方向

    # ── 以下为面板层（goto_panel_layer）──
    # 3) 提高面板 tintOpacity（+0.05/步，累计 ≤ 0.3）——把合成明度推出死区
    # 4) 注入 scrim（≤ maxOpacity 0.55）
    # 5) 强制极性翻转
    #    ↑ 5 步内必然收敛

    record_repair(path, reason, before, after)
```

每次修复写入 `meta.generator.repairs`，UI 可展示「AI 想要的配色有 2 处对比度不足，已自动修正」—— 既保证可用，又保持透明。

### 4.5 AI 参与桌面整理

桌面整理是唯一 AI 真正提供智能而非审美的场景。约束：

- AI 读取的是**文件名 + 扩展名 + 修改时间**，不读文件内容（隐私）
- 输出 `desktop.suggestedZones`（分区名 + 匹配规则），不输出具体文件的归属
- 实际归类由规则引擎执行，用户可见规则并可编辑
- `physicalMove` 默认 `false`（仅视觉分组，不动磁盘）
- 开启物理移动时 `dryRun: true` 强制先预览

---

## 5. 实现优先级建议

MVP 四块的设计实现顺序，按依赖排：

1. **Token 层 + Expander** —— 一切的基础，先把 4 套预置主题跑通
2. **壁纸引擎** —— 先 image/gradient，再 video，最后 shader/web
3. **对比度采样与 adaptiveScrim** —— 必须在 Dock 之前，否则 Dock 做完还要返工
4. **Dock** —— 视觉回报最高，用户最先感知
5. **小组件** —— 先 3 个内置（clock / systemMonitor / weather），再开放自定义
6. **桌面整理** —— 涉及 shell 集成，风险最高，放最后
7. **AI 接入** —— 前六项稳定后接入，否则无法判断问题出在 AI 还是引擎

---

## 附：文件清单

| 文件 | 内容 |
|------|------|
| `theme-schema.json` | 主题包完整 JSON Schema（draft 2020-12），30 个 `$defs` |
| `theme-intent-schema.json` | AI 输出契约，约 35 字段 |
| `DESIGN.md` | 本文档 |
| `design-system-preview.html` | 可视化预览页，浏览器直接打开 |

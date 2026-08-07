# 项目记忆：WallpaperLibrary 壁纸库

- 路径：D:\WorkBuddy\桌面美化\src\DesktopSuite\bin\Debug\net8.0-windows\WallpaperLibrary
- 结构：8 个时段文件夹（清晨 / 早上 / 中午 / 下午 / 傍晚 / 黄昏 / 晚上 / 深夜），
  各含 `静态壁纸/`（png/jpg/webp/bmp/gif）与 `动态壁纸/`（mp4/webm/mkv/mov/avi）。
  软件按当前时间选时段目录并每隔一段时间随机轮换。
- 命名约定：各时段用统一英文主题前缀（dawn / morning / midday / afternoon / dusk / sunset / night-city / milkyway），数字递增；
  网络下载素材统一加 `web-` 前缀以便区分来源、不覆盖原有文件。
- 扩展壁纸的两种来源：
  1) AI 生成：ImageGen（静态）+ VideoGen（动态），output_dir 直接指向目标文件夹再重命名。
  2) 网络真实素材：Wikimedia Commons（API 搜索 + curl 下载，公有领域/自由授权，合规安全）。
- 已知坑：
  - 本沙箱 safe-delete 会拒绝删除（python os.remove 与 bash rm 均可能被拦），临时文件需用户手动清理。
  - Commons 视频源 upload.wikimedia.org 在部分环境被限流/超时，动态壁纸建议优先用 AI 生成。

# DesktopSuite 发布安全网脚本 (Phase 0)
# 固化历史踩坑：① 发布前解锁 DLL（否则 MSB3027）② 归档上一版 exe 以便回退
# ③ 单文件发布 ④ 双远程推送 + git ls-remote 校验（防 gitee SSH 假 "Everything up-to-date"）
#
# 用法（在桌面/用户机器，需已配置 SSH 双远程）：
#   powershell -ExecutionPolicy Bypass -File src\publish-ds2.ps1
# 注意：本脚本只负责「发布+推送+校验」，提交(commit)请单独用 git commit 完成。

$ErrorActionPreference = "Stop"

$exe     = "D:\WorkBuddy\ds2\DesktopSuite.exe"
$archive = "D:\WorkBuddy\ds2\archive"
$repo    = "D:\WorkBuddy\桌面美化"
$src     = "$repo\src\DesktopSuite"

# 1) 解锁 DLL：结束可能占用 ds2\DesktopSuite.exe 的进程
taskkill /f /im DesktopSuite.exe 2>$null
Start-Sleep -Milliseconds 800

# 2) 归档上一版 exe（按 日期_commit前7 命名，保留最近 5 个）
if (Test-Path $exe) {
    if (-not (Test-Path $archive)) { New-Item -ItemType Directory -Path $archive | Out-Null }
    $stamp = (git -C $repo rev-parse --short HEAD 2>$null)
    $date  = Get-Date -Format "yyyyMMdd"
    Copy-Item $exe "$archive\DesktopSuite_${date}_${stamp}.exe" -Force
    Get-ChildItem $archive -Filter "DesktopSuite_*.exe" |
        Sort-Object LastWriteTime -Descending |
        Select-Object -Skip 5 |
        Remove-Item -Force -ErrorAction SilentlyContinue
    Write-Host "已归档上一版 exe 到 $archive"
}

# 3) 单文件自包含发布
Set-Location $src
dotnet publish -c Release -r win-x64 `
    -p:SelfContained=true -p:PublishSingleFile=true -p:PublishReadyToRun=true `
    -o D:\WorkBuddy\ds2
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败 (exit $LASTEXITCODE)" }
Write-Host "发布完成：$exe"

# 4) 双远程推送 + 校验
git -C $repo push gitee  master
git -C $repo push github master
$gitee  = (git -C $repo ls-remote gitee  master)  -split '\s' | Select-Object -First 1
$github = (git -C $repo ls-remote github master)  -split '\s' | Select-Object -First 1
Write-Host "gitee  : $gitee"
Write-Host "github : $github"
if ($gitee -ne $github) {
    Write-Warning "⚠ 双远程 HEAD 不一致！请检查推送结果。"
    exit 1
}
Write-Host "✅ 双远程校验一致，发布安全网通过。"

@echo off
chcp 65001 >nul
echo [1/3] 结束可能残留的 DesktopSuite 进程...
taskkill /f /im DesktopSuite.exe 2>nul
timeout /t 1 >nul
echo [2/3] 启动 DesktopSuite...
cd /d "D:\WorkBuddy\桌面美化\src\DesktopSuite\bin\x64\Release\self-contained"
DesktopSuite.exe
echo [3/3] 进程已退出，退出码=%ERRORLEVEL%
echo.
echo 日志位置: %LOCALAPPDATA%\DesktopSuite\logs\wallpaper.log
echo 按任意键打开日志文件夹...
pause >nul
start "" "%LOCALAPPDATA%\DesktopSuite\logs"

@echo off
taskkill /f /im DesktopSuite.exe >nul 2>&1
ping -n 2 127.0.0.1 >nul
start "" "D:\WorkBuddy\ds2\DesktopSuite.exe"

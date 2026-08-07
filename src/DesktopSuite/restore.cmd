@echo off
setlocal EnableExtensions
:: Phase -1 restore (L3 pure batch, no app required).
:: Restores the newest baseline: imports all *.reg, stops the app, restarts
:: Explorer (which recreates the desktop window in its default visible state).
set "ROOT=%LOCALAPPDATA%\DesktopSuite\backups"

if not exist "%ROOT%" (
  echo [restore] No DesktopSuite backups found.
  pause
  exit /b 1
)

set "LATEST="
:: /o-d = directories by modification time, newest first.
for /f "delims=" %%D in ('dir /b /ad /o-d "%ROOT%"') do (
  set "LATEST=%ROOT%\%%D"
  goto :found
)
:found
if "%LATEST%"=="" (
  echo [restore] No backups available.
  pause
  exit /b 1
)

echo [restore] Using backup: %LATEST%
for %%R in ("%LATEST%\*.reg") do (
  echo [restore] Importing %%~nxR
  reg import "%%R" >nul 2>&1
)

echo [restore] Stopping DesktopSuite (if running)...
taskkill /f /im DesktopSuite.exe >nul 2>&1

echo [restore] Restarting Explorer to reset desktop state...
taskkill /f /im explorer.exe >nul 2>&1
start "" explorer.exe

echo [restore] Done. Your desktop baseline has been restored.
pause

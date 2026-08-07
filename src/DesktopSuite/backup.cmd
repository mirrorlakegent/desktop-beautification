@echo off
setlocal EnableExtensions
:: Phase -1 baseline backup (L3 pure batch, no app required).
:: Mirrors DesktopSuite.Safety.BackupManager.RegKeys.
set "ROOT=%LOCALAPPDATA%\DesktopSuite\backups"

:: Timestamp dir (sanitized). Sortability is not required because restore.cmd
:: picks the newest by modification time, and the in-app restore sorts by
:: creation time.
set "STAMP=%DATE%_%TIME%"
set "STAMP=%STAMP:/=_%"
set "STAMP=%STAMP::=_%"
set "STAMP=%STAMP: =0%"
set "DIR=%ROOT%\%STAMP%"
mkdir "%DIR%" 2>nul

for %%K in (
  "HKEY_CURRENT_USER\Control Panel\Desktop"
  "HKEY_CURRENT_USER\Control Panel\Colors"
  "HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"
  "HKEY_CURRENT_USER\Software\Microsoft\Windows\Shell\Bags"
  "HKEY_CURRENT_USER\Software\Microsoft\Windows\Shell\BagMRU"
  "HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM"
  "HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"
) do (
  call :export "%%~K" "%DIR%"
)

echo Backup complete: %DIR%
goto :eof

:export
set "KEY=%~1"
set "BASE=%KEY:\=_%"
set "BASE=%BASE::=_%"
set "OUT=%~2\%BASE%.reg"
reg export "%KEY%" "%OUT%" /y >nul 2>&1
goto :eof

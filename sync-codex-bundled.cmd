@echo off
setlocal EnableExtensions EnableDelayedExpansion
chcp 65001 >nul
title Codex bundled plugin sync - do not close

set "EXE_PATH=%~dp0CodexPluginSync.exe"
if not exist "%EXE_PATH%" set "EXE_PATH=%~dp0build\bin\Release\CodexPluginSync.exe"

if exist "%EXE_PATH%" goto :DISPATCH_EXE

if "%CODEX_HOME%"=="" set "CODEX_HOME=%USERPROFILE%\CodexHome"
set "SCRIPT_DIR=%~dp0scripts"
set "SYNC_SCRIPT=%SCRIPT_DIR%\sync-openai-bundled.ps1"
set "CHECK_SCRIPT=%SCRIPT_DIR%\check-openai-bundled.ps1"
set "ROLLBACK_SCRIPT=%SCRIPT_DIR%\rollback-openai-bundled.ps1"
set "LOG_DIR=%CODEX_HOME%\logs"
set "CLEANUP_SCRIPT=%SCRIPT_DIR%\cleanup-openai-bundled.ps1"
set "KEEP_BACKUPS=1"
set "STALE_STAGE_HOURS=24"
set "KEEP_LOG_DAYS=30"
set "MIN_FREE_GB=1"
set "MODE=%~1"

if /i "%MODE%"=="--help" goto :MODE_HELP
if /i "%MODE%"=="/h" goto :MODE_HELP
if /i "%MODE%"=="-h" goto :MODE_HELP
if /i "%MODE%"=="--check" goto :MODE_CHECK
if /i "%MODE%"=="--cleanup-only" goto :MODE_CLEANUP
if /i "%MODE%"=="--rollback" goto :MODE_ROLLBACK
if not "%MODE%"=="" if /i not "%MODE%"=="--sync" goto :MODE_HELP
goto :MODE_SYNC

:DISPATCH_EXE
set "MODE=%~1"
if "%MODE%"=="" set "MODE=--sync"

if /i "%MODE%"=="--help" goto :EXE_HELP
if /i "%MODE%"=="/h" goto :EXE_HELP
if /i "%MODE%"=="-h" goto :EXE_HELP

if /i "%MODE%"=="--check" (
  echo ================================================================
  echo Codex bundled plugin check
  echo ================================================================
  "%EXE_PATH%" --check %2 %3 %4 %5 %6 %7 %8 %9
  set "EXE_EXIT=!ERRORLEVEL!"
  if "!EXE_EXIT!"=="2" set "EXE_EXIT=0"
  goto :EXE_DONE
)

if /i "%MODE%"=="--sync" (
  echo ================================================================
  echo Codex bundled plugin sync
  echo ================================================================
  "%EXE_PATH%" --sync %2 %3 %4 %5 %6 %7 %8 %9
  set "EXE_EXIT=!ERRORLEVEL!"
  goto :EXE_DONE
)

if /i "%MODE%"=="--rollback" (
  echo ================================================================
  echo Codex bundled plugin rollback
  echo ================================================================
  "%EXE_PATH%" --rollback %2 %3 %4 %5 %6 %7 %8 %9
  set "EXE_EXIT=!ERRORLEVEL!"
  goto :EXE_DONE
)

if /i "%MODE%"=="--cleanup-only" (
  echo ================================================================
  echo Codex bundled plugin cleanup
  echo ================================================================
  "%EXE_PATH%" --cleanup-only %2 %3 %4 %5 %6 %7 %8 %9
  set "EXE_EXIT=!ERRORLEVEL!"
  goto :EXE_DONE
)

"%EXE_PATH%" %*
set "EXE_EXIT=!ERRORLEVEL!"
goto :EXE_DONE

:EXE_HELP
echo.
echo Codex bundled plugin tools (EXE mode)
echo.
echo Usage:
echo   %~nx0                  Synchronize bundled plugins (default: --sync)
echo   %~nx0 --sync           Synchronize bundled plugins
echo   %~nx0 --check          Check versions, processes, disk space, and backup status
echo   %~nx0 --cleanup-only   Clean old backups, stages, and logs only
echo   %~nx0 --rollback       Restore the newest completed rollback backup
echo   %~nx0 --help           Show this help
echo.
set "EXE_EXIT=0"
goto :EXE_DONE

:EXE_DONE
echo.
pause
endlocal & exit /b %EXE_EXIT%

:MODE_HELP
echo.
echo Codex bundled plugin tools
echo.
echo Usage:
echo   %~nx0                  Synchronize bundled plugins (default)
echo   %~nx0 --sync           Synchronize bundled plugins
echo   %~nx0 --check          Check versions, processes, disk space, and backup status
echo   %~nx0 --cleanup-only   Clean old backups, stages, and logs only
echo   %~nx0 --rollback       Restore the newest completed rollback backup
echo   %~nx0 --help           Show this help
 echo.
echo Safety:
echo   Close Codex Desktop, Chrome, and Edge before sync or rollback.
echo   Current policy keeps %KEEP_BACKUPS% rollback backup and %KEEP_LOG_DAYS% days of logs.
echo.
pause
endlocal
exit /b 0

:MODE_CHECK
echo ================================================================
echo Codex bundled plugin check
echo ================================================================
if not exist "%CHECK_SCRIPT%" (
  echo ERROR: Check script was not found.
  echo %CHECK_SCRIPT%
  pause
  endlocal
  exit /b 1
)
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%CHECK_SCRIPT%" -Root "%CODEX_HOME%" -MinimumFreeGB %MIN_FREE_GB%
set "MODE_EXIT=!ERRORLEVEL!"
echo.
pause
endlocal & exit /b %MODE_EXIT%

:MODE_CLEANUP
echo ================================================================
echo Codex bundled plugin cleanup
echo ================================================================
if not exist "%CLEANUP_SCRIPT%" (
  echo ERROR: Cleanup script was not found.
  echo %CLEANUP_SCRIPT%
  pause
  endlocal
  exit /b 1
)
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%CLEANUP_SCRIPT%" -KeepBackups %KEEP_BACKUPS% -StaleStageHours %STALE_STAGE_HOURS% -KeepLogDays %KEEP_LOG_DAYS%
set "MODE_EXIT=!ERRORLEVEL!"
echo.
pause
endlocal & exit /b %MODE_EXIT%

:MODE_ROLLBACK
echo ================================================================
echo Codex bundled plugin rollback
echo ================================================================
if not exist "%ROLLBACK_SCRIPT%" (
  echo ERROR: Rollback script was not found.
  echo %ROLLBACK_SCRIPT%
  pause
  endlocal
  exit /b 1
)
echo This will restore the newest completed backup.
echo Make sure Codex Desktop, Chrome, and Edge are fully closed.
echo.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%ROLLBACK_SCRIPT%" -Root "%CODEX_HOME%"
set "MODE_EXIT=!ERRORLEVEL!"
echo.
if not "!MODE_EXIT!"=="0" echo ROLLBACK FAILED. Review the error above.
if "!MODE_EXIT!"=="0" echo ROLLBACK SUCCEEDED. Restart Codex Desktop before using it.
pause
endlocal & exit /b %MODE_EXIT%

:MODE_SYNC
if not exist "%LOG_DIR%" mkdir "%LOG_DIR%"
for /f %%I in ('powershell.exe -NoLogo -NoProfile -Command "Get-Date -Format yyyyMMdd-HHmmss"') do set "RUN_STAMP=%%I"
set "LOG_FILE=%LOG_DIR%\sync-openai-bundled-%RUN_STAMP%.log"

echo ================================================================
echo Codex bundled plugin sync
echo Started: %date% %time%
echo Log file: %LOG_FILE%
echo ================================================================
echo.

if not exist "%SYNC_SCRIPT%" (
  echo ERROR: Sync script was not found.
  echo %SYNC_SCRIPT%
  echo.
  pause
  endlocal
  exit /b 1
)

if not exist "%CHECK_SCRIPT%" (
  echo ERROR: Disk-check script was not found.
  echo %CHECK_SCRIPT%
  echo.
  pause
  endlocal
  exit /b 1
)

echo Checking free disk space before synchronization...
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%CHECK_SCRIPT%" -Root "%CODEX_HOME%" -DiskOnly -MinimumFreeGB %MIN_FREE_GB%
set "DISK_EXIT=!ERRORLEVEL!"
if not "!DISK_EXIT!"=="0" (
  echo ================================================================
  echo SYNC ABORTED. Disk-space preflight failed.
  echo Required minimum free space per drive: %MIN_FREE_GB% GB.
  echo ================================================================
  pause
  endlocal & exit /b !DISK_EXIT!
)

echo.
echo Starting sync. Detailed progress is printed below.
echo.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%SYNC_SCRIPT%" -Root "%CODEX_HOME%" -LogPath "%LOG_FILE%"
set "SYNC_EXIT=!ERRORLEVEL!"

echo.
if not "!SYNC_EXIT!"=="0" (
  echo ================================================================
  echo SYNC FAILED. Exit code: !SYNC_EXIT!
  echo Send this window or the log file to Codex:
  echo %LOG_FILE%
  echo ================================================================
  pause
  endlocal & exit /b !SYNC_EXIT!
)

echo.
echo Starting automatic cleanup...
if not exist "%CLEANUP_SCRIPT%" (
  echo WARNING: Cleanup script was not found. Skipping cleanup.
) else (
  powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%CLEANUP_SCRIPT%" -Root "%CODEX_HOME%" -KeepBackups %KEEP_BACKUPS% -StaleStageHours %STALE_STAGE_HOURS% -KeepLogDays %KEEP_LOG_DAYS%
  set "CLEANUP_EXIT=!ERRORLEVEL!"
  if not "!CLEANUP_EXIT!"=="0" echo WARNING: Cleanup failed, but synchronization succeeded. Review the paths above.
)

echo ================================================================
echo SYNC SUCCEEDED. Fully quit and restart Codex Desktop.
echo Full log: %LOG_FILE%
echo Cleanup policy: keep %KEEP_BACKUPS% backup, remove stages older than %STALE_STAGE_HOURS% hours, keep logs %KEEP_LOG_DAYS% days.
echo ================================================================
pause
endlocal
exit /b 0

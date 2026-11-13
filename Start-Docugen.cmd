@echo off
setlocal EnableExtensions
cd /d "%~dp0"

set "LOG=%~dp0startup-launcher.log"
echo ===== Docugen launcher %DATE% %TIME% =====> "%LOG%"
echo Dir: %CD%>> "%LOG%"
echo User: %USERNAME%>> "%LOG%"

REM Remove Mark-of-the-Web from downloaded files (otherwise Windows may block the EXE silently)
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "Get-ChildItem -LiteralPath '%~dp0' -File | Unblock-File -ErrorAction SilentlyContinue" >> "%LOG%" 2>&1

if not exist "%~dp0DocumentGenerator.exe" (
  echo ERROR: DocumentGenerator.exe not found in this folder.>> "%LOG%"
  echo DocumentGenerator.exe not found next to this script.
  echo See startup-launcher.log
  pause
  exit /b 1
)

echo EXE found.>> "%LOG%"
echo.>> "%LOG%"
echo --- files --->> "%LOG%"
dir /b "%~dp0" >> "%LOG%" 2>&1
echo.>> "%LOG%"

if not exist "%~dp0e_sqlite3.dll" (
  echo WARNING: e_sqlite3.dll missing.>> "%LOG%"
)
if not exist "%~dp0hostfxr.dll" if not exist "%~dp0coreclr.dll" (
  echo WARNING: runtime host DLLs not obvious — folder may be incomplete.>> "%LOG%"
)

echo Starting DocumentGenerator.exe ...>> "%LOG%"
echo Starting Docugen...

"%~dp0DocumentGenerator.exe" %*
set "ERR=%ERRORLEVEL%"

echo Exit code: %ERR%>> "%LOG%"
echo.>> "%LOG%"

if exist "%~dp0startup.log" (
  echo --- startup.log --->> "%LOG%"
  type "%~dp0startup.log" >> "%LOG%"
) else (
  echo startup.log was NOT created.>> "%LOG%"
  echo Managed code did not run. Possible causes:>> "%LOG%"
  echo   - incomplete folder ^(missing DLLs^)>> "%LOG%"
  echo   - antivirus quarantine>> "%LOG%"
  echo   - blocked downloaded file^(s^)>> "%LOG%"
  echo   - wrong CPU architecture>> "%LOG%"
  echo Check Event Viewer: Windows Logs - Application>> "%LOG%"
)

if not "%ERR%"=="0" (
  echo.
  echo Exit code %ERR%. See startup-launcher.log
  pause
) else if not exist "%~dp0startup.log" (
  echo.
  echo EXE returned 0 but startup.log is missing — still suspicious.
  echo See startup-launcher.log
  pause
)

endlocal
exit /b %ERR%

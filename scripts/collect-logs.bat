@echo off
setlocal
set DEST=%USERPROFILE%\Desktop\MasterApp_Logs.zip
set BASE=%LOCALAPPDATA%\MasterApp
set STAGE=%TEMP%\masterapp-logs-%RANDOM%%RANDOM%

if not exist "%BASE%\Logs" (
  echo Logs folder not found: %BASE%\Logs
  exit /b 1
)

if exist "%STAGE%" rmdir /s /q "%STAGE%" >nul 2>nul
mkdir "%STAGE%\Logs"

robocopy "%BASE%\Logs" "%STAGE%\Logs" /E /NJH /NJS /NFL /NDL /NP >nul
if errorlevel 8 (
  echo Failed to copy logs.
  rmdir /s /q "%STAGE%"
  exit /b %errorlevel%
)

if exist "%BASE%\State\runtime-state.json" copy /Y "%BASE%\State\runtime-state.json" "%STAGE%\runtime-state.json" >nul
if exist "%BASE%\State\settings.json" copy /Y "%BASE%\State\settings.json" "%STAGE%\settings.json" >nul

if exist "%DEST%" del /f /q "%DEST%" >nul 2>nul
tar -a -c -f "%DEST%" -C "%STAGE%" . >nul
set TAR_EXIT=%ERRORLEVEL%
rmdir /s /q "%STAGE%" >nul 2>nul
if not "%TAR_EXIT%"=="0" exit /b %TAR_EXIT%

echo.
echo Collected logs to:
echo %DEST%
echo.
echo Do NOT send secrets.json because it contains your tunnel token.
exit /b 0

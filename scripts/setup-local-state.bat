@echo off
setlocal
for %%I in ("%~dp0..") do set ROOT=%%~fI
set TEMPLATE_DIR=%ROOT%\templates\state
set STATE_DIR=%LOCALAPPDATA%\MasterApp\State
set FORCE=

if not "%~1"=="" (
  if /I "%~1"=="--force" (
    set FORCE=1
  ) else (
    set STATE_DIR=%~1
  )
)

if /I "%~2"=="--force" set FORCE=1

if not exist "%STATE_DIR%" mkdir "%STATE_DIR%"

call :copy_template settings.example.json settings.json
call :copy_template secrets.example.json secrets.json
call :copy_template runtime-state.example.json runtime-state.json

echo.
echo State folder ready:
echo %STATE_DIR%
echo.
echo Next step: edit settings.json and secrets.json with your own values.
exit /b 0

:copy_template
set SOURCE=%TEMPLATE_DIR%\%~1
set DEST=%STATE_DIR%\%~2

if exist "%DEST%" if not defined FORCE (
  echo Keeping existing %~2
  goto :eof
)

copy /Y "%SOURCE%" "%DEST%" >nul
echo Wrote %DEST%
goto :eof

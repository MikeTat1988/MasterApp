@echo off
setlocal
set SCRIPT_DIR=%~dp0
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%setup-lazy-masterapp.ps1"
if errorlevel 1 exit /b %errorlevel%
echo.
echo MasterApp lazy-local setup completed.
pause

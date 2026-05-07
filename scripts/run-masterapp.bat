@echo off
setlocal
for %%I in ("%~dp0..") do set ROOT=%%~fI
echo [run-masterapp] ROOT=%ROOT%
if /I not "%~1"=="--no-watchdog" (
  start "" /min powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%ROOT%\scripts\masterapp-watchdog.ps1" -Root "%ROOT%"
)
dotnet run --project "%ROOT%\src\MasterApp\MasterApp.csproj"

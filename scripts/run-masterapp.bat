@echo off
setlocal
for %%I in ("%~dp0..") do set ROOT=%%~fI
echo [run-masterapp] ROOT=%ROOT%
dotnet run --project "%ROOT%\src\MasterApp\MasterApp.csproj"

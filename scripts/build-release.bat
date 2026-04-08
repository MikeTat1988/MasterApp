@echo off
setlocal
for %%I in ("%~dp0..") do set ROOT=%%~fI
set OUT=%ROOT%\artifacts\release
if not exist "%OUT%" mkdir "%OUT%"
dotnet publish "%ROOT%\src\MasterApp\MasterApp.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -p:DebugType=None -p:NuGetAudit=false -o "%OUT%"
if errorlevel 1 exit /b %errorlevel%
echo.
echo Release build created in:
echo %OUT%

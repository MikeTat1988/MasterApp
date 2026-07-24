@echo off
setlocal
for %%I in ("%~dp0..\..") do set ROOT=%%~fI
set OUT=%ROOT%\artifacts\lazy-local\MasterApp-Lazy
if exist "%OUT%" rmdir /s /q "%OUT%"
mkdir "%OUT%"
dotnet publish "%ROOT%\src\MasterApp\MasterApp.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -p:DebugType=None -p:NuGetAudit=false -o "%OUT%"
if errorlevel 1 exit /b %errorlevel%
copy /Y "%ROOT%\distributions\lazy-local\Setup Lazy MasterApp.bat" "%OUT%\" >nul
copy /Y "%ROOT%\distributions\lazy-local\Start MasterApp Local.bat" "%OUT%\" >nul
copy /Y "%ROOT%\distributions\lazy-local\setup-lazy-masterapp.ps1" "%OUT%\" >nul
copy /Y "%ROOT%\distributions\lazy-local\README.md" "%OUT%\" >nul
copy /Y "%ROOT%\distributions\lazy-local\GPT_INSTRUCTIONS.md" "%OUT%\" >nul
copy /Y "%ROOT%\distributions\lazy-local\DRIVE_SETUP.md" "%OUT%\" >nul
echo.
echo Lazy-local distribution created in:
echo %OUT%

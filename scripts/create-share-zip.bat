@echo off
setlocal
for %%I in ("%~dp0..") do set ROOT=%%~fI
set OUTPUT_NAME=%~1
if "%OUTPUT_NAME%"=="" set OUTPUT_NAME=MasterApp-source.zip
for %%I in ("%OUTPUT_NAME%") do set OUTPUT_EXT=%%~xI
if "%OUTPUT_EXT%"=="" set OUTPUT_NAME=%OUTPUT_NAME%.zip
for %%I in ("%OUTPUT_NAME%") do set OUTPUT_BASE=%%~nI
for %%I in ("%OUTPUT_NAME%") do set OUTPUT_EXT=%%~xI

set OUTPUT_DIR=%ROOT%\artifacts\share
set ARCHIVE=%OUTPUT_DIR%\%OUTPUT_NAME%
set STAGE=%TEMP%\masterapp-share-%RANDOM%%RANDOM%%RANDOM%

if not exist "%OUTPUT_DIR%" mkdir "%OUTPUT_DIR%"
if exist "%ARCHIVE%" del /f /q "%ARCHIVE%" >nul 2>nul
if exist "%ARCHIVE%" set ARCHIVE=%OUTPUT_DIR%\%OUTPUT_BASE%-%RANDOM%%RANDOM%%OUTPUT_EXT%
if exist "%STAGE%" rmdir /s /q "%STAGE%" >nul 2>nul
mkdir "%STAGE%"

robocopy "%ROOT%" "%STAGE%" /E ^
  /XD "%ROOT%\.git" "%ROOT%\.codex" "%ROOT%\.idea" "%ROOT%\.vs" "%ROOT%\artifacts" "%ROOT%\.share-stage" "%ROOT%\.test-state" "%ROOT%\src\MasterApp\bin" "%ROOT%\src\MasterApp\obj" "%ROOT%\src\MasterApp\dist" ^
  /XF "*.zip" "*.exe" "*.msi" "*.tmp" "*.pdb" ^
  /NJH /NJS /NFL /NDL /NP >nul

if errorlevel 8 (
  echo Failed to stage files for the share zip.
  rmdir /s /q "%STAGE%"
  exit /b %errorlevel%
)

set "ZIP_STAGE=%STAGE%"
set "ZIP_DEST=%ARCHIVE%"
powershell -NoProfile -Command "Compress-Archive -Path \"$env:ZIP_STAGE\\*\" -DestinationPath \"$env:ZIP_DEST\" -CompressionLevel Optimal -Force" >nul
set TAR_EXIT=%ERRORLEVEL%
rmdir /s /q "%STAGE%" >nul 2>nul
if not "%TAR_EXIT%"=="0" exit /b %TAR_EXIT%

for %%I in ("%ARCHIVE%") do set ARCHIVE_SIZE=%%~zI

echo.
echo Created share zip:
echo %ARCHIVE%
echo Size: %ARCHIVE_SIZE% bytes
exit /b 0
